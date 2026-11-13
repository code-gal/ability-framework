namespace Ability.Sdk;

/// <summary>
/// 一次完整解密读取的敏感资料内存快照。
/// 内部持有解密后的明文字符数组缓冲；当调用方使用完毕并调用 <see cref="Dispose"/> 时，
/// 将使用安全清零方式清除所有字符内存并丢弃引用。
/// 谁调用读取谁就持有该快照所有权并负责释放；已释放后访问将抛出 <see cref="ObjectDisposedException"/>。
/// </summary>
public sealed class SensitiveDataSnapshot : IDisposable
{
    /// <summary>
    /// 存放明文字符数组的私有字典，键名区分大小写。
    /// </summary>
    private readonly Dictionary<string, char[]> _values;

    /// <summary>
    /// 标记快照是否已释放的原子状态标志（0 表示未释放，1 表示已释放）。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化敏感资料内存快照。
    /// </summary>
    /// <param name="version">快照对应的数据版本号。</param>
    /// <param name="values">解密得到的字段键值对集合。</param>
    internal SensitiveDataSnapshot(long version, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        Version = version;
        _values = values.ToDictionary(pair => pair.Key, pair => pair.Value.ToCharArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// 获取当前敏感资料的版本号。
    /// 版本号来自加密文件头部明文元数据，由底层 AES-GCM 附加认证数据（AAD）保护其真实完整性。
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// 取出指定名称的必填敏感字段文本。
    /// 若快照已被释放则抛出 <see cref="ObjectDisposedException"/>；若字段不存在则抛出 <see cref="KeyNotFoundException"/>。
    /// </summary>
    /// <param name="name">字段名称标识。</param>
    /// <returns>敏感字段文本副本；由调用方负责管理导出副本的生命周期与安全性。</returns>
    /// <exception cref="ObjectDisposedException">快照实例已被释放时抛出。</exception>
    /// <exception cref="KeyNotFoundException">指定字段在快照中不存在时抛出。</exception>
    public string GetRequiredString(string name)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"加密资料快照缺少字段 '{name}'。");
        }

        return new string(value);
    }

    /// <summary>
    /// 尝试取出指定名称的可选敏感字段文本。
    /// 若快照已被释放则抛出 <see cref="ObjectDisposedException"/>；若字段存在返回 true，否则返回 false 并将输出置为空字符串。
    /// </summary>
    /// <param name="name">字段名称标识。</param>
    /// <param name="value">成功匹配时输出字段文本副本，否则输出空字符串。</param>
    /// <returns>若字段存在返回 true；否则返回 false。</returns>
    /// <exception cref="ObjectDisposedException">快照实例已被释放时抛出。</exception>
    public bool TryGetString(string name, out string value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!string.IsNullOrWhiteSpace(name) && _values.TryGetValue(name, out var stored))
        {
            value = new string(stored);
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// 释放快照占用的敏感字符内存。
    /// 通过原子操作保证仅执行一次清零；遍历内部所有字符数组并使用 <see cref="Array.Clear(Array)"/> 擦除内容后清空字典。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var array in _values.Values)
        {
            Array.Clear(array);
        }

        _values.Clear();
    }
}
