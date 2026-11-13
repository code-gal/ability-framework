using System.Text;
using System.Threading.Channels;

namespace Ability.Hosting;

/// <summary>
/// 有界读取子进程输出流，防止管道缓冲占满导致进程挂起，并限制内存占用。
/// 回调分发通过独立有界通道异步解耦，杜绝外部回调阻塞流排空；
/// 支持实时读取当前已收集文本，避免超时丢弃已有诊断。
/// </summary>
internal sealed class BoundedProcessOutputReader
{
    /// <summary>单行最大字符数限制（64 KB）。</summary>
    private const int MaxLineLength = 64 * 1024;
    /// <summary>每次从流中读取的数据块大小（8 KB）。</summary>
    private const int ReadChunkSize = 8 * 1024;
    /// <summary>默认输出保留文本总量限制（256 KB）。</summary>
    private const int DefaultTextLimit = 256 * 1024;
    /// <summary>异步回调队列容量。</summary>
    private const int CallbackQueueCapacity = 256;

    /// <summary>底层流读取器。</summary>
    private readonly StreamReader _reader;
    /// <summary>单行文本到达时的外部回调。</summary>
    private readonly Action<string>? _lineCallback;
    /// <summary>保留文本上限。</summary>
    private readonly int _textLimit;

    /// <summary>输出队列与正在拼接行的同步锁。</summary>
    private readonly object _lock = new();
    /// <summary>已完成换行的行队列尾部。</summary>
    private readonly Queue<string> _tail = new();
    /// <summary>当前正在拼接的尚未换行文本。</summary>
    private readonly StringBuilder _currentLine = new();
    /// <summary>当前正在拼接的行是否已超长截断。</summary>
    private bool _currentLineTruncated;
    /// <summary>当前队列保存的总字符数。</summary>
    private int _totalLength;

    /// <summary>
    /// 初始化有界进程输出流读取器。
    /// </summary>
    /// <param name="reader">待读取的标准输出或错误流。</param>
    /// <param name="lineCallback">可选的逐行诊断回调。</param>
    /// <param name="textLimit">总文本保留字符数上限。</param>
    public BoundedProcessOutputReader(
        StreamReader reader,
        Action<string>? lineCallback = null,
        int textLimit = DefaultTextLimit)
    {
        _reader = reader;
        _lineCallback = lineCallback;
        _textLimit = textLimit;
        ReadTask = ReadInternalAsync();
    }

    /// <summary>
    /// 流排空读取异步任务。
    /// </summary>
    public Task<string> ReadTask { get; }

    /// <summary>
    /// 获取当前已读取到的有界文本快照。
    /// 将已完成行与正在拼接中的尚未换行内容一并返回，避免排空超时丢失诊断。
    /// </summary>
    /// <returns>当前已收集的有界文本。</returns>
    public string GetCurrentText()
    {
        lock (_lock)
        {
            if (_currentLine.Length == 0 && !_currentLineTruncated)
            {
                return string.Join(Environment.NewLine, _tail);
            }

            var partial = _currentLineTruncated ? _currentLine + "…[截断]" : _currentLine.ToString();
            if (_tail.Count == 0)
            {
                return partial;
            }

            return string.Join(Environment.NewLine, _tail.Append(partial));
        }
    }

    /// <summary>
    /// 内部异步流排空循环。
    /// </summary>
    private async Task<string> ReadInternalAsync()
    {
        var callbackChannel = _lineCallback is null ? null : CreateCallbackChannel();
        var callbackConsumerTask = callbackChannel is null ? null : StartCallbackConsumer(callbackChannel);

        try
        {
            await DrainReaderAsync(new char[ReadChunkSize], callbackChannel).ConfigureAwait(false);
        }
        finally
        {
            await CompleteCallbackChannelAsync(callbackChannel, callbackConsumerTask).ConfigureAwait(false);
        }

        return GetCurrentText();
    }

    /// <summary>
    /// 建立有界回调通道；满时丢弃最旧项，保证流排空顺畅。
    /// </summary>
    /// <returns>已建立的回调通道。</returns>
    private static Channel<string> CreateCallbackChannel()
    {
        return Channel.CreateBounded<string>(new BoundedChannelOptions(CallbackQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });
    }

    /// <summary>
    /// 启动回调消费者任务，逐项执行行回调并隔离外部回调异常。
    /// </summary>
    /// <param name="callbackChannel">回调通道。</param>
    /// <returns>消费者任务。</returns>
    private Task StartCallbackConsumer(Channel<string> callbackChannel)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (await callbackChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (callbackChannel.Reader.TryRead(out var item))
                    {
                        try
                        {
                            _lineCallback!(item);
                        }
                        catch
                        {
                            // 隔离外部回调异常，绝不向上抛出
                        }
                    }
                }
            }
            catch
            {
            }
        });
    }

    /// <summary>
    /// 读取循环：逐块读取并按行切分；读取异常时仍把未换行内容收尾，保留诊断。
    /// </summary>
    /// <param name="buffer">读取缓冲区。</param>
    /// <param name="callbackChannel">回调通道；未配置回调时为 null。</param>
    /// <returns>读取循环的异步完成任务。</returns>
    private async Task DrainReaderAsync(char[] buffer, Channel<string>? callbackChannel)
    {
        try
        {
            while (true)
            {
                var read = await _reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                AppendBuffer(buffer, read, callbackChannel);
            }

            FlushPendingLine(callbackChannel);
        }
        catch
        {
            FlushPendingLine(callbackChannel);
        }
    }

    /// <summary>
    /// 把本次读取的字符追加到当前行：换行完成一行，超长行置位截断标记。
    /// </summary>
    /// <param name="buffer">读取缓冲区。</param>
    /// <param name="read">本次读取的字符数。</param>
    /// <param name="callbackChannel">回调通道；未配置回调时为 null。</param>
    private void AppendBuffer(char[] buffer, int read, Channel<string>? callbackChannel)
    {
        for (var i = 0; i < read; i++)
        {
            var c = buffer[i];
            if (c == '\n')
            {
                CompleteLine(callbackChannel);
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            lock (_lock)
            {
                if (_currentLine.Length < MaxLineLength)
                {
                    _currentLine.Append(c);
                }
                else
                {
                    _currentLineTruncated = true;
                }
            }
        }
    }

    /// <summary>
    /// 在锁内把尚未换行的内容收尾为一行。
    /// </summary>
    /// <param name="callbackChannel">回调通道；未配置回调时为 null。</param>
    private void FlushPendingLine(Channel<string>? callbackChannel)
    {
        lock (_lock)
        {
            if (_currentLine.Length > 0 || _currentLineTruncated)
            {
                CompleteLine(callbackChannel);
            }
        }
    }

    /// <summary>
    /// 完成当前行：截断标记转文本、写入有界尾部缓冲，并在配置回调时非阻塞投递。
    /// </summary>
    /// <param name="callbackChannel">回调通道；未配置回调时为 null。</param>
    private void CompleteLine(Channel<string>? callbackChannel)
    {
        string text;
        lock (_lock)
        {
            text = _currentLineTruncated ? _currentLine + "…[截断]" : _currentLine.ToString();
            _currentLine.Clear();
            _currentLineTruncated = false;

            _tail.Enqueue(text);
            _totalLength += text.Length + 1;

            while (_totalLength > _textLimit && _tail.Count > 0)
            {
                _totalLength -= _tail.Dequeue().Length + 1;
            }
        }

        // 非阻塞写入，满时丢弃最旧项以保流排空顺畅
        callbackChannel?.Writer.TryWrite(text);
    }

    /// <summary>
    /// 关闭回调通道并在有限时限内等待消费者退出。
    /// </summary>
    /// <param name="callbackChannel">回调通道；未配置回调时为 null。</param>
    /// <param name="callbackConsumerTask">回调消费者任务；未启动时为 null。</param>
    /// <returns>收尾的异步完成任务。</returns>
    private static async Task CompleteCallbackChannelAsync(Channel<string>? callbackChannel, Task? callbackConsumerTask)
    {
        if (callbackChannel is null)
        {
            return;
        }

        callbackChannel.Writer.TryComplete();
        if (callbackConsumerTask is null)
        {
            return;
        }

        try
        {
            await callbackConsumerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
