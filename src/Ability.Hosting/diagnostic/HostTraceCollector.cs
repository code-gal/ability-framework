namespace Ability.Hosting;

/// <summary>
/// 每次调用的结构化诊断收集器。
/// 结构化记录与普通 stderr 文本各自具有固定容量上限，
/// 满载时自动淘汰最旧数据并统计丢弃量，确保宿主端收集绝对不会因为消费延迟而阻塞子进程。
/// </summary>
/// <param name="recordLimit">结构化记录最大条数，缺省为 4096 条。</param>
/// <param name="textLimit">普通 stderr 文本保留的最大字符总数，缺省为 256 KiB。</param>
internal sealed class HostTraceCollector(int recordLimit = 4096, int textLimit = 256 * 1024)
{
    /// <summary>
    /// 记录表与普通文本队列共用的同步互斥锁。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 收集到的结构化记录列表。
    /// </summary>
    private readonly List<HostTraceRecord> _records = [];

    /// <summary>
    /// 收集到的普通文本行队列。
    /// </summary>
    private readonly Queue<string> _plainLines = [];

    /// <summary>
    /// 普通文本行队列当前累计字符总长度。
    /// </summary>
    private int _plainLength;

    /// <summary>
    /// 因超限被淘汰的结构化记录总数。
    /// </summary>
    private long _droppedRecords;

    /// <summary>
    /// 因超限被淘汰的普通文本行总数。
    /// </summary>
    private long _droppedLines;

    /// <summary>
    /// 获取被逐出缓冲的结构化记录总数。
    /// </summary>
    internal long DroppedRecords => Interlocked.Read(ref _droppedRecords);

    /// <summary>
    /// 获取被逐出缓冲的普通文本行总数。
    /// </summary>
    internal long DroppedLines => Interlocked.Read(ref _droppedLines);

    /// <summary>
    /// 接收并处理一行 stderr 输出：
    /// 若成功解析为结构化记录则纳入记录表，否则作为普通文本保留有界尾部。
    /// </summary>
    /// <param name="line">待处理的单行文本。</param>
    internal void AddLine(string line)
    {
        if (line is null)
        {
            return;
        }

        HostTraceRecord? record = null;
        try
        {
            record = HostTraceRecord.TryParse(line);
        }
        catch
        {
            record = null;
        }

        lock (_gate)
        {
            if (record is not null)
            {
                if (_records.Count >= recordLimit)
                {
                    _records.RemoveAt(0);
                    Interlocked.Increment(ref _droppedRecords);
                }

                _records.Add(record);
                return;
            }

            _plainLines.Enqueue(line);
            _plainLength += line.Length + 1;
            while (_plainLength > textLimit && _plainLines.Count > 0)
            {
                _plainLength -= _plainLines.Dequeue().Length + 1;
                Interlocked.Increment(ref _droppedLines);
            }
        }
    }

    /// <summary>
    /// 获取当前收集到的全部结构化记录的快照副本，调用方可安全并发遍历。
    /// </summary>
    /// <returns>结构化记录数组。</returns>
    internal IReadOnlyList<HostTraceRecord> SnapshotRecords()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }
}
