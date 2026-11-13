using System.Text.Json.Serialization;

namespace Ability.Core;

/// <summary>
/// 框架 Core 协议模型自身的源生成 JSON 序列化上下文。
/// 保持在程序集内部实现，对外通过 <see cref="ProtocolMessages"/> 与 <see cref="IpcFrame"/> 提供编解码能力。
/// 业务模型由各业务方或调用方另行提供 <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(HelloBody))]
[JsonSerializable(typeof(HelloAcceptedBody))]
[JsonSerializable(typeof(ProtocolMismatchBody))]
[JsonSerializable(typeof(ChildStartBody))]
[JsonSerializable(typeof(ChildStartedBody))]
[JsonSerializable(typeof(ChildStopBody))]
[JsonSerializable(typeof(ChildReapedBody))]
[JsonSerializable(typeof(ChildMessageBody))]
[JsonSerializable(typeof(ChildMessageReplyBody))]
[JsonSerializable(typeof(TaskInputBody))]
[JsonSerializable(typeof(SubmitResultBody))]
[JsonSerializable(typeof(SubmitFaultBody))]
[JsonSerializable(typeof(RequestFaultBody))]
[JsonSerializable(typeof(LeaseRequestBody))]
[JsonSerializable(typeof(LeaseGrantedBody))]
[JsonSerializable(typeof(LeaseDeniedBody))]
[JsonSerializable(typeof(LeaseReleaseBody))]
[JsonSerializable(typeof(ParamRequestBody))]
[JsonSerializable(typeof(ParamValueBody))]
[JsonSerializable(typeof(SourceRepairBody))]
[JsonSerializable(typeof(SourceRepairResultBody))]
[JsonSerializable(typeof(RunPolicyBody))]
[JsonSerializable(typeof(OperationRegisterBody))]
[JsonSerializable(typeof(OperationAcceptedBody))]
[JsonSerializable(typeof(PolicyUpdateBody))]
[JsonSerializable(typeof(PolicyAppliedBody))]
[JsonSerializable(typeof(OperationStateBody))]
[JsonSerializable(typeof(OperationExpiredBody))]
[JsonSerializable(typeof(ResultOpenBody))]
[JsonSerializable(typeof(ResultItemBody))]
[JsonSerializable(typeof(ProgressBody))]
[JsonSerializable(typeof(ResultCreditBody))]
[JsonSerializable(typeof(ResultClosedBody))]
[JsonSerializable(typeof(DiagnosticRecord))]
internal partial class ProtocolJsonContext : JsonSerializerContext;
