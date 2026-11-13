using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 读取与原子更新单一加密敏感资料存储文件。
/// 文件来源与部署 bootstrap 密钥均需在向宿主申请的 Read 租约授权下访问。
/// 数据信封采用 AES-256-GCM 认证加密，保证头部元数据与明文载荷防篡改。
/// </summary>
public static partial class SensitiveDataStore
{
    /// <summary>
    /// 默认密钥提供者实现（从环境变量读取 Base64 编码的 32 字节密钥）。
    /// </summary>
    private static readonly ISensitiveKeyProvider DefaultKeyProvider = new EnvironmentSensitiveKeyProvider();

    /// <summary>
    /// 固定容量的路径写入互斥信号量桶，避免同路径并发写入造成磁盘交错或竞争损坏。
    /// </summary>
    private static readonly SemaphoreSlim[] ReplaceGates = Enumerable.Range(0, 32)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    /// <summary>
    /// 数据信封协议格式版本号。
    /// </summary>
    private const int FormatVersion = 1;

    /// <summary>
    /// AES-GCM 认证随机数（Nonce）字节长度（12 字节）。
    /// </summary>
    private const int NonceLength = 12;

    /// <summary>
    /// AES-GCM 认证标签（Tag）字节长度（16 字节）。
    /// </summary>
    private const int TagLength = 16;

    /// <summary>
    /// 信封头部总长度（4 字节魔数 + 1 字节版本 + 8 字节序列号 + 12 字节 Nonce + 16 字节 Tag + 4 字节密文长度 = 45 字节）。
    /// </summary>
    private const int HeaderLength = 4 + 1 + 8 + NonceLength + TagLength + 4;

    /// <summary>
    /// 附加认证数据（AAD）字节长度（头部前 13 字节：魔数 + 格式版本 + 版本号）。
    /// </summary>
    private const int AadLength = 13;

    /// <summary>
    /// 协议固定文件头部魔数 "SDS1"。
    /// </summary>
    private static ReadOnlySpan<byte> Magic => "SDS1"u8;

    /// <summary>
    /// 按敏感来源配置安全读取一次完整快照。
    /// 先后申请资料文件本身及密钥提供者的 Read 租约，在授权保障下解密并返回快照。
    /// 返回的快照由调用方持有所有权并在使用完毕后释放。
    /// </summary>
    /// <param name="source">敏感资料来源引用模型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="keyProvider">自定义密钥提供者，为 null 时使用默认环境变量提供者。</param>
    /// <returns>解密后的只读敏感资料快照。</returns>
    /// <exception cref="ArgumentNullException">source 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">source 配置格式无效时抛出。</exception>
    /// <exception cref="CryptographicException">密钥格式错误、信封损坏或认证标签不匹配时抛出。</exception>
    public static async Task<SensitiveDataSnapshot> ReadSnapshotAsync(
        SensitiveSourceRefMdl source,
        CancellationToken cancellationToken,
        ISensitiveKeyProvider? keyProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);

        var provider = keyProvider ?? DefaultKeyProvider;
        var keyLeaseRef = ValidateKeyLeaseReference(provider.GetReadLeaseReference(source.BootstrapKeyReference));

        // 1. 申请资料文件 Read 租约
        var sourceLease = await AbilityRuntime.LeaseAsync(source.SourceUri, LeaseMode.Read, cancellationToken).ConfigureAwait(false);
        using (sourceLease)
        // 2. 申请密钥提供者 Read 租约
        using (await AbilityRuntime.LeaseAsync(keyLeaseRef, LeaseMode.Read, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = await provider.ReadKeyAsync(source.BootstrapKeyReference, cancellationToken).ConfigureAwait(false);
            try
            {
                ValidateKey(key);
                var envelope = await ReadSharedDeleteAsync(SourcePath(source.SourceUri), cancellationToken).ConfigureAwait(false);
                var (version, values) = Decrypt(envelope, key);
                return new SensitiveDataSnapshot(version, values);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>
    /// 在当前技能会话受控环境中原子更新指定敏感来源文件。
    /// 必须先成功取得目标文件 Write 租约与密钥提供者 Read 租约，授权通过后再执行加密刷盘与原子替换。
    /// </summary>
    /// <param name="source">敏感资料来源引用模型。</param>
    /// <param name="version">新写入的数据版本号（非负数）。</param>
    /// <param name="values">敏感字段键值字典。</param>
    /// <param name="keyProvider">密钥提供者实例。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    /// <exception cref="ArgumentNullException">参数为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">版本号为负数时抛出。</exception>
    /// <exception cref="InvalidOperationException">目标路径无有效父级目录时抛出。</exception>
    /// <exception cref="LeaseDeniedException">写入租约被宿主拒绝时抛出。</exception>
    public static async Task ReplaceAsync(
        SensitiveSourceRefMdl source,
        long version,
        IReadOnlyDictionary<string, string> values,
        ISensitiveKeyProvider keyProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ValidateSource(source);

        var keyLeaseRef = ValidateKeyLeaseReference(keyProvider.GetReadLeaseReference(source.BootstrapKeyReference));

        // 1. 申请资料文件 Write 租约
        using (await AbilityRuntime.LeaseAsync(source.SourceUri, LeaseMode.Write, cancellationToken).ConfigureAwait(false))
        // 2. 申请密钥提供者 Read 租约
        using (await AbilityRuntime.LeaseAsync(keyLeaseRef, LeaseMode.Read, cancellationToken).ConfigureAwait(false))
        {
            await ReplaceInternalAsync(source, version, values, keyProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 脱机会话管理更新：供无会话的部署与管理工具在离线状态下执行初始化与原子更新。
    /// 明确声明其脱离技能会话的独立管理权限约束，不发起 IPC 租约申请。
    /// </summary>
    /// <param name="source">敏感资料来源引用模型。</param>
    /// <param name="version">新写入的数据版本号（非负数）。</param>
    /// <param name="values">敏感字段键值字典。</param>
    /// <param name="keyProvider">密钥提供者实例。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    public static Task ReplaceStandaloneAsync(
        SensitiveSourceRefMdl source,
        long version,
        IReadOnlyDictionary<string, string> values,
        ISensitiveKeyProvider keyProvider,
        CancellationToken cancellationToken)
    {
        if (AbilityRuntime.HasActiveSession)
        {
            throw new InvalidOperationException("当前调用上下文处于活跃技能会话中；在会话内更新敏感资料必须调用 ReplaceAsync 并申请租约，禁止使用 ReplaceStandaloneAsync 绕过治理管控。");
        }

        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ValidateSource(source);

        return ReplaceInternalAsync(source, version, values, keyProvider, cancellationToken);
    }

    /// <summary>
    /// 执行加密刷盘与原子替换的内部核心实现。
    /// </summary>
    private static async Task ReplaceInternalAsync(
        SensitiveSourceRefMdl source,
        long version,
        IReadOnlyDictionary<string, string> values,
        ISensitiveKeyProvider keyProvider,
        CancellationToken cancellationToken)
    {
        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "资料版本号必须为非负整数。");
        }

        var key = await keyProvider.ReadKeyAsync(source.BootstrapKeyReference, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateKey(key);
            var path = SourcePath(source.SourceUri);
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("资料来源路径缺少父级目录。");

            Directory.CreateDirectory(directory);

            var gateIndex = unchecked((int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)ReplaceGates.Length));
            var gate = ReplaceGates[gateIndex];

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bytes = Encrypt(version, values, key);
                var temporaryPath = Path.Combine(directory, ".sensitive-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (var stream = new FileStream(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     4096,
                                     FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    ReplaceAtomically(temporaryPath, path);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// 执行原子替换：若目标已存在使用 <see cref="File.Replace(string, string, string?, bool)"/>，
    /// 若首次创建则执行移动；若移动遇到并发竞争重新回退为替换。
    /// </summary>
    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temporaryPath, destinationPath);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
    }

    /// <summary>
    /// 加密字段字典并组装自描述数据信封。
    /// 无论加密成功或失败，明文缓冲区均通过 finally 保证安全清零擦除。
    /// </summary>
    private static byte[] Encrypt(long version, IReadOnlyDictionary<string, string> values, byte[] key)
    {
        var document = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, SensitiveJsonContext.Default.DictionaryStringString);

        try
        {
            var bytes = new byte[HeaderLength + plaintext.Length];
            Magic.CopyTo(bytes.AsSpan(0, 4));
            bytes[4] = FormatVersion;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(5, 8), version);

            var nonce = bytes.AsSpan(13, NonceLength);
            var tag = bytes.AsSpan(13 + NonceLength, TagLength);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(41, 4), plaintext.Length);
            RandomNumberGenerator.Fill(nonce);

            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Encrypt(nonce, plaintext, bytes.AsSpan(HeaderLength), tag, bytes.AsSpan(0, AadLength));
            }

            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// 校验信封标头完整性并执行 AES-GCM 认证解密。
    /// </summary>
    private static (long Version, Dictionary<string, string> Values) Decrypt(byte[] bytes, byte[] key)
    {
        if (bytes.Length < HeaderLength
            || !bytes.AsSpan(0, 4).SequenceEqual(Magic)
            || bytes[4] != FormatVersion)
        {
            throw new CryptographicException("加密资料信封版本或魔数头部无效。");
        }

        var version = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(5, 8));
        var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(41, 4));

        if (version < 0 || ciphertextLength < 0 || ciphertextLength != bytes.Length - HeaderLength)
        {
            throw new CryptographicException("加密资料信封密文长度或版本数值无效。");
        }

        var plaintext = new byte[ciphertextLength];
        try
        {
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(
                    bytes.AsSpan(13, NonceLength),
                    bytes.AsSpan(HeaderLength),
                    bytes.AsSpan(13 + NonceLength, TagLength),
                    plaintext,
                    bytes.AsSpan(0, AadLength));
            }

            var values = JsonSerializer.Deserialize(plaintext, SensitiveJsonContext.Default.DictionaryStringString)
                ?? throw new CryptographicException("加密资料反序列化内容为空。");

            return (version, values);
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("加密资料内容格式无效。", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// 校验密钥字节长度是否满足 AES-256 的 32 字节要求。
    /// </summary>
    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new CryptographicException("部署 bootstrap 密钥长度必须为 32 字节。");
        }
    }

    /// <summary>
    /// 校验敏感资料来源配置有效性。
    /// </summary>
    private static void ValidateSource(SensitiveSourceRefMdl source)
    {
        if (!Uri.TryCreate(source.SourceUri, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            throw new ArgumentException("当前加密资料实现仅支持本地 file URI 协议来源。", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(source.BootstrapKeyReference) || source.BootstrapKeyReference.Any(char.IsControl))
        {
            throw new ArgumentException("bootstrap 密钥引用为空或包含非法控制字符。", nameof(source));
        }
    }

    /// <summary>
    /// 将 file URI 解析转换为本地绝对文件路径。
    /// </summary>
    private static string SourcePath(string sourceUri) => new Uri(sourceUri, UriKind.Absolute).LocalPath;

    /// <summary>
    /// 校验密钥提供者返回的租约资源引用字符串。
    /// </summary>
    private static string ValidateKeyLeaseReference(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource) || resource.Any(char.IsControl))
        {
            throw new InvalidOperationException("密钥提供者返回了无效的来源租约引用。");
        }

        return resource;
    }

    /// <summary>
    /// 以允许删除共享模式异步读取文件整份字节流，应对原子替换期间可能发生的短暂文件访问抖动。
    /// </summary>
    private static async Task<byte[]> ReadSharedDeleteAsync(string path, CancellationToken cancellationToken)
    {
        FileStream source;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                break;
            }
            catch (FileNotFoundException) when (attempt < 6 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (attempt < 6
                && ex is not DirectoryNotFoundException
                && ex is not PathTooLongException
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        await using (source)
        {
            if (source.Length > int.MaxValue)
            {
                throw new IOException("加密资料来源文件超出单次内存读取最大限制。");
            }

            using var buffer = new MemoryStream((int)source.Length);
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// 敏感资料字典序列化源生成上下文。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    private sealed partial class SensitiveJsonContext : JsonSerializerContext;
}
