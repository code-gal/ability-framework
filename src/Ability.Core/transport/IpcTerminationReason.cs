namespace Ability.Core;

/// <summary>
/// IPC 连接终止原因分类。
/// </summary>
public enum IpcTerminationReason
{
    /// <summary>
    /// 正常协商完成后的优雅收尾。
    /// </summary>
    NormalClosed = 0,

    /// <summary>
    /// 本地调用方主动取消操作。
    /// </summary>
    CallerCancelled = 1,

    /// <summary>
    /// 对端主动发送停止或干净关闭连接。
    /// </summary>
    PeerStopped = 2,

    /// <summary>
    /// 底层数据流中途遭遇意外断开（非预期 EOF 或 I/O 错误）。
    /// </summary>
    Disconnected = 3,

    /// <summary>
    /// 帧长度超限、载荷非法、信封结构违规或反序列化失败导致的连接损坏。
    /// </summary>
    ProtocolCorrupted = 4
}
