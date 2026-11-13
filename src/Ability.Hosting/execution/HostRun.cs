using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 单次宿主运行的组件与资源所有者。
/// 持有本次调用创建的回调派发、治理协调、权威登记表、启动准备、任务专属目录、命名管道、IPC 连接、执行协调器与根控制信号，
/// 按准备、启动与握手、输入下发、执行、必要停止与处置、返回快照、后台收尾的顺序组织流程，并汇聚各组件提供的完成事实。
/// </summary>
internal sealed partial class HostRun
{
    /// <summary>记录的已观测消息类型最大条数。</summary>
    private const int MaxObservedMessages = 128;

    /// <summary>未预期失败后前台补齐收尾的有限预算（2 秒）；预算耗尽时剩余收尾由后台继续。</summary>
    private static readonly TimeSpan FailureCleanupBudget = TimeSpan.FromSeconds(2);

    /// <summary>调用请求参数。</summary>
    private readonly AbilityInvocation _invocation;

    /// <summary>运行时可调选项。</summary>
    private readonly HostRuntimeOptions _options;

    /// <summary>外部取消令牌。</summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>实际使用结束观察句柄。</summary>
    private readonly HostRunLifetime _lifetime;

    /// <summary>
    /// 前台收尾是否已完整执行。准备成功后、执行保护块之前失败时它仍为 false，
    /// 未预期失败的收尾安排因此会首次运行同一批收尾步骤补齐清理。
    /// </summary>
    private bool _foregroundCleanupCompleted;

    /// <summary>
    /// 后台收尾任务是否已调度。已调度时最终报告由该任务在实际使用者退出后发布，
    /// 未预期失败不再重复调度收尾。
    /// </summary>
    private bool _backgroundCleanupStarted;

    /// <summary>
    /// 根记录是否已进入根启动流程。未进入时根记录仍等待启动完成通知，
    /// 收尾须先按未启动回收，避免整树停止等待不会发生的启动结果。
    /// </summary>
    private bool _rootLaunchEntered;

    /// <summary>
    /// 未预期失败后仍在执行的前台收尾任务。后台收尾先等待它实际退出，再取得问题快照并继续等待其余使用者。
    /// </summary>
    private Task? _pendingForegroundCleanup;

    /// <summary>已预检并通过格式验证的输入 JSON 元素。</summary>
    private readonly JsonElement _inputPayload;

    /// <summary>本次运行的根调用键，同时用作根命名管道名称。</summary>
    private readonly string _rootKey = $"ci_{Guid.NewGuid():N}";

    /// <summary>本层收尾问题列表，随结果与最终报告公开。</summary>
    private readonly List<HostCleanupIssue> _issues = [];

    /// <summary>自然退出回收任务列表。</summary>
    private readonly List<Task> _naturalExitTasks = [];

    /// <summary>自然退出回收任务收集到的异常。</summary>
    private readonly List<HostCleanupIssue> _naturalExitIssues = [];

    /// <summary>自然退出回收任务列表与异常的保护锁。</summary>
    private readonly Lock _naturalExitLock = new();

    /// <summary>回调派发器。</summary>
    private HostCallbackDispatcher? _dispatcher;

    /// <summary>治理协调器。</summary>
    private HostGovernanceCoordinator? _governance;

    /// <summary>权威调用登记表。</summary>
    private InvocationRegistry? _registry;

    /// <summary>根调用记录。</summary>
    private InvocationRecord? _rootRecord;

    /// <summary>启动准备结果。</summary>
    private PreparedLauncher? _launcher;

    /// <summary>任务专属临时目录。</summary>
    private TaskTempDirectory? _tempDirectory;

    /// <summary>根命名管道服务端流。</summary>
    private NamedPipeServerStream? _pipeServer;

    /// <summary>子调用管理器。</summary>
    private HostChildCalls? _childCalls;

    /// <summary>根 IPC 连接。</summary>
    private IpcConnection? _connection;

    /// <summary>执行协调器。</summary>
    private HostExecutionCoordinator? _coordinator;

    /// <summary>整任务执行期限取消源，未启动进程时为 null。</summary>
    private CancellationTokenSource? _executionCts;

    /// <summary>执行期限与调用方令牌的关联取消源，未启动进程时为 null。</summary>
    private CancellationTokenSource? _linkedCts;

    /// <summary>本次运行的根 Activity。</summary>
    private Activity? _hostActivity;

    /// <summary>根调用记录是否已确认退出。</summary>
    private bool _rootExitConfirmed;

    /// <summary>根进程是否自然退出。</summary>
    private bool _rootExitedNaturally;

    /// <summary>后台收尾累计的问题列表：前台问题快照加上后台迟到问题。</summary>
    private List<HostCleanupIssue> _finalIssues = [];

    /// <summary>连接超时标记。</summary>
    private bool _connectTimedOut;

    /// <summary>握手超时标记。</summary>
    private bool _handshakeTimedOut;

    /// <summary>根进程是否已成功启动并完成登记。</summary>
    private bool _launchSucceeded;

    /// <summary>当前终态标识。</summary>
    private string _outcome = "Unknown";

    /// <summary>结果输出类型名。</summary>
    private string? _outputTypeName;

    /// <summary>一元结果输出元素。</summary>
    private JsonElement? _outputJson;

    /// <summary>已接收的流元素计数。</summary>
    private int _streamItemCount;

    /// <summary>故障种类。</summary>
    private string? _faultKind;

    /// <summary>故障说明。</summary>
    private string? _faultMessage;

    /// <summary>已观测消息类型。</summary>
    private List<string> _observedMessages = [];

    /// <summary>已观测消息是否截断。</summary>
    private bool _observedTruncated;

    /// <summary>执行协调器前台结果。</summary>
    private HostExecutionResult? _execResult;

    /// <summary>整树进程退出是否确认。</summary>
    private bool _exitConfirmed;

    /// <summary>已确认的根进程退出码。</summary>
    private int? _finalExitCode;

    /// <summary>治理资源是否完成结算。</summary>
    private bool _governanceResourcesReclaimed;

    /// <summary>前台临时目录报告。</summary>
    private TaskTempReport? _tempReport;

    /// <summary>追踪导出异常。</summary>
    private Exception? _traceExportError;

    /// <summary>收集到的 stderr 文本。</summary>
    private string _stderrOutput = string.Empty;

    /// <summary>准备阶段资源回收是否失败。</summary>
    private bool _preparationReleaseFailed;

    /// <summary>命名管道释放是否失败。</summary>
    private bool _pipeServerReleaseFailed;

    /// <summary>临时目录处置或释放是否失败。</summary>
    private bool _tempDirectoryReleaseFailed;

    /// <summary>连接关闭、释放或清理是否失败。</summary>
    private bool _connectionReleaseFailed;

    /// <summary>进程停止、迟到回收或输出观察是否失败。</summary>
    private bool _processReleaseFailed;

    /// <summary>子调用树停止或派生任务等待是否失败。</summary>
    private bool _childCallsReleaseFailed;

    /// <summary>治理中止、操作收口或租约结算是否失败。</summary>
    private bool _governanceReleaseFailed;

    /// <summary>回调派发排空或释放是否失败。</summary>
    private bool _dispatcherReleaseFailed;

    /// <summary>取消源释放是否失败。</summary>
    private bool _cancellationSourceReleaseFailed;

    /// <summary>后台收尾整体是否失败。</summary>
    private bool _hostLifetimeReleaseFailed;

    /// <summary>前台诊断工作是否结束。</summary>
    private bool _foregroundDiagnosticsCompleted;

    /// <summary>前台认定的其余受管工作是否结束。</summary>
    private bool _foregroundDeferredWorkCompleted;

    /// <summary>
    /// 建立一次运行的所有者。
    /// </summary>
    /// <param name="invocation">调用请求参数。</param>
    /// <param name="options">运行时可调选项。</param>
    /// <param name="cancellationToken">外部取消令牌。</param>
    /// <param name="lifetime">实际使用结束观察句柄。</param>
    /// <param name="inputPayload">已预检的输入 JSON 元素。</param>
    public HostRun(
        AbilityInvocation invocation,
        HostRuntimeOptions options,
        CancellationToken cancellationToken,
        HostRunLifetime lifetime,
        JsonElement inputPayload)
    {
        _invocation = invocation;
        _options = options;
        _cancellationToken = cancellationToken;
        _lifetime = lifetime;
        _inputPayload = inputPayload;
        _outputTypeName = invocation.OutputTypeName;
    }

    /// <summary>本次运行的回调派发器。</summary>
    private HostCallbackDispatcher Dispatcher => _dispatcher!;

    /// <summary>本次运行的治理协调器。</summary>
    private HostGovernanceCoordinator Governance => _governance!;

    /// <summary>本次运行的权威调用登记表。</summary>
    private InvocationRegistry Registry => _registry!;

    /// <summary>本次运行的根调用记录。</summary>
    private InvocationRecord RootRecord => _rootRecord!;

    /// <summary>Hosting 自有资源与进程处置是否全部实际释放成功。</summary>
    private bool OwnResourcesReleased =>
        !_pipeServerReleaseFailed
        && !_tempDirectoryReleaseFailed
        && !_connectionReleaseFailed
        && !_processReleaseFailed
        && !_childCallsReleaseFailed
        && !_governanceReleaseFailed
        && !_dispatcherReleaseFailed
        && !_cancellationSourceReleaseFailed
        && !_hostLifetimeReleaseFailed
        && _dispatcher?.HasDisposalFailure != true
        && _coordinator?.HasReleaseFailure != true
        && _governance?.HasReleaseFailure != true;

    /// <summary>
    /// 执行本次运行的全部阶段，返回前台结果快照。
    /// 准备失败按准备阶段的清理路径发布报告并重抛原异常；准备成功后的任何未预期异常，
    /// 都由本对象在有限预算内补齐收尾并交给同一后台收尾任务发布最终报告，原异常照原样抛出。
    /// </summary>
    /// <returns>前台返回时的结果快照。</returns>
    public async Task<AbilityRunResult> ExecuteAsync()
    {
        if (!TryPrepare(out var preparationError))
        {
            await HandlePreparationFailureAsync(preparationError!).ConfigureAwait(false);
        }

        try
        {
            return await RunPreparedAsync().ConfigureAwait(false);
        }
        catch (Exception executionFailure)
        {
            await ArrangeFailureCleanupAsync(executionFailure).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 运行准备成功后的全部阶段：建立子调用管理与根活动、启动与执行、前台收尾、前台快照与后台收尾。
    /// 建立子调用管理与根活动位于执行保护块之前，其失败不改变已创建资源的所有者，由调用方的失败收尾处理。
    /// </summary>
    /// <returns>前台返回时的结果快照。</returns>
    private async Task<AbilityRunResult> RunPreparedAsync()
    {
        StartChildCallManager();

        using var hostActivity = StartHostActivity();
        _hostActivity = hostActivity;

        try
        {
            await LaunchAndExecuteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await HandleCancellationAsync().ConfigureAwait(false);
        }
        catch (ProtocolException protocolError)
        {
            HandleProtocolException(protocolError);
        }
        catch (Exception executionError)
        {
            HandleUnexpectedException(executionError);
        }
        finally
        {
            await PerformForegroundCleanupAsync().ConfigureAwait(false);
        }

        await PublishForegroundResultAsync().ConfigureAwait(false);
        EvaluateForegroundFacts();

        // 前台快照包含受管操作耗时等取自借用依赖的事实，是前台最后一次使用借用依赖。
        // 必须先完成快照，再允许后台收尾发布完成信号，避免提供方据信号释放依赖后前台仍读取它。
        var result = BuildResult();
        StartBackgroundCleanup();
        return result;
    }

    /// <summary>
    /// 未预期失败后的收尾安排：补齐尚未执行的前台收尾，再交给同一后台收尾任务等待实际使用者与派生任务退出，
    /// 并由该任务发布最终报告。已安排后台收尾时不重复调度，只把问题记入最终报告。
    /// 安排本身失败只作记录，不覆盖调用方收到的原异常，也不提前完成使用结束信号。
    /// </summary>
    /// <param name="failure">导致运行提前结束的未预期异常。</param>
    /// <returns>收尾安排的异步完成任务。</returns>
    private async Task ArrangeFailureCleanupAsync(Exception failure)
    {
        if (_backgroundCleanupStarted)
        {
            // 报告快照已由后台收尾取得，问题经共用派发器进入最终报告，不再重复调度收尾。
            Dispatcher.RecordIssue("HostRun", failure.GetType().Name, DescribeSafeError(failure));
            return;
        }

        _issues.Add(new HostCleanupIssue("HostRun", failure.GetType().Name, DescribeSafeError(failure)));

        try
        {
            await CompleteForegroundCleanupAsync().ConfigureAwait(false);
            StartBackgroundCleanup();
        }
        catch (Exception arrangeFailure)
        {
            // 收尾任务本身无法安排：只记录事实，原异常继续向外抛出；
            // 没有发布者时使用结束信号保持待完成，如实表达实际使用尚未确认结束。
            Dispatcher.RecordIssue("HostRunCleanupArrange", arrangeFailure.GetType().Name, DescribeSafeError(arrangeFailure));
        }
    }

    /// <summary>
    /// 在有限前台预算内补齐尚未执行的前台收尾：先建立预算，再把可能同步阻塞的收尾安排成本次运行持有的任务，
    /// 前台只在该预算内等待。预算耗尽或收尾异常时任务保留给后台收尾等待其真实结束。
    /// </summary>
    /// <returns>收尾安排的异步完成任务。</returns>
    private async Task CompleteForegroundCleanupAsync()
    {
        if (_foregroundCleanupCompleted)
        {
            return;
        }

        using var finiteCts = new CancellationTokenSource(FailureCleanupBudget, _options.TimeProvider);
        var cleanupTask = Task.Run(PerformForegroundCleanupAsync);
        _pendingForegroundCleanup = cleanupTask;

        try
        {
            await cleanupTask.WaitAsync(finiteCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 预算耗尽或收尾异常：同一任务由后台收尾等待其结束，异常与迟到问题在彼处进入最终报告。
        }
    }

    /// <summary>
    /// 启动宿主根 Activity 并注入任务标识。
    /// </summary>
    /// <returns>已启动的根 Activity。</returns>
    private Activity StartHostActivity()
    {
        return new Activity("Ability.Run")
            .SetIdFormat(ActivityIdFormat.W3C)
            .AddTag("task.id", _invocation.TaskId)
            .Start();
    }

    /// <summary>
    /// 按取消来源分类终态，并保留已接收的局部事实。
    /// </summary>
    /// <returns>分类后的异步完成任务。</returns>
    private async Task HandleCancellationAsync()
    {
        if (_connectTimedOut)
        {
            _outcome = "ConnectTimeout";
            _faultKind = "ConnectTimeout";
            _faultMessage = $"管道连接超时（{_options.ConnectTimeout.TotalSeconds:F1} 秒）。";
        }
        else if (_handshakeTimedOut)
        {
            _outcome = "HandshakeTimeout";
            _faultKind = "HandshakeTimeout";
            _faultMessage = $"协议握手超时（{_options.HandshakeTimeout.TotalSeconds:F1} 秒）。";
        }
        else if (string.Equals(_faultKind, "OperationUnresponsive", StringComparison.Ordinal))
        {
            _outcome = "Fault";
        }
        else if (_cancellationToken.IsCancellationRequested)
        {
            _outcome = "Cancelled";
        }
        else if (_executionCts?.IsCancellationRequested == true)
        {
            _outcome = "ExecutionTimeout";
            _faultKind = "ExecutionTimeout";
            _faultMessage = $"任务执行超时（{_options.ExecutionTimeout?.TotalSeconds:F1} 秒）。";
        }
        else
        {
            _outcome = "ConsumerFault";
            _faultKind = "CallbackCancelled";
            _faultMessage = "消费回调触发了取消。";
        }

        await SendCancelOnStopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 记录协议异常对应的终态分类。
    /// </summary>
    /// <param name="protocolError">协议异常。</param>
    private void HandleProtocolException(ProtocolException protocolError)
    {
        if (_outcome == "Unknown")
        {
            _outcome = "ProtocolError";
        }

        _faultKind ??= "ProtocolViolation";
        _faultMessage ??= protocolError.Message;
    }

    /// <summary>
    /// 记录执行阶段未预期异常对应的终态分类。
    /// </summary>
    /// <param name="executionError">未预期异常。</param>
    private void HandleUnexpectedException(Exception executionError)
    {
        if (_outcome == "Unknown")
        {
            _outcome = "Disconnected";
            _faultKind ??= executionError.GetType().Name;
            _faultMessage = "通信或运行时发生异常。";
        }
    }

    /// <summary>
    /// 在前台返回前完成 stderr 采集、问题汇聚与前台完成事实的计算。
    /// </summary>
    /// <returns>采集与汇聚的异步完成任务。</returns>
    private async Task PublishForegroundResultAsync()
    {
        await CollectStderrAsync().ConfigureAwait(false);
        MergeCollectedIssues();
        _childCalls?.CloseRegistrations();
    }

    /// <summary>
    /// 采集根进程 stderr 文本，超时取当前已收集快照。
    /// </summary>
    /// <returns>采集的异步完成任务。</returns>
    private async Task CollectStderrAsync()
    {
        if (_rootRecord?.StandardErrorTask is { } stderrTask)
        {
            try
            {
                _stderrOutput = await stderrTask.WaitAsync(TimeSpan.FromSeconds(3), _options.TimeProvider).ConfigureAwait(false);
            }
            catch
            {
                _stderrOutput = _rootRecord.GetStderrSnapshot();
            }
        }
    }

    /// <summary>
    /// 汇聚追踪导出、连接清理、目录处置、自然退出、派发器与治理收集到的问题。
    /// </summary>
    private void MergeCollectedIssues()
    {
        if (_traceExportError is not null)
        {
            _issues.Add(new HostCleanupIssue("TraceExport", _traceExportError.GetType().Name, DescribeSafeError(_traceExportError)));
        }

        if (_connection?.CleanupFailure is { } connectionError)
        {
            // Core 连接把出站队列、同步原语、请求匹配与流状态的释放失败汇聚到 CleanupFailure，这里按释放事实处理。
            _issues.Add(new HostCleanupIssue("Connection", connectionError.GetType().Name, DescribeSafeError(connectionError)));
            _connectionReleaseFailed = true;
        }

        if (_tempReport is not null)
        {
            if (_tempReport.DeletionStatus == "Failed")
            {
                _issues.Add(new HostCleanupIssue("TaskTemp", "DeletionFailed", _tempReport.RetentionReason ?? "临时目录删除失败"));
            }

            if (!_tempReport.MarkerPersisted)
            {
                _issues.Add(new HostCleanupIssue("TaskTemp", "MarkerPersistFailed", _tempReport.MarkerError ?? "临时目录标记持久化失败"));
            }
        }

        lock (_naturalExitLock)
        {
            AddDistinct(_naturalExitIssues);
        }

        AddDistinct(Dispatcher.GetCapturedIssues());

        if (_governance is not null)
        {
            AddDistinct(_governance.GetCapturedIssues());
        }
    }

    /// <summary>
    /// 把来源问题列表中尚未记录的问题并入本层问题列表。
    /// </summary>
    /// <param name="source">来源问题列表。</param>
    private void AddDistinct(IReadOnlyList<HostCleanupIssue> source)
    {
        foreach (var issue in source)
        {
            if (!_issues.Contains(issue))
            {
                _issues.Add(issue);
            }
        }
    }

    /// <summary>
    /// 按组件完成事实与自有资源释放结果计算前台完成事实。
    /// </summary>
    private void EvaluateForegroundFacts()
    {
        var allRecords = _registry?.AllRecords ?? [RootRecord];
        bool allStreamsCompleted = allRecords.All(record =>
            (record.StandardOutputTask == null || record.StandardOutputTask.IsCompleted) &&
            (record.StandardErrorTask == null || record.StandardErrorTask.IsCompleted));

        _foregroundDiagnosticsCompleted = allStreamsCompleted && Dispatcher.IsDrained;

        bool coordinatorDrained = _coordinator == null || (_coordinator.IsDrained && _coordinator.WaitForPhysicalExitAsync().IsCompleted);
        bool childCallsDrained = _childCalls == null || _childCalls.IsAllDrained;
        bool governanceDrained = _governance == null || _governance.IsDrained;
        bool consumerDrained = _tempDirectory?.ActiveConsumerTask == null || _tempDirectory.ActiveConsumerTask.IsCompleted;
        bool naturalExitsCompleted;
        lock (_naturalExitLock)
        {
            naturalExitsCompleted = _naturalExitTasks.All(task => task.IsCompleted);
        }

        bool connectionReleased = _connection == null
            || (_connection.WaitClosedAsync().IsCompleted && _connection.DisposalCleanupTask is { IsCompleted: true });
        bool ownedResourcesReleased = connectionReleased
            && _pipeServer == null
            && _tempDirectory == null
            && _linkedCts == null
            && _executionCts == null;

        _foregroundDeferredWorkCompleted = _exitConfirmed
            && connectionReleased
            && naturalExitsCompleted
            && coordinatorDrained
            && childCallsDrained
            && governanceDrained
            && consumerDrained
            && ownedResourcesReleased;
    }

    /// <summary>
    /// 构造前台结果快照；快照不因迟到事实改写。
    /// </summary>
    /// <returns>前台结果快照。</returns>
    private AbilityRunResult BuildResult()
    {
        bool tempCompliant = IsTempCompliant(_tempReport);
        bool cleanupCompleted = _exitConfirmed
            && _governanceResourcesReclaimed
            && _foregroundDiagnosticsCompleted
            && _foregroundDeferredWorkCompleted
            && tempCompliant
            && OwnResourcesReleased;

        return new AbilityRunResult
        {
            BusinessOutcome = _execResult?.BusinessOutcome,
            ProcessExitCode = _finalExitCode,
            ProcessExitConfirmed = _exitConfirmed,
            GovernanceResourcesReclaimed = _governanceResourcesReclaimed,
            DiagnosticsCompleted = _foregroundDiagnosticsCompleted,
            DeferredWorkCompleted = _foregroundDeferredWorkCompleted,
            CleanupCompleted = cleanupCompleted,
            Lifetime = _lifetime,
            Issues = _issues.AsReadOnly(),
            Outcome = _outcome,
            OutputTypeName = _outputTypeName,
            OutputJson = _outputJson,
            StreamItemCount = _streamItemCount,
            FaultKind = _faultKind,
            FaultMessage = _faultMessage,
            TaskTemp = _tempReport?.Snapshot(),
            Diagnostics = _stderrOutput,
            ObservedMessageTypes = Array.AsReadOnly(_observedMessages.ToArray()),
            ObservedMessagesTruncated = _observedTruncated,
            Operations = _governance?.GetOperationSnapshots() ?? []
        };
    }

    /// <summary>
    /// 判断目录处置是否满足既定策略。
    /// </summary>
    /// <param name="report">目录报告；未创建目录时为 null。</param>
    /// <returns>满足策略或无需处置时为 true。</returns>
    private static bool IsTempCompliant(TaskTempReport? report)
    {
        return report is null
            || (report.Disposition is "RetainedByPolicy" or "Deleted"
                && report.ConsumptionStatus is not "Failed"
                && report.DeletionStatus != "Failed"
                && report.MarkerPersisted);
    }

    /// <summary>
    /// 获取对外部观察安全的异常简要描述。
    /// </summary>
    /// <param name="ex">异常对象。</param>
    /// <returns>安全的异常描述文本或异常类型名。</returns>
    private static string DescribeSafeError(Exception ex) =>
        ex is ISafeFaultDescription safe && !string.IsNullOrWhiteSpace(safe.SafeDescription)
            ? safe.SafeDescription
            : ex.GetType().Name;

    /// <summary>
    /// 记录已观测到的协议消息类型名称，超限时置位截断标记。
    /// </summary>
    /// <param name="messageType">消息类型名称。</param>
    private void RecordObservedMessage(string messageType)
    {
        if (_observedMessages.Count < MaxObservedMessages)
        {
            _observedMessages.Add(messageType);
        }
        else
        {
            _observedTruncated = true;
        }
    }

    /// <summary>
    /// 从入站消息通道中异步读取下一条协议信封。
    /// </summary>
    /// <param name="connection">IPC 双工通信连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取到的协议信封。</returns>
    private static async Task<IpcEnvelope> ReadNextEnvelopeAsync(IpcConnection connection, CancellationToken cancellationToken)
    {
        while (await connection.Inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (connection.Inbound.TryRead(out var envelope))
            {
                return envelope;
            }
        }

        throw new ProtocolException("连接在接收到预期帧前关闭。");
    }

    /// <summary>
    /// 根据任务执行最终产出结果映射对应的 IPC 连接终止原因。
    /// </summary>
    /// <param name="outcome">任务执行结果标识。</param>
    /// <returns>对应的 IPC 连接终止原因枚举。</returns>
    private static IpcTerminationReason ResolveTerminationReason(string outcome) => outcome switch
    {
        "Cancelled" or "ExecutionTimeout" or "ConnectTimeout" or "HandshakeTimeout" or "TaskInputSendTimeout" => IpcTerminationReason.CallerCancelled,
        "SubmitResult" or "ResultClose" => IpcTerminationReason.NormalClosed,
        "ProtocolMismatch" or "ProtocolError" => IpcTerminationReason.ProtocolCorrupted,
        _ => IpcTerminationReason.Disconnected
    };
}
