using System.Text.Json;

namespace Ability.Core;

/// <summary>
/// 协议握手请求体；由上游宿主向技能实例发送。
/// </summary>
/// <param name="ProtocolMajor">上游支持的协议主版本号。</param>
/// <param name="ResultCreditWindow">流式结果初始信用额度窗口大小。</param>
/// <param name="CleanupGracePeriod">取消或异常退出时的资源清理宽限期。</param>
/// <param name="BuildTimeout">源码构建或准备超时期限；默认为 2 分钟。</param>
/// <param name="ConnectionTimeout">管道连接建立超时期限；默认为 5 秒。</param>
public sealed record HelloBody(
    int ProtocolMajor,
    int ResultCreditWindow,
    TimeSpan CleanupGracePeriod,
    TimeSpan BuildTimeout = default,
    TimeSpan ConnectionTimeout = default);

/// <summary>
/// 协议握手接受确认体；由技能向宿主回显确认的主版本。
/// </summary>
/// <param name="ProtocolMajor">技能确认支持的协议主版本号。</param>
public sealed record HelloAcceptedBody(int ProtocolMajor);

/// <summary>
/// 协议版本不匹配拒绝答复体；当主版本不一致时由技能发送以拒绝接入。
/// </summary>
/// <param name="ExpectedMajor">技能期望的协议主版本号。</param>
/// <param name="ActualMajor">宿主实际提出的协议主版本号。</param>
public sealed record ProtocolMismatchBody(int ExpectedMajor, int ActualMajor);

/// <summary>
/// 首帧任务输入体；宿主在握手成功后交付一次，包含任务 ID、专属临时目录及业务输入。
/// </summary>
/// <param name="TaskId">全局唯一的任务标识。</param>
/// <param name="TaskTempPath">宿主为该任务分配的专属临时目录绝对路径。</param>
/// <param name="PayloadType">业务输入的类型名称提示。</param>
/// <param name="Payload">原始 JSON 形式的业务输入载荷。</param>
public sealed record TaskInputBody(
    string TaskId,
    string TaskTempPath,
    string PayloadType,
    JsonElement Payload);
