using System.Buffers;

namespace Ability.Core;

/// <summary>
/// 有界缓冲写出器。
/// 限制实际提交的写出量不超过指定上限，并限制单次工作空间申请不超过最大工作空间上限，
/// 从而在序列化超大对象时及早失败，避免分配巨大的中间缓冲区。
/// </summary>
internal sealed class BoundedBufferWriter : IBufferWriter<byte>
{
    /// <summary>
    /// 最大允许提交的字节数上限。
    /// </summary>
    private readonly int _limit;

    /// <summary>
    /// 最大允许申请的工作空间字节数上限。
    /// </summary>
    private readonly int _maxWorkspaceBytes;

    /// <summary>
    /// 当前底层可扩容缓冲区字节数组。
    /// </summary>
    private byte[] _buffer;

    /// <summary>
    /// 当前已提交写入的字节数。
    /// </summary>
    private int _written;

    /// <summary>
    /// 初始化有界缓冲写出器实例。
    /// </summary>
    /// <param name="limit">最大允许提交的写入字节数。</param>
    /// <param name="maxWorkspaceBytes">最大允许申请的工作空间字节数上限。</param>
    public BoundedBufferWriter(int limit, int maxWorkspaceBytes)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "写出上限必须为正数。");
        }

        if (maxWorkspaceBytes < limit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWorkspaceBytes), "工作空间上限不能小于写出限制。");
        }

        _limit = limit;
        _maxWorkspaceBytes = maxWorkspaceBytes;
        _buffer = new byte[Math.Min(limit, 64 * 1024)];
    }

    /// <summary>
    /// 初始化有界缓冲写出器实例（工作空间上限与写出上限对齐）。
    /// </summary>
    /// <param name="limit">最大允许提交的写入字节数。</param>
    public BoundedBufferWriter(int limit)
        : this(limit, limit)
    {
    }

    /// <summary>
    /// 获取当前已提交的字节数。
    /// </summary>
    public int WrittenCount => _written;

    /// <summary>
    /// 推进写出游标并检查是否超过载荷上限。
    /// </summary>
    /// <param name="count">已写入的字节数。</param>
    /// <exception cref="PayloadTooLargeException">当累计写入字节数超过上限时抛出。</exception>
    public void Advance(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "推进字节数不能为负。");
        }

        _written += count;
        if (_written > _limit)
        {
            throw new PayloadTooLargeException($"写出 {_written} 字节超过上限 {_limit} 字节。");
        }
    }

    /// <summary>
    /// 获取用于写入的内存切片。
    /// </summary>
    /// <param name="sizeHint">期望的最小空间建议。</param>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <summary>
    /// 获取用于写入的 Span 切片。
    /// </summary>
    /// <param name="sizeHint">期望的最小空间建议。</param>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>
    /// 导出当前已提交内容的字节数组。
    /// </summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

    /// <summary>
    /// 获取当前已提交内容的只读内存切片。
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// 满足写出器申请的空间需求。
    /// 若已提交字节数与本次申请之和超过最大工作空间限制，立即拒绝。
    /// </summary>
    private void Reserve(int sizeHint)
    {
        var required = (long)_written + Math.Max(sizeHint, 1);
        if (required > _maxWorkspaceBytes)
        {
            throw new PayloadTooLargeException(
                $"写出器申请 {required} 字节工作空间，超过上限 {_maxWorkspaceBytes} 字节。");
        }

        if (_buffer.Length >= required)
        {
            return;
        }

        var grown = Math.Max(required, (long)_buffer.Length * 2);
        Array.Resize(ref _buffer, (int)Math.Min(grown, _maxWorkspaceBytes));
    }
}
