using System.Text.Json;

namespace Ability.Hosting;

/// <summary>
/// Ability 宿主公开入口：校验调用参数、绑定使用结束句柄，并把本次调用交给单次运行对象。
/// 运行对象的组件所有权、阶段顺序与收尾事实见 <see cref="HostRun"/>。
/// </summary>
public static partial class AbilityHost
{
    /// <summary>
    /// 启动并运行一个 Ability 技能任务。内部创建 <see cref="HostRunLifetime"/> 句柄，成功返回时通过结果的 <see cref="AbilityRunResult.Lifetime"/> 提供观察入口。
    /// </summary>
    /// <param name="invocation">任务调用定义与输入载荷。</param>
    /// <param name="options">可选的宿主运行选项与时限配置。</param>
    /// <param name="cancellationToken">可选的取消令牌。</param>
    /// <returns>任务执行结果、进程退出码及收尾诊断。</returns>
    public static Task<AbilityRunResult> RunAsync(
        AbilityInvocation invocation,
        HostRuntimeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lifetime = new HostRunLifetime();
        return RunAsync(invocation, options, cancellationToken, lifetime);
    }

    /// <summary>
    /// 启动并运行一个 Ability 技能任务，使用调用方预先创建的 <see cref="HostRunLifetime"/> 句柄观察借用依赖的实际使用结束。
    /// </summary>
    /// <param name="invocation">任务调用定义与输入载荷。</param>
    /// <param name="options">可选的宿主运行选项与时限配置。</param>
    /// <param name="cancellationToken">可选的取消令牌。</param>
    /// <param name="lifetime">调用方创建的使用结束观察句柄，在借用外部依赖前独占绑定。</param>
    /// <returns>任务执行结果、进程退出码及收尾诊断。</returns>
    public static async Task<AbilityRunResult> RunAsync(
        AbilityInvocation invocation,
        HostRuntimeOptions? options,
        CancellationToken cancellationToken,
        HostRunLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!lifetime.TryBind())
        {
            throw new InvalidOperationException("HostRunLifetime 句柄已被绑定过，禁止重复绑定。");
        }

        var (effectiveOptions, inputPayload) = ValidateInvocation(lifetime, invocation, options, cancellationToken);

        HostRun run;
        try
        {
            run = new HostRun(invocation, effectiveOptions, cancellationToken, lifetime, inputPayload);
        }
        catch (Exception fatalEx)
        {
            // 运行对象未能建立：本次调用尚未创建任何资源使用，完成报告仍由入口发布，避免调用方无限等待。
            lifetime.Complete(BuildUnstartedReport(fatalEx));
            throw;
        }

        // 运行对象已接管完成报告的发布责任：入口不再依据 Completion 是否完成补发兜底报告。
        return await run.ExecuteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 构造运行对象未能建立时的失败报告：未创建任何本次资源，全部完成事实为 false。
    /// </summary>
    /// <param name="fatalError">建立运行对象失败的原异常。</param>
    /// <returns>完成报告。</returns>
    private static HostCleanupReport BuildUnstartedReport(Exception fatalError)
    {
        return new HostCleanupReport
        {
            ProcessExitConfirmed = false,
            GovernanceResourcesReclaimed = false,
            DiagnosticsCompleted = false,
            DeferredWorkCompleted = false,
            CleanupCompleted = false,
            TaskTemp = null,
            Issues = new List<HostCleanupIssue>
            {
                new HostCleanupIssue("RunCoreFatal", fatalError.GetType().Name, DescribeSafeError(fatalError))
            }.AsReadOnly()
        };
    }

    /// <summary>
    /// 接入前预检调用参数与输入载荷：创建任何本次资源之前完成校验，失败时发布全为 true 的清理报告并抛原异常。
    /// </summary>
    /// <param name="lifetime">已绑定的使用结束观察句柄。</param>
    /// <param name="invocation">任务调用定义与输入载荷。</param>
    /// <param name="options">可选的宿主运行选项与时限配置。</param>
    /// <param name="cancellationToken">可选的取消令牌。</param>
    /// <returns>生效的运行选项与解析后的输入 JSON 元素。</returns>
    private static (HostRuntimeOptions Options, JsonElement InputPayload) ValidateInvocation(
        HostRunLifetime lifetime,
        AbilityInvocation invocation,
        HostRuntimeOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.TaskId);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.InputTypeName);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.InputJson);

            var effectiveOptions = options ?? new HostRuntimeOptions();
            effectiveOptions.Validate();

            cancellationToken.ThrowIfCancellationRequested();

            return (effectiveOptions, ValidateAndParseInputPayload(invocation.InputJson, nameof(invocation)));
        }
        catch
        {
            lifetime.Complete(new HostCleanupReport
            {
                ProcessExitConfirmed = true,
                GovernanceResourcesReclaimed = true,
                DiagnosticsCompleted = true,
                DeferredWorkCompleted = true,
                CleanupCompleted = true,
                TaskTemp = null,
                Issues = Array.Empty<HostCleanupIssue>().AsReadOnly()
            });
            throw;
        }
    }
}
