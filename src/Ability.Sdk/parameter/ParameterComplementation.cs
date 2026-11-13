using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ability.Core;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// 负责参数补充（文本/结构化）及数据源恢复请求的编组、通信与模式校验。
/// 技能端复用 Core 的 <see cref="JsonSchemaValidator"/> 对请求声明的约束进行前置校验及对交付值的后置核验。
/// </summary>
internal static class ParameterComplementation
{
    /// <summary>
    /// 申请文本形式的补充参数。
    /// </summary>
    /// <param name="connection">当前 IPC 连接。</param>
    /// <param name="request">补参请求描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>宿主交付的文本参数值。</returns>
    public static async Task<string> RequestParamAsync(
        IpcConnection connection,
        ParamRequestMdl request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var envelope = BuildParamRequest(connection, request, payloadType: null);
        var response = await connection.RequestAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (string.Equals(response.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
        {
            var fault = ProtocolMessages.ReadBody<RequestFaultBody>(response);
            throw new AbilityRequestFaultException(fault.Kind, fault.Message, site: "ParamRequest");
        }

        if (!string.Equals(response.Type, ProtocolConstants.MessageTypes.ParamValue, StringComparison.Ordinal))
        {
            throw new ProtocolException($"补参请求收到意外答复 '{response.Type}'。");
        }

        var responseValue = ProtocolMessages.ReadBody<ParamValueBody>(response);
        ProtocolMessages.ValidateParamValueShape(responseValue);

        var value = responseValue.Value
            ?? throw new ProtocolException("文本补参答复缺少 value 字段。");

        if (request.Schema is { } schema)
        {
            ValidatePayload(schema, JsonSerializer.SerializeToElement(value, ParameterJsonContext.Default.String));
        }

        return value;
    }

    /// <summary>
    /// 申请结构化强类型补充参数。
    /// </summary>
    /// <typeparam name="T">期望解析的业务数据类型（模型或 DTO 等）。</typeparam>
    /// <param name="connection">当前 IPC 连接。</param>
    /// <param name="request">补参请求描述。</param>
    /// <param name="jsonTypeInfo">调用方提供的源生成类型元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反序列化后的业务数据实例。</returns>
    public static async Task<T> RequestParamAsync<T>(
        IpcConnection connection,
        ParamRequestMdl request,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var envelope = BuildParamRequest(connection, request, jsonTypeInfo.Type.Name);
        var response = await connection.RequestAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (string.Equals(response.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
        {
            var fault = ProtocolMessages.ReadBody<RequestFaultBody>(response);
            throw new AbilityRequestFaultException(fault.Kind, fault.Message, site: "ParamRequest");
        }

        if (!string.Equals(response.Type, ProtocolConstants.MessageTypes.ParamValue, StringComparison.Ordinal))
        {
            throw new ProtocolException($"类型化补参请求收到意外答复 '{response.Type}'。");
        }

        var responseValue = ProtocolMessages.ReadBody<ParamValueBody>(response);
        ProtocolMessages.ValidateParamValueShape(responseValue);

        if (responseValue.Payload is not { } payload
            || !string.Equals(responseValue.PayloadType, jsonTypeInfo.Type.Name, StringComparison.Ordinal))
        {
            throw new ProtocolException("类型化补参答复缺少匹配的 payloadType 或 payload。");
        }

        if (request.Schema is { } schema)
        {
            ValidatePayload(schema, payload);
        }

        return JsonSerializer.Deserialize(payload, jsonTypeInfo)
            ?? throw new ProtocolException("类型化补参载荷反序列化为空。");
    }

    /// <summary>
    /// 向宿主发起数据源失效恢复请求。
    /// </summary>
    /// <param name="connection">当前 IPC 连接。</param>
    /// <param name="sourceKind">数据源种类标识。</param>
    /// <param name="sourceRef">数据源的稳定引用标识。</param>
    /// <param name="failureReason">失败原因说明。</param>
    /// <param name="hotUpdateSupported">技能是否支持在当前进程内就地重读新数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>宿主返回的恢复仲裁动作。</returns>
    public static async Task<SourceRepairOutcome> RequestSourceRepairAsync(
        IpcConnection connection,
        string sourceKind,
        string sourceRef,
        string failureReason,
        bool hotUpdateSupported,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var requestId = connection.NextRequestId();
        var envelope = ProtocolMessages.CreateSourceRepair(
            requestId,
            new SourceRepairBody(sourceKind, sourceRef, failureReason, hotUpdateSupported));

        var response = await connection.RequestAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (string.Equals(response.Type, ProtocolConstants.MessageTypes.RequestFault, StringComparison.Ordinal))
        {
            var fault = ProtocolMessages.ReadBody<RequestFaultBody>(response);
            throw new AbilityRequestFaultException(fault.Kind, fault.Message, site: "SourceRepair");
        }

        if (!string.Equals(response.Type, ProtocolConstants.MessageTypes.SourceRepairResult, StringComparison.Ordinal))
        {
            throw new ProtocolException($"数据源恢复请求收到意外答复 '{response.Type}'。");
        }

        var result = ProtocolMessages.ReadBody<SourceRepairResultBody>(response);

        if (result.Outcome is not ("Reread" or "RestartRequired" or "Denied" or "Cancelled"))
        {
            throw new ProtocolException($"数据源恢复答复状态未定义：'{result.Outcome}'。");
        }

        var outcome = Enum.Parse<SourceRepairOutcome>(result.Outcome, ignoreCase: false);

        if (!hotUpdateSupported && outcome == SourceRepairOutcome.Reread)
        {
            throw new ProtocolException("数据源未声明支持热更新，不能答复 Reread。");
        }

        return outcome;
    }

    /// <summary>
    /// 校验补参请求参数并在发送前执行本地 Schema 语法解析。
    /// </summary>
    private static IpcEnvelope BuildParamRequest(
        IpcConnection connection,
        ParamRequestMdl request,
        string? payloadType)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        ArgumentNullException.ThrowIfNull(request.Hint);

        if (request.Schema is { } schema)
        {
            // 发送前先在本进程解析一次 Schema：关键字不支持当场抛出异常，杜绝向宿主发送无法判定的契约
            _ = JsonSchemaValidator.For(schema);
        }

        var requestId = connection.NextRequestId();
        return ProtocolMessages.CreateParamRequest(
            requestId,
            request.Key,
            request.Hint,
            payloadType,
            request.Schema,
            request.UiDescriptor);
    }

    /// <summary>
    /// 使用声明的 Schema 校验交付的数据；违规或未完成时抛出对应异常。
    /// </summary>
    private static void ValidatePayload(JsonElement schema, JsonElement payload)
    {
        SchemaValidationResult result;
        try
        {
            result = JsonSchemaValidator.For(schema).Validate(payload);
        }
        catch (SchemaValidationIncompleteException ex)
        {
            throw AbilityRequestFaultException.Reviewed("ParamValidationIncomplete", ex.Message, site: "ParamValidation");
        }

        if (!result.IsValid)
        {
            throw new AbilitySchemaViolationException(result.Summary);
        }
    }
}

/// <summary>
/// 参数补充模块专用的源生成 JSON 序列化上下文。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
internal sealed partial class ParameterJsonContext : JsonSerializerContext;

