using Ability.Core;

namespace Ability.Hosting;

/// <summary>
/// 宿主对单个已登记受管操作的动态运行视图。
/// 追踪操作状态流转、单调递增尝试次数、权威策略版本、结构化进度过滤与时间点度量。
/// 操作在收到 OperationRegister 时创建，由所属会话的后续状态与进度信封驱动更新。
/// </summary>
internal sealed class HostOperation
{
    /// <summary>
    /// 操作初始登记请求体元数据。
    /// </summary>
    private readonly OperationRegisterBody _registration;

    /// <summary>
    /// 时间提供者。
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 登记时刻的高精度单调时间戳。
    /// </summary>
    private readonly long _registeredTimestamp;

    /// <summary>
    /// 登记时刻的绝对 UTC 时间点。
    /// </summary>
    private readonly DateTimeOffset _registeredAt;

    /// <summary>
    /// 首次尝试启动的绝对时间点。
    /// </summary>
    private DateTimeOffset? _startedAt;

    /// <summary>
    /// 整体操作终态结束的绝对时间点。
    /// </summary>
    private DateTimeOffset? _endedAt;

    /// <summary>
    /// 累计发起的尝试次数。
    /// </summary>
    private int _attempts;

    /// <summary>
    /// 当前生命周期状态名称。
    /// </summary>
    private string _state = "Registered";

    /// <summary>
    /// 当前是否处于尝试执行中状态。
    /// </summary>
    private bool _attemptRunning;

    /// <summary>
    /// 最近一次采纳的单调递增进度序号。
    /// </summary>
    private long _lastProgressSequence;

    /// <summary>
    /// 最近采纳的最新有效进度数据体。
    /// </summary>
    private ProgressBody? _latestProgress;

    /// <summary>
    /// 同步互斥门控，确保状态更新与快照导出的一致性，防止跨版本字段混合。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 当前生效的权威运行策略内部字段。
    /// </summary>
    private RunPolicyBody _policy;

    /// <summary>
    /// 当前生效策略的来源标识内部字段。
    /// </summary>
    private string _policySource;

    /// <summary>
    /// 当前生效策略的版本号内部字段。
    /// </summary>
    private long _policyVersion;

    /// <summary>
    /// 操作终止或停止的具体原因内部字段。
    /// </summary>
    private string? _stopReason;

    /// <summary>
    /// 最近一次策略更新的裁决结果内部字段。
    /// </summary>
    private string? _lastPolicyUpdateOutcome;

    /// <summary>
    /// 最近一次策略更新的版本号内部字段。
    /// </summary>
    private long? _lastPolicyUpdateVersion;

    /// <summary>
    /// 最近一次策略更新的发起来源内部字段。
    /// </summary>
    private string? _lastPolicyUpdateSource;

    /// <summary>
    /// 初始化宿主受管操作运行视图。
    /// </summary>
    /// <param name="registration">技能侧上报的操作登记信封体。</param>
    /// <param name="policy">初始生效策略。</param>
    /// <param name="policySource">策略来源标识（如 HostOverride 或 SkillSuggested）。</param>
    /// <param name="policyVersion">初始策略版本号。</param>
    /// <param name="timeProvider">时间提供者。</param>
    public HostOperation(
        OperationRegisterBody registration,
        RunPolicyBody policy,
        string policySource,
        long policyVersion,
        TimeProvider timeProvider)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _registeredTimestamp = _timeProvider.GetTimestamp();
        _registeredAt = _timeProvider.GetUtcNow();
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policySource = policySource ?? "SkillSuggested";
        _policyVersion = policyVersion;
    }

    /// <summary>
    /// 当前生效的权威运行策略。
    /// </summary>
    public RunPolicyBody Policy { get { lock (_gate) return _policy; } }

    /// <summary>
    /// 当前生效策略的来源标识。
    /// </summary>
    public string PolicySource { get { lock (_gate) return _policySource; } }

    /// <summary>
    /// 当前生效策略的版本号。
    /// </summary>
    public long PolicyVersion { get { lock (_gate) return _policyVersion; } }

    /// <summary>
    /// 操作终止或停止的具体原因说明。
    /// </summary>
    public string? StopReason
    {
        get { lock (_gate) return _stopReason; }
        set { lock (_gate) _stopReason = value; }
    }

    /// <summary>
    /// 最近一次策略更新的裁决结果（未收到确认时为 null）。
    /// </summary>
    public string? LastPolicyUpdateOutcome { get { lock (_gate) return _lastPolicyUpdateOutcome; } }

    /// <summary>
    /// 最近一次策略更新的版本号。
    /// </summary>
    public long? LastPolicyUpdateVersion { get { lock (_gate) return _lastPolicyUpdateVersion; } }

    /// <summary>
    /// 最近一次策略更新的发起来源。
    /// </summary>
    public string? LastPolicyUpdateSource { get { lock (_gate) return _lastPolicyUpdateSource; } }

    /// <summary>
    /// 操作是否已进入终态。
    /// </summary>
    public bool IsEnded
    {
        get
        {
            lock (_gate)
            {
                return _endedAt is not null || _state is "Ended" or "Unresponsive" or "Stopped";
            }
        }
    }

    /// <summary>
    /// 采纳一次上报的进度数据。
    /// 仅在尝试运行期间且进度序号单调递增时采纳；丢弃序号倒退或重复的过期进度帧。
    /// </summary>
    /// <param name="progress">收到的结构化进度数据体。</param>
    /// <returns>若采纳成功返回 true；否则返回 false。</returns>
    public bool ApplyProgress(ProgressBody progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_gate)
        {
            if (!_attemptRunning
                || _state is "Ended" or "Unresponsive"
                || progress.Sequence <= _lastProgressSequence)
            {
                return false;
            }

            _lastProgressSequence = progress.Sequence;
            _latestProgress = progress;
            return true;
        }
    }

    /// <summary>
    /// 根据操作状态变更信封更新操作阶段与时间戳。
    /// </summary>
    /// <param name="messageType">消息类型标识（OperationStarted、AttemptEnded、OperationEnd 或 OperationUnresponsive）。</param>
    /// <param name="state">状态数据体。</param>
    public void ApplyState(string messageType, OperationStateBody state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            _attempts = Math.Max(_attempts, state.Attempts);
            _state = state.State;

            _stopReason = string.Equals(messageType, ProtocolConstants.MessageTypes.OperationStarted, StringComparison.Ordinal)
                ? null
                : state.Reason ?? _stopReason;

            var now = _timeProvider.GetUtcNow();
            if (string.Equals(messageType, ProtocolConstants.MessageTypes.OperationStarted, StringComparison.Ordinal))
            {
                _attemptRunning = true;
                _startedAt ??= now;
            }
            else if (string.Equals(messageType, ProtocolConstants.MessageTypes.AttemptEnded, StringComparison.Ordinal)
                     || string.Equals(messageType, ProtocolConstants.MessageTypes.OperationEnd, StringComparison.Ordinal)
                     || string.Equals(state.State, "Ended", StringComparison.Ordinal)
                     || string.Equals(state.State, "Unresponsive", StringComparison.Ordinal))
            {
                _attemptRunning = false;
                if (string.Equals(messageType, ProtocolConstants.MessageTypes.OperationEnd, StringComparison.Ordinal)
                    || string.Equals(state.State, "Ended", StringComparison.Ordinal)
                    || string.Equals(state.State, "Unresponsive", StringComparison.Ordinal))
                {
                    _endedAt = now;
                }
            }
        }
    }

    /// <summary>
    /// 记录来自技能端对策略更新的裁决确认结果。
    /// 仅当技能确认 Applied 时替换当前生效权威策略。
    /// </summary>
    /// <param name="policy">更新的目标策略体。</param>
    /// <param name="version">更新策略版本号。</param>
    /// <param name="source">更新来源说明。</param>
    /// <param name="outcome">技能裁决结果（如 Applied、Stale 等）。</param>
    public void ApplyPolicy(RunPolicyBody policy, long version, string source, string outcome)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (_gate)
        {
            _lastPolicyUpdateOutcome = outcome;
            _lastPolicyUpdateVersion = version;
            _lastPolicyUpdateSource = source;

            if (string.Equals(outcome, "Applied", StringComparison.Ordinal))
            {
                _policy = policy;
                _policyVersion = version;
                _policySource = source;
            }
        }
    }

    /// <summary>
    /// 记录一次策略更新的失败或超时结果。
    /// </summary>
    /// <param name="outcome">更新结果标识（如 "Rejected" 或 "UpdateTimeout"）。</param>
    /// <param name="version">目标策略版本号。</param>
    public void RecordPolicyUpdateOutcome(string outcome, long version)
    {
        lock (_gate)
        {
            _lastPolicyUpdateOutcome = outcome;
            _lastPolicyUpdateVersion = version;
        }
    }

    /// <summary>
    /// 当子进程退出且操作尚未正常结束时，执行宿主兜底强制收口。
    /// </summary>
    /// <param name="exitCode">子进程退出码。</param>
    /// <param name="exitedNaturally">是否自然退出。</param>
    /// <returns>若本次调用将操作收口返回 true；若操作此前已处于终态返回 false。</returns>
    public bool EndForProcessExit(int? exitCode, bool? exitedNaturally)
    {
        lock (_gate)
        {
            if (_endedAt is not null || _state is "Ended" or "Unresponsive")
            {
                return false;
            }

            _attemptRunning = false;
            _state = "Stopped";
            _stopReason = $"ProcessExited(exitCode={exitCode?.ToString() ?? "unknown"}, natural={exitedNaturally?.ToString() ?? "unknown"})";
            _endedAt = _timeProvider.GetUtcNow();
            return true;
        }
    }

    /// <summary>
    /// 导出当前操作的不可变只读状态快照。
    /// 严格在门控保护下读取全部字段，杜绝跨版本混合数据的脏快照。
    /// </summary>
    /// <returns>构造完成的不可变状态快照实例。</returns>
    public OperationSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(
                _registration.OperationKey,
                _registration.ParentOperationKey,
                _registration.Name,
                _registration.Purpose,
                _state,
                _attempts,
                _policyVersion,
                _policySource,
                _policy,
                _registeredAt,
                _startedAt,
                _endedAt,
                _timeProvider.GetElapsedTime(_registeredTimestamp),
                _stopReason,
                _latestProgress,
                _lastPolicyUpdateOutcome,
                _lastPolicyUpdateVersion,
                _lastPolicyUpdateSource);
        }
    }
}
