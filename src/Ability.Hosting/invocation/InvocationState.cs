namespace Ability.Hosting;

/// <summary>
/// 标识单个调用在宿主生命周期管理中的状态阶段。
/// 状态严格单向演进（允许跳过中间状态）：Registered → Starting → Running → StopRequested → Exited → Reaped。
/// </summary>
public enum InvocationState
{
    /// <summary>
    /// 调用已权威登记，管道名已分配，尚未启动底层进程。
    /// </summary>
    Registered,

    /// <summary>
    /// 正在准备启动可执行文件或构建产物。
    /// </summary>
    Starting,

    /// <summary>
    /// 进程已成功启动并绑定，正在正常运行。
    /// </summary>
    Running,

    /// <summary>
    /// 已收到停止请求，等待进程自行退出或触发强制终止。
    /// </summary>
    StopRequested,

    /// <summary>
    /// 实际操作系统进程已确认退出。
    /// </summary>
    Exited,

    /// <summary>
    /// 关联的资源、租约与产物目录已全部清理完成，进入终态。
    /// </summary>
    Reaped
}
