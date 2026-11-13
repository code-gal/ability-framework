using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 为加密资料来源所有者提供原子更新入口，复用自描述信封格式与同目录落盘原子替换机制。
/// </summary>
public static class EncryptedSensitiveFile
{
    /// <summary>
    /// 原子替换更新指定来源加密数据文件。
    /// 转发调用 <see cref="SensitiveDataStore.ReplaceAsync(SensitiveSourceRefMdl, long, IReadOnlyDictionary{string, string}, ISensitiveKeyProvider, CancellationToken)"/>。
    /// </summary>
    /// <param name="source">敏感资料来源引用模型。</param>
    /// <param name="version">新写入的数据版本号（非负数）。</param>
    /// <param name="values">敏感字段键值字典。</param>
    /// <param name="keyProvider">密钥提供者实例。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步替换操作的任务。</returns>
    public static Task ReplaceAsync(
        SensitiveSourceRefMdl source,
        long version,
        IReadOnlyDictionary<string, string> values,
        ISensitiveKeyProvider keyProvider,
        CancellationToken cancellationToken)
        => SensitiveDataStore.ReplaceAsync(source, version, values, keyProvider, cancellationToken);
}
