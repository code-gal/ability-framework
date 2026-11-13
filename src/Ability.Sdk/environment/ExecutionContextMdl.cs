using Ability.Role;
using Ability.Sdk;

namespace Ability.Model;

/// <summary>
/// 执行上下文模型，保存单次调用实际采用的取消信号、时间来源与进度接收端。
/// </summary>
public sealed partial class ExecutionContextMdl
{
    /// <summary>
    /// 取消令牌字段。
    /// </summary>
    private CancellationToken _cancellationToken;

    /// <summary>
    /// 时间提供者字段。
    /// </summary>
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>
    /// 进度汇报接收端字段。
    /// </summary>
    private IProgress<RunProgress>? _progress;

    /// <summary>
    /// 本次执行的取消令牌。
    /// </summary>
    public CancellationToken CancellationToken
    {
        get => _cancellationToken;
        init => _cancellationToken = value;
    }

    /// <summary>
    /// 本次执行的时间提供者。
    /// </summary>
    public required TimeProvider TimeProvider
    {
        get => _timeProvider;
        init => _timeProvider = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 进度汇报句柄。
    /// </summary>
    public IProgress<RunProgress>? Progress
    {
        get => _progress;
        init => _progress = value;
    }
}
