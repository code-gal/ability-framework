using System.Diagnostics;
using System.Text.Json;

namespace Ability.Hosting;

/// <summary>
/// 任务专属临时目录管理器，负责创建隔离目录、维护 task.owner.json 归属标记状态机并协调结果文件消费与目录安全回收。
/// </summary>
internal sealed class TaskTempDirectory : IAsyncDisposable
{
    /// <summary>
    /// 任务归属标记文件名称。
    /// </summary>
    private const string MarkerFileName = "task.owner.json";

    /// <summary>
    /// 归属标记文件格式版本号。
    /// </summary>
    private const int MarkerVersion = 2;

    /// <summary>
    /// 宿主所有者标识符。
    /// </summary>
    private const string OwnerKey = "ability-host";

    /// <summary>
    /// 活跃运行中状态标识。
    /// </summary>
    private const string StateActive = "active";

    /// <summary>
    /// 等待结果消费状态标识。
    /// </summary>
    private const string StatePending = "pending";

    /// <summary>
    /// 允许释放删除状态标识。
    /// </summary>
    private const string StateReleasing = "releasing";

    /// <summary>
    /// 归属无法确认状态标识。
    /// </summary>
    private const string StateUnconfirmed = "unconfirmed";

    /// <summary>
    /// 任务专属目录的绝对路径。
    /// </summary>
    private readonly string _path;

    /// <summary>
    /// 任务唯一标识符。
    /// </summary>
    private readonly string _taskId;

    /// <summary>
    /// 目录创建时间的 ISO-8601 UTC 字符串。
    /// </summary>
    private readonly string _createdUtc;

    /// <summary>
    /// 磁盘 task.owner.json 中最后一次成功持久化的标记状态。
    /// </summary>
    private string? _persistedState;

    /// <summary>
    /// 转存消费执行状态：NotRequested、Pending、Succeeded、Failed 或 TimedOut。
    /// </summary>
    private string _consumptionStatus = "NotRequested";

    /// <summary>
    /// 物理删除执行状态：NotRequested、Pending、Succeeded 或 Failed。
    /// </summary>
    private string _deletionStatus = "NotRequested";

    /// <summary>
    /// 本次临时目录处置事实：RetainedByPolicy、Deleted、ConsumerFailed、DeleteFailed、AwaitingProcessExit 或 OwnershipUnconfirmed。
    /// </summary>
    private string _disposition = "RetainedByPolicy";

    /// <summary>
    /// 保留原因说明；成功删除时为 null。
    /// </summary>
    private string? _retentionReason;

    /// <summary>
    /// 所有者标记文件是否已成功持久化到磁盘。
    /// </summary>
    private bool _markerPersisted;

    /// <summary>
    /// 若写入标记文件失败，记录具体的脱敏错误信息；成功时为 null。
    /// </summary>
    private string? _markerError;

    /// <summary>
    /// 在途活跃的转存消费与取消后台任务。
    /// </summary>
    private Task? _activeConsumerTask;

    /// <summary>
    /// 内部状态变更与标记写入同步锁。
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// 收尾操作单次执行保护锁。
    /// </summary>
    private readonly object _finalizeLock = new();

    /// <summary>
    /// 已登记且关联到该目录的子进程 PID 集合。
    /// </summary>
    private readonly HashSet<int> _processIds = [];

    /// <summary>
    /// 当前内存中的目录生命周期状态。
    /// </summary>
    private string _state = StateActive;

    /// <summary>
    /// 是否发生过进程登记失败导致归属无法确认。
    /// </summary>
    private bool _unconfirmed;

    /// <summary>
    /// 惰性初始化的单次收尾任务。
    /// </summary>
    private Task<TaskTempReport>? _finalizeTask;

    /// <summary>
    /// 释放标记，用于防止重复释放。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化任务专属临时目录管理器实例。
    /// </summary>
    /// <param name="path">临时目录路径。</param>
    /// <param name="taskId">任务唯一标识符。</param>
    private TaskTempDirectory(string path, string taskId)
    {
        _path = path;
        _taskId = taskId;
        _createdUtc = DateTimeOffset.UtcNow.ToString("O");
    }

    /// <summary>
    /// 任务专属临时目录的绝对路径。
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// 获取当前在途执行的转存消费与取消后台任务（若有）。
    /// </summary>
    public Task? ActiveConsumerTask => _activeConsumerTask;

    /// <summary>
    /// 临时目录收尾审计报告不可变快照。
    /// </summary>
    public TaskTempReport Report => BuildReport();

    /// <summary>
    /// 构建当前目录生命周期事实的不可变快照报告。
    /// </summary>
    /// <returns>不可变报告实例。</returns>
    public TaskTempReport BuildReport()
    {
        lock (_lock)
        {
            return new TaskTempReport
            {
                Path = _path,
                CreatedUtc = _createdUtc,
                State = _state,
                PersistedState = _persistedState,
                ConsumptionStatus = _consumptionStatus,
                DeletionStatus = _deletionStatus,
                Disposition = _disposition,
                RetentionReason = _retentionReason,
                MarkerPersisted = _markerPersisted,
                MarkerError = _markerError
            };
        }
    }

    /// <summary>
    /// 创建并初始化专属临时目录与归属标记。
    /// </summary>
    /// <param name="rootDirectory">自定义临时根目录路径；若为空则使用默认系统临时路径。</param>
    /// <param name="taskId">任务唯一标识符。</param>
    /// <returns>初始化完成的专属临时目录管理器实例。</returns>
    public static TaskTempDirectory Create(string? rootDirectory, string taskId)
    {
        var root = !string.IsNullOrWhiteSpace(rootDirectory)
            ? System.IO.Path.GetFullPath(rootDirectory)
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ability-tasks");

        Directory.CreateDirectory(root);

        var safeId = SanitizeTaskId(taskId);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dirPath = System.IO.Path.Combine(root, $"{safeId}-{suffix}");

        Directory.CreateDirectory(dirPath);

        var dir = new TaskTempDirectory(dirPath, taskId);
        dir.WriteMarkerCore();
        return dir;
    }

    /// <summary>
    /// 登记已启动的技能进程 PID。若写入标记失败则标记为 unconfirmed 并抛出 <see cref="TaskTempOwnershipException"/>。
    /// </summary>
    /// <param name="processId">已启动的技能进程 ID。</param>
    /// <exception cref="TaskTempOwnershipException">当持久化进程归属标记失败时抛出。</exception>
    public void RecordProcessId(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_processIds.Add(processId))
            {
                return;
            }

            if (WriteMarkerCore())
            {
                return;
            }

            _unconfirmed = true;
            _state = StateUnconfirmed;
            WriteMarkerCore();
            throw new TaskTempOwnershipException(
                $"技能进程 {processId} 未能登记到任务专属临时目录 {_path} 的归属标记；目录归属无法确认，必须保留，该进程必须回收。");
        }
    }

    /// <summary>
    /// 执行收尾流程：等待技能进程退出、将状态变更为 pending、调用消费者转存产物、写 releasing 状态并在取得明确消费许可后删除目录。
    /// 并发调用时共享并等待同一个收尾 Task。未配置消费者时按规约保留目录供事后人工查验。
    /// </summary>
    /// <param name="consumer">消费回调函数。</param>
    /// <param name="consumerTimeout">消费超时时限。</param>
    /// <param name="wholeTreeExitConfirmed">权威进程树退出是否已经确认。</param>
    /// <returns>收尾审计报告。</returns>
    public Task<TaskTempReport> FinalizeAsync(
        Func<string, CancellationToken, ValueTask>? consumer,
        TimeSpan consumerTimeout,
        bool wholeTreeExitConfirmed = true)
    {
        lock (_finalizeLock)
        {
            _finalizeTask ??= FinalizeCoreAsync(consumer, consumerTimeout, wholeTreeExitConfirmed);
            return _finalizeTask;
        }
    }

    /// <summary>
    /// 执行内部收尾核心逻辑，涵盖存活进程检查、状态推进、消费回调与目录删除。
    /// </summary>
    /// <param name="consumer">消费回调函数。</param>
    /// <param name="consumerTimeout">消费超时时限。</param>
    /// <param name="wholeTreeExitConfirmed">权威进程树退出是否已经确认。</param>
    /// <returns>收尾审计报告。</returns>
    private async Task<TaskTempReport> FinalizeCoreAsync(
        Func<string, CancellationToken, ValueTask>? consumer,
        TimeSpan consumerTimeout,
        bool wholeTreeExitConfirmed)
    {
        if (RetainUntilSafe(wholeTreeExitConfirmed) is { } blocked)
        {
            return blocked;
        }

        // 标记为等待消费状态
        SetState(StatePending);

        // 如果标记未能成功持久化到磁盘，按规约保留目录并报告原因
        if (!_markerPersisted)
        {
            return RetainBlocked("OwnershipUnconfirmed", $"所有者标记文件未能成功持久化到磁盘（{_markerError}），保留目录以供调查。");
        }

        // 未配置消费者时按规约保留临时目录供事后查验，不予删除
        if (consumer is null)
        {
            _disposition = "RetainedByPolicy";
            _consumptionStatus = "NotRequested";
            _deletionStatus = "NotRequested";
            return Retain("未配置结果消费转存回调，按规约保留任务专属临时目录以供事后查验。");
        }

        if (await RunConsumerAsync(consumer, consumerTimeout).ConfigureAwait(false) is { } consumptionFailure)
        {
            return consumptionFailure;
        }

        return DeleteAfterConsumption();
    }

    /// <summary>
    /// 在归属未确认、整树退出未确认或存在存活子进程时拒绝消费与删除。
    /// </summary>
    /// <param name="wholeTreeExitConfirmed">权威进程树退出是否已经确认。</param>
    /// <returns>需要保留时的报告；可以继续收尾时为 null。</returns>
    private TaskTempReport? RetainUntilSafe(bool wholeTreeExitConfirmed)
    {
        lock (_lock)
        {
            if (_unconfirmed)
            {
                return RetainBlocked("OwnershipUnconfirmed", "归属标记写入失败，目录归属无法确认，收尾拒绝删除目录。");
            }

            if (!wholeTreeExitConfirmed)
            {
                return RetainBlocked("AwaitingProcessExit", "进程树退出未完全确认，收尾拒绝消费或删除目录。");
            }

            foreach (var pid in _processIds)
            {
                if (IsProcessAlive(pid))
                {
                    return RetainBlocked("AwaitingProcessExit", $"子进程 {pid} 仍在运行，收尾拒绝删除目录。");
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 记录阻止消费与删除的处置取值，并返回保留报告。
    /// </summary>
    /// <param name="disposition">处置取值。</param>
    /// <param name="reason">保留原因说明。</param>
    /// <returns>保留处置报告。</returns>
    private TaskTempReport RetainBlocked(string disposition, string reason)
    {
        _disposition = disposition;
        _consumptionStatus = "NotRequested";
        _deletionStatus = "NotRequested";
        return Retain(reason);
    }

    /// <summary>
    /// 在有限期限内执行结果消费回调；超时或异常时触发异步取消并返回失败处置。
    /// </summary>
    /// <param name="consumer">结果消费回调。</param>
    /// <param name="consumerTimeout">消费超时时限。</param>
    /// <returns>消费失败时的保留报告；消费成功时为 null。</returns>
    private async Task<TaskTempReport?> RunConsumerAsync(Func<string, CancellationToken, ValueTask> consumer, TimeSpan consumerTimeout)
    {
        var cancelGate = new object();
        var cancelClosed = false;
        Task? cancelTask = null;
        var consumerCts = new CancellationTokenSource();

        void TriggerAsyncCancellation()
        {
            lock (cancelGate)
            {
                if (cancelClosed || cancelTask is not null)
                {
                    return;
                }

                cancelTask = Task.Run(() =>
                {
                    try { consumerCts.Cancel(); } catch (ObjectDisposedException) { }
                });
            }
        }

        using var timeoutTimer = new Timer(
            static state => ((Action)state!)(),
            (Action)TriggerAsyncCancellation,
            consumerTimeout,
            Timeout.InfiniteTimeSpan);

        var consumerToken = consumerCts.Token;
        var consumerTask = Task.Run(async () =>
        {
            await consumer(_path, consumerToken).ConfigureAwait(false);
        }, consumerToken);

        try
        {
            await consumerTask.WaitAsync(consumerTimeout).ConfigureAwait(false);
            _consumptionStatus = "Succeeded";
            return null;
        }
        catch (OperationCanceledException)
        {
            TriggerAsyncCancellation();
            return RetainConsumerFailure("TimedOut", $"消费回调执行超时（{consumerTimeout.TotalSeconds:F1} 秒）。");
        }
        catch (TimeoutException)
        {
            TriggerAsyncCancellation();
            return RetainConsumerFailure("TimedOut", $"消费回调执行超时（{consumerTimeout.TotalSeconds:F1} 秒）。");
        }
        catch (Exception ex)
        {
            TriggerAsyncCancellation();
            return RetainConsumerFailure("Failed", $"消费回调执行抛出异常: {ex.GetType().Name}。");
        }
        finally
        {
            timeoutTimer.Dispose();
            Task? finalCancelTask;
            lock (cancelGate)
            {
                cancelClosed = true;
                finalCancelTask = cancelTask;
            }

            var tasksToAwait = finalCancelTask is not null
                ? [consumerTask, finalCancelTask]
                : new[] { consumerTask };

            var waitAll = Task.WhenAll(tasksToAwait);
            _activeConsumerTask = waitAll.ContinueWith(static (t, state) =>
            {
                var c = (CancellationTokenSource)state!;
                try { c.Dispose(); } catch { }
                if (t.IsFaulted && t.Exception is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(t.Exception.GetBaseException()).Throw();
                }
            }, consumerCts, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 记录消费失败处置并返回保留报告；迟到回调继续由 <see cref="ActiveConsumerTask"/> 追踪。
    /// </summary>
    /// <param name="consumptionStatus">消费状态取值（TimedOut 或 Failed）。</param>
    /// <param name="reason">保留原因说明。</param>
    /// <returns>保留处置报告。</returns>
    private TaskTempReport RetainConsumerFailure(string consumptionStatus, string reason)
    {
        _disposition = "ConsumerFailed";
        _consumptionStatus = consumptionStatus;
        _deletionStatus = "NotRequested";
        return Retain(reason);
    }

    /// <summary>
    /// 消费成功后转为释放状态并删除目录；标记未持久化或删除失败时按实际状态报告。
    /// </summary>
    /// <returns>收尾审计报告。</returns>
    private TaskTempReport DeleteAfterConsumption()
    {
        SetState(StateReleasing);

        if (!_markerPersisted)
        {
            _disposition = "OwnershipUnconfirmed";
            _deletionStatus = "NotRequested";
            return Retain($"releasing 标记未能成功持久化（{_markerError}），保留目录以供调查。");
        }

        try
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }

            _deletionStatus = "Succeeded";
            _disposition = "Deleted";
            _state = StateReleasing;
            _retentionReason = null;
        }
        catch (Exception ex)
        {
            _disposition = "DeleteFailed";
            _deletionStatus = "Failed";
            return Retain($"删除临时目录失败: {ex.GetType().Name}。");
        }

        return BuildReport();
    }

    /// <summary>
    /// 清理遗留的任务专属临时目录。
    /// 仅当目录标记文件存在、版本与所有者匹配、状态为 releasing、且宿主进程与所有技能进程均已确认退出时，才执行物理删除。
    /// </summary>
    /// <param name="options">宿主运行时配置选项。</param>
    /// <returns>实际安全清理的目录数量。</returns>
    public static int CleanupLeftovers(HostRuntimeOptions options)
    {
        var root = !string.IsNullOrWhiteSpace(options.TaskTempRootDirectory)
            ? System.IO.Path.GetFullPath(options.TaskTempRootDirectory)
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ability-tasks");

        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cleaned = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!TryReadMarker(dir, out var marker, out var reason))
            {
                options.DiagnosticCallback?.Invoke($"[TaskTemp] 保留无法识别的遗留任务目录（{reason}）: {dir}");
                continue;
            }

            if (!string.Equals(marker.State, StateReleasing, StringComparison.Ordinal))
            {
                options.DiagnosticCallback?.Invoke($"[TaskTemp] 保留未处于 releasing 状态的遗留任务目录（当前状态: {marker.State}）: {dir}");
                continue;
            }

            if (marker.ProcessIds.Count == 0)
            {
                options.DiagnosticCallback?.Invoke($"[TaskTemp] 保留缺少技能进程记录的遗留任务目录: {dir}");
                continue;
            }

            var aliveChildPid = marker.ProcessIds.FirstOrDefault(IsProcessAlive);
            if (aliveChildPid != 0)
            {
                options.DiagnosticCallback?.Invoke($"[TaskTemp] 保留仍有技能进程 {aliveChildPid} 存活的遗留任务目录: {dir}");
                continue;
            }

            if (IsProcessAlive(marker.HostProcessId))
            {
                options.DiagnosticCallback?.Invoke($"[TaskTemp] 保留宿主进程 {marker.HostProcessId} 仍存活的遗留任务目录: {dir}");
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                cleaned++;
            }
            catch (Exception ex)
            {
                options.DiagnosticCallback?.Invoke($"[TaskTemp] 删除遗留任务目录失败（{ex.GetType().Name}: {ex.Message}）: {dir}");
            }
        }

        return cleaned;
    }

    /// <summary>
    /// 异步释放专属临时目录管理器，默认触发收尾操作（保留目录策略）。
    /// </summary>
    /// <returns>异步释放任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await FinalizeAsync(null, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    /// <summary>
    /// 尝试读取并解析指定目录下的任务所有者标记文件。
    /// </summary>
    /// <param name="dir">目录路径。</param>
    /// <param name="marker">解析成功的标记记录。</param>
    /// <param name="failureReason">失败原因说明。</param>
    /// <returns>成功读取并有效验证返回 true，否则返回 false。</returns>
    private static bool TryReadMarker(
        string dir,
        out TaskOwnerMarker marker,
        out string? failureReason)
    {
        marker = default;
        var markerPath = System.IO.Path.Combine(dir, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            failureReason = "缺少标记文件";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(markerPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("markerVersion", out var verProp) || verProp.GetInt32() != MarkerVersion)
            {
                failureReason = "标记版本不匹配";
                return false;
            }

            if (!root.TryGetProperty("ownerKey", out var ownerProp) || !string.Equals(ownerProp.GetString(), OwnerKey, StringComparison.Ordinal))
            {
                failureReason = "所有者标识不匹配";
                return false;
            }

            if (!root.TryGetProperty("hostProcessId", out var hostPidProp) || !hostPidProp.TryGetInt32(out var hostPid) || hostPid <= 0)
            {
                failureReason = "宿主进程标识缺失或非法";
                return false;
            }

            if (!root.TryGetProperty("state", out var stateProp) || stateProp.GetString() is not { } state
                || (state != StateActive && state != StatePending && state != StateReleasing && state != StateUnconfirmed))
            {
                failureReason = "标记状态缺失或非法";
                return false;
            }

            if (!root.TryGetProperty("processIds", out var pidsProp) || pidsProp.ValueKind != JsonValueKind.Array)
            {
                failureReason = "技能进程标识列表缺失或格式非法";
                return false;
            }

            var pids = new List<int>();
            foreach (var item in pidsProp.EnumerateArray())
            {
                if (item.TryGetInt32(out var pid) && pid > 0)
                {
                    pids.Add(pid);
                }
                else
                {
                    failureReason = "包含非法的技能进程标识";
                    return false;
                }
            }

            marker = new TaskOwnerMarker(MarkerVersion, hostPid, state, pids);
            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"标记文件读取失败: {ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    /// 记录目录保留原因并返回审计报告。
    /// </summary>
    /// <param name="reason">保留原因描述。</param>
    /// <returns>收尾审计报告。</returns>
    private TaskTempReport Retain(string reason)
    {
        lock (_lock)
        {
            _state = StatePending;
            _retentionReason = reason;
            return BuildReport();
        }
    }

    /// <summary>
    /// 更新当前生命周期状态并同步持久化到标记文件。
    /// </summary>
    /// <param name="newState">新生命周期状态。</param>
    private void SetState(string newState)
    {
        lock (_lock)
        {
            _state = newState;
            WriteMarkerCore();
        }
    }

    /// <summary>
    /// 写入归属标记文件原子操作，先写入临时文件后原子重命名覆盖。
    /// </summary>
    /// <returns>写入并覆盖成功返回 true，否则返回 false。</returns>
    private bool WriteMarkerCore()
    {
        lock (_lock)
        {
            try
            {
                var target = System.IO.Path.Combine(_path, MarkerFileName);
                var temp = target + ".tmp";

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("markerVersion", MarkerVersion);
                    writer.WriteString("taskId", _taskId);
                    writer.WriteNumber("hostProcessId", Environment.ProcessId);
                    writer.WriteString("ownerKey", OwnerKey);
                    writer.WriteString("createdUtc", _createdUtc);
                    writer.WriteString("state", _state);
                    writer.WriteStartArray("processIds");
                    foreach (var pid in _processIds)
                    {
                        writer.WriteNumberValue(pid);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                File.Move(temp, target, overwrite: true);
                _markerPersisted = true;
                _persistedState = _state;
                _markerError = null;
                return true;
            }
            catch (Exception ex)
            {
                _markerPersisted = false;
                _markerError = $"写入所有者标记失败: {ex.GetType().Name} - {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>
    /// 检测指定进程 ID 对应的进程是否仍在运行。
    /// </summary>
    /// <param name="pid">进程 ID。</param>
    /// <returns>进程存活返回 true，已退出或不存在返回 false。</returns>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 清理任务标识符中的非法字符，生成安全的文件系统路径段。
    /// </summary>
    /// <param name="taskId">原始任务标识符。</param>
    /// <returns>安全的文件系统路径段。</returns>
    private static string SanitizeTaskId(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return "task";
        }

        Span<char> buffer = stackalloc char[Math.Min(taskId.Length, 32)];
        var len = 0;
        foreach (var c in taskId)
        {
            if (len >= buffer.Length)
            {
                break;
            }

            buffer[len++] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }

        return len > 0 ? buffer[..len].ToString() : "task";
    }

    /// <summary>
    /// 任务归属标记只读元数据模型。
    /// </summary>
    /// <param name="Version">标记版本。</param>
    /// <param name="HostProcessId">宿主进程 ID。</param>
    /// <param name="State">标记状态。</param>
    /// <param name="ProcessIds">关联的技能进程 ID 列表。</param>
    private readonly record struct TaskOwnerMarker(
        int Version,
        int HostProcessId,
        string State,
        List<int> ProcessIds);
}
