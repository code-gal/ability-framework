using System.Text.Json.Serialization.Metadata;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 绑定当前进程与任务执行上下文的静态运行时 SDK 门面入口。
/// 业务代码直接调用此类提供的原语发起资源租约申请、补充参数、报告进度、受管操作登记与数据源恢复，
/// 无需在业务接口之间显式传递 IPC 连接对象。
/// </summary>
public static class AbilityRuntime
{
    /// <summary>
    /// 当前异步执行流绑定的活跃技能会话环境存储。
    /// </summary>
    private static readonly AsyncLocal<AbilitySession?> CurrentSession = new();

    /// <summary>
    /// 获取当前调用上下文是否存在活跃的技能会话。
    /// </summary>
    internal static bool HasActiveSession => CurrentSession.Value is not null;

    /// <summary>
    /// 向宿主申请指定受管资源的访问租约。
    /// 收到宿主批准返回可同步释放的 <see cref="AbilityLease"/> 句柄对象；被拒绝时抛出 <see cref="LeaseDeniedException"/>。
    /// </summary>
    /// <param name="resource">受管资源定位标识（如本地文件 file URI 或环境变量 env 引用）。</param>
    /// <param name="mode">租约访问模式（读、写或调用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务，其结果为取得的租约句柄。</returns>
    /// <exception cref="ArgumentException">资源标识为空或仅包含空白字符时抛出。</exception>
    /// <exception cref="LeaseDeniedException">宿主拒绝租约申请时抛出。</exception>
    /// <exception cref="AbilityRequestFaultException">宿主处理租约请求发生故障时抛出。</exception>
    public static Task<AbilityLease> LeaseAsync(
        string resource,
        LeaseMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return RequireSession().LeaseAsync(resource, mode, cancellationToken);
    }

    /// <summary>
    /// 以文本简写形式向宿主申请补充单个业务参数。
    /// </summary>
    /// <param name="key">参数键标识。</param>
    /// <param name="hint">提示文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>宿主交互交付的参数文本值。</returns>
    public static Task<string> RequestParamAsync(
        string key,
        string hint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(hint);

        return RequestParamAsync(new ParamRequestMdl { Key = key, Hint = hint }, cancellationToken);
    }

    /// <summary>
    /// 向宿主申请补充文本参数，支持携带前置 Schema 与交互 UI 描述符。
    /// </summary>
    /// <param name="request">补参请求完整配置模型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>宿主交付并通过模式校验的文本参数值。</returns>
    public static Task<string> RequestParamAsync(
        ParamRequestMdl request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequireSession().RequestParamAsync(request, cancellationToken);
    }

    /// <summary>
    /// 向宿主申请强类型结构化补充参数，支持通过 Schema 模式校验与源生成 JSON 强类型反序列化。
    /// </summary>
    /// <typeparam name="T">期望解析的业务数据类型（模型或 DTO 等）。</typeparam>
    /// <param name="request">补参请求完整配置模型。</param>
    /// <param name="jsonTypeInfo">源生成 JSON 类型元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反序列化后的业务数据实例。</returns>
    public static Task<T> RequestParamAsync<T>(
        ParamRequestMdl request,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        return RequireSession().RequestParamAsync(request, jsonTypeInfo, cancellationToken);
    }

    /// <summary>
    /// 从原任务输入通道向宿主申请补充强类型业务输入。
    /// </summary>
    /// <typeparam name="T">业务数据类型（模型或 DTO 等）。</typeparam>
    /// <param name="key">业务输入键标识。</param>
    /// <param name="hint">提示文本。</param>
    /// <param name="jsonTypeInfo">源生成 JSON 类型元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反序列化后的业务输入数据实例。</returns>
    public static Task<T> RequestInputAsync<T>(
        string key,
        string hint,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        return RequestParamAsync(
            new ParamRequestMdl { Key = key, Hint = hint },
            jsonTypeInfo,
            cancellationToken);
    }

    /// <summary>
    /// 向宿主发起数据源失效恢复请求。
    /// 由宿主决策就地重读、重启重试或终止，不将单项数据源异常升级为连接崩溃。
    /// </summary>
    /// <param name="sourceKind">数据源种类标识。</param>
    /// <param name="sourceRef">数据源稳定引用标识。</param>
    /// <param name="failureReason">失败原因说明。</param>
    /// <param name="hotUpdateSupported">技能是否支持就地重读新数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>宿主仲裁返回的恢复动作。</returns>
    public static Task<SourceRepairOutcome> RequestSourceRepairAsync(
        string sourceKind,
        string sourceRef,
        string failureReason,
        bool hotUpdateSupported,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        return RequireSession().RequestSourceRepairAsync(
            sourceKind,
            sourceRef,
            failureReason,
            hotUpdateSupported,
            cancellationToken);
    }

    /// <summary>
    /// 向宿主登记受管操作；在获得宿主确认策略前不会开始执行。
    /// </summary>
    /// <param name="name">操作人类可读名称。</param>
    /// <param name="purpose">操作业务目的说明。</param>
    /// <param name="suggestedPolicy">技能建议的运行策略模型。</param>
    /// <param name="cancellationToken">局部取消令牌。</param>
    /// <param name="timeProvider">时间提供者。</param>
    /// <param name="progress">本地进度观察者（可选）。</param>
    /// <param name="parent">父级受管操作（可选）。</param>
    /// <returns>表示异步操作的任务，其结果为初始化完成的受管操作实例。</returns>
    public static Task<ManagedOperation> RegisterOperationAsync(
        string name,
        string purpose,
        RunPolicyMdl suggestedPolicy,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        IProgress<RunProgress>? progress = null,
        ManagedOperation? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(suggestedPolicy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        suggestedPolicy.Validate();

        return RequireSession().RegisterOperationAsync(
            name,
            purpose,
            suggestedPolicy,
            cancellationToken,
            timeProvider,
            progress,
            parent);
    }

    /// <summary>
    /// 向宿主异步汇报指定受管操作的阶段进度。
    /// </summary>
    /// <param name="operationKey">受管操作唯一标识键。</param>
    /// <param name="progress">进度模型实例。</param>
    /// <returns>表示异步操作的任务。</returns>
    public static Task ReportProgressAsync(string operationKey, RunProgress progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentNullException.ThrowIfNull(progress);
        return RequireSession().ReportProgressAsync(operationKey, progress);
    }

    /// <summary>
    /// 建立当前线程执行流的会话上下文作用域。
    /// </summary>
    /// <param name="session">当前活动的技能会话实例。</param>
    /// <returns>作用域销毁句柄，释放时回滚上下文。</returns>
    internal static IDisposable Enter(AbilitySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var previous = CurrentSession.Value;
        CurrentSession.Value = session;
        return new SessionScope(previous);
    }

    /// <summary>
    /// 获取当前调用上下文绑定的活跃技能会话；未建立上下文时抛出明确异常。
    /// </summary>
    internal static AbilitySession RequireSession()
        => CurrentSession.Value
           ?? throw new InvalidOperationException("当前调用缺少活跃的技能会话上下文；AbilityRuntime 原语仅在 AbilitySession 内部有效。");

    /// <summary>
    /// 会话上下文还原作用域句柄。
    /// </summary>
    private sealed class SessionScope(AbilitySession? previous) : IDisposable
    {
        /// <summary>
        /// 还原上一级会话上下文环境。
        /// </summary>
        public void Dispose() => CurrentSession.Value = previous;
    }
}
