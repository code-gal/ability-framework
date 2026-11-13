using System.Security.Cryptography;

namespace Ability.Sdk;

/// <summary>
/// 按稳定引用获取部署注入的 AES-256 密钥的提供者契约。
/// 密钥本身不随模型或通信信封传递，调用方在使用完毕后负责将返回的密钥字节数组清零。
/// </summary>
public interface ISensitiveKeyProvider
{
    /// <summary>
    /// 获取该密钥引用在申请租约时所对应的资源 URI。
    /// 调用方在实际读取密钥前，必须对该资源取得只读租约。
    /// </summary>
    /// <param name="keyReference">密钥引用标识符。</param>
    /// <returns>租约资源标识 URI（如 env:///KEY_NAME）。</returns>
    string GetReadLeaseReference(string keyReference);

    /// <summary>
    /// 读取 32 字节的 AES-256 解密密钥。
    /// </summary>
    /// <param name="keyReference">密钥引用标识符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>32 字节的密钥数组；调用方负责在使用后安全清零。</returns>
    ValueTask<byte[]> ReadKeyAsync(string keyReference, CancellationToken cancellationToken);
}

/// <summary>
/// 默认敏感密钥提供者：将 key 引用解释为环境变量名，变量值必须为 Base64 编码的 32 字节 AES 密钥。
/// </summary>
public sealed class EnvironmentSensitiveKeyProvider : ISensitiveKeyProvider
{
    /// <summary>
    /// 返回转义后的 env:/// 格式租约引用，脱敏保护本地具体变量值。
    /// </summary>
    /// <param name="keyReference">环境变量名。</param>
    /// <returns>env:/// 前缀的租约资源标识。</returns>
    public string GetReadLeaseReference(string keyReference)
        => "env:///" + Uri.EscapeDataString(keyReference ?? string.Empty);

    /// <summary>
    /// 从环境变量读取并解码 32 字节密钥。
    /// </summary>
    public ValueTask<byte[]> ReadKeyAsync(string keyReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(keyReference))
        {
            throw new CryptographicException("Bootstrap key 引用为空。");
        }

        var encoded = Environment.GetEnvironmentVariable(keyReference);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new CryptographicException($"部署 bootstrap key 环境变量 '{keyReference}' 未配置。");
        }

        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException($"部署 bootstrap key 长度非法（必须为 32 字节，实际为 {key.Length} 字节）。");
            }

            return ValueTask.FromResult(key);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("部署 bootstrap key Base64 格式无效。", ex);
        }
    }
}
