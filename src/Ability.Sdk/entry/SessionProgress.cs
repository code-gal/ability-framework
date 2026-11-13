namespace Ability.Sdk;

/// <summary>
/// 会话级执行环境进度适配器，将未绑定到具体操作的阶段进度报告路由至会话通道。
/// </summary>
internal sealed class SessionProgress : IProgress<RunProgress>
{
    /// <summary>
    /// 当前宿主会话实例。
    /// </summary>
    private readonly AbilitySession _session;

    /// <summary>
    /// 初始化会话级进度适配器。
    /// </summary>
    /// <param name="session">所属技能会话。</param>
    public SessionProgress(AbilitySession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public void Report(RunProgress value)
    {
        if (value is not null)
        {
            _ = _session.ReportProgressAsync(string.Empty, value);
        }
    }
}
