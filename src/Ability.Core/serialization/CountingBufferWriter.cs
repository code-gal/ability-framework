using System.Buffers;

namespace Ability.Core;

/// <summary>
/// 专门用于公开计量的字节累计 IBufferWriter 实现。
/// 按需复用暂存区，并根据 IBufferWriter 的申请建议扩容以满足写出器契约；
/// 不施加硬上限拦截，从而为公开测量 API 忠实返回超限数据的真实完整大小。
/// 发送前的有限工作空间与超限及早拒绝逻辑由 BoundedBufferWriter 单独承担。
/// </summary>
internal sealed class CountingBufferWriter : IBufferWriter<byte>
{
    /// <summary>
    /// 重复复用的写入暂存区。
    /// </summary>
    private byte[] _scratch = new byte[16 * 1024];

    /// <summary>
    /// 累计写入的总字节数。
    /// </summary>
    private long _count;

    /// <summary>
    /// 获取累计写入的总字节数。
    /// </summary>
    public long Count => _count;

    /// <summary>
    /// 推进写出游标并累加计数。
    /// </summary>
    /// <param name="count">已写入的字节数。</param>
    public void Advance(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "推进字节数不能为负。");
        }

        _count += count;
    }

    /// <summary>
    /// 获取暂存内存切片。
    /// </summary>
    /// <param name="sizeHint">期望空间建议。</param>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureScratch(sizeHint);
        return _scratch;
    }

    /// <summary>
    /// 获取暂存 Span 切片。
    /// </summary>
    /// <param name="sizeHint">期望空间建议。</param>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureScratch(sizeHint);
        return _scratch;
    }

    /// <summary>
    /// 确保暂存区具有满足申请的最小容量。
    /// </summary>
    private void EnsureScratch(int sizeHint)
    {
        var required = sizeHint <= 0 ? 1 : sizeHint;
        if (_scratch.Length < required)
        {
            _scratch = new byte[Math.Max(_scratch.Length * 2, required)];
        }
    }
}
