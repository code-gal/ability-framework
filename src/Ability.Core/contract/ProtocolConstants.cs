namespace Ability.Core;

/// <summary>
/// 协议级常量定义；包含当前支持的协议主版本、大小限制及各消息类型标识。
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// 当前实现的 IPC 协议主版本号。
    /// </summary>
    public const int CurrentProtocolMajor = 3;

    /// <summary>
    /// 完整 UTF-8 JSON 信封的最大允许字节数（16 MiB），不包含 4 字节长度前缀。
    /// </summary>
    public const int MaxEnvelopeBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 信封内业务载荷的最大允许 UTF-8 字节数（1 MiB）。
    /// </summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>
    /// 编码业务载荷所需的最大工作空间字节数（6 MiB + 16 字节），用于限制序列化写出器的内存增长。
    /// </summary>
    public const int MaxEncodeWorkspaceBytes = 6 * MaxPayloadBytes + 16;

    /// <summary>
    /// 编码完整信封允许的最大工作空间字节数（128 MiB）。
    /// 依据 Utf8JsonWriter 写入字符串时的最坏转码空间申请规则推导：
    /// 信封最终输出上限为 16 MiB (MaxEnvelopeBytes)；当信封内包含极限长度字段（例如接近 16 MiB 文本）且已写出前置内容 W 时，
    /// Utf8JsonWriter 在目标属性名或字符串包含需转义字符（如控制字符或 Unicode 扩展）的最坏路径下，
    /// 会按单值字符数的最坏六倍加上固定格式开销（引号、冒号与前置逗号，至多 4 字节）申请暂存空间 (sizeHint = 6 * L + 4)。
    /// 由于 W + L &lt;= 16 MiB，最坏单次申请需求达到 W + 6 * L + 4 &lt;= W + L + 5 * L + 4 &lt;= 16 MiB + 5 * 16 MiB + 4 = 96 MiB + 4。
    /// 加上信封外层结构与 BoundedBufferWriter 二进制翻倍对齐裕量（96 MiB 向上对齐至 128 MiB），
    /// 工作空间上限设定为 128 MiB (128 * 1024 * 1024 字节)，
    /// 既能严格杜绝无界大内存申请，又确保合法的 16 MiB 极限信封（即使包含大比例转义字符）绝不会在序列化暂存阶段被误拒。
    /// </summary>
    public const int MaxEnvelopeWorkspaceBytes = 128 * 1024 * 1024;

    /// <summary>
    /// 协议消息类型标识常量。
    /// </summary>
    public static class MessageTypes
    {
        /// <summary>协议握手请求。</summary>
        public const string Hello = "Hello";

        /// <summary>协议握手接受确认。</summary>
        public const string HelloAccepted = "HelloAccepted";

        /// <summary>协议版本不匹配拒绝答复。</summary>
        public const string ProtocolMismatch = "ProtocolMismatch";

        /// <summary>任务输入分发消息。</summary>
        public const string TaskInput = "TaskInput";

        /// <summary>一元结果提交终态。</summary>
        public const string SubmitResult = "SubmitResult";

        /// <summary>终态业务或协议故障上报。</summary>
        public const string SubmitFault = "SubmitFault";

        /// <summary>针对具体请求的失败或拒绝答复。</summary>
        public const string RequestFault = "RequestFault";

        /// <summary>启动子调用进程请求。</summary>
        public const string ChildStart = "ChildStart";

        /// <summary>子调用进程已启动确认。</summary>
        public const string ChildStarted = "ChildStarted";

        /// <summary>停止子调用进程请求。</summary>
        public const string ChildStop = "ChildStop";

        /// <summary>子调用进程已回收确认通知。</summary>
        public const string ChildReaped = "ChildReaped";

        /// <summary>转发给子调用的内层信封消息。</summary>
        public const string ChildMessage = "ChildMessage";

        /// <summary>子调用转发消息的答复信封。</summary>
        public const string ChildMessageReply = "ChildMessageReply";

        /// <summary>受管资源租约申请请求。</summary>
        public const string LeaseRequest = "LeaseRequest";

        /// <summary>受管资源租约批准答复。</summary>
        public const string LeaseGranted = "LeaseGranted";

        /// <summary>受管资源租约拒绝答复。</summary>
        public const string LeaseDenied = "LeaseDenied";

        /// <summary>受管资源租约释放通知。</summary>
        public const string LeaseRelease = "LeaseRelease";

        /// <summary>参数补充申请请求。</summary>
        public const string ParamRequest = "ParamRequest";

        /// <summary>参数补充值交付答复。</summary>
        public const string ParamValue = "ParamValue";

        /// <summary>数据源恢复请求。</summary>
        public const string SourceRepair = "SourceRepair";

        /// <summary>数据源恢复结果答复。</summary>
        public const string SourceRepairResult = "SourceRepairResult";

        /// <summary>协同取消通知。</summary>
        public const string Cancel = "Cancel";

        /// <summary>安全退出通知。</summary>
        public const string Shutdown = "Shutdown";

        /// <summary>开启结果流声明通知。</summary>
        public const string ResultOpen = "ResultOpen";

        /// <summary>结果流单个元素通知。</summary>
        public const string ResultItem = "ResultItem";

        /// <summary>关闭结果流通知。</summary>
        public const string ResultClose = "ResultClose";

        /// <summary>流元素背压发送额度通知。</summary>
        public const string ResultCredit = "ResultCredit";

        /// <summary>结果流结束汇总对账通知。</summary>
        public const string ResultClosed = "ResultClosed";

        /// <summary>受管操作登记请求。</summary>
        public const string OperationRegister = "OperationRegister";

        /// <summary>受管操作登记接受答复。</summary>
        public const string OperationAccepted = "OperationAccepted";

        /// <summary>受管操作策略动态更新通知。</summary>
        public const string PolicyUpdate = "PolicyUpdate";

        /// <summary>受管操作策略更新应用确认。</summary>
        public const string PolicyApplied = "PolicyApplied";

        /// <summary>受管操作状态变更通知。</summary>
        public const string OperationState = "OperationState";

        /// <summary>受管操作尝试启动通知。</summary>
        public const string OperationStarted = "OperationStarted";

        /// <summary>受管操作尝试结束通知。</summary>
        public const string AttemptEnded = "AttemptEnded";

        /// <summary>受管操作整体结束通知。</summary>
        public const string OperationEnd = "OperationEnd";

        /// <summary>受管操作到期通知。</summary>
        public const string OperationExpired = "OperationExpired";

        /// <summary>受管操作进度上报通知。</summary>
        public const string Progress = "Progress";
    }
}
