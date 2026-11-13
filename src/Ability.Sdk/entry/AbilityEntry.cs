using System.Text.Json.Serialization.Metadata;
using Ability.Core;
using Ability.Model;

namespace Ability.Sdk;

/// <summary>
/// Ability 技能进程接入门面，提供统一的启动入口、一元与流式委托执行以及业务自查计量工具。
/// </summary>
public static class AbilityEntry
{
    /// <summary>
    /// 一元任务执行入口：接管命令行参数、建立通信与环境、执行具名委托并提交一元业务数据结果（支持业务模型或 DTO）。
    /// </summary>
    /// <typeparam name="TInput">业务输入模型类型。</typeparam>
    /// <typeparam name="TOutput">业务输出数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="args">命令行参数数组（包含 --ipc &lt;pipeName&gt;）。</param>
    /// <param name="handler">业务处理委托。</param>
    /// <param name="inputJsonTypeInfo">输入模型的序列化元数据。</param>
    /// <param name="outputJsonTypeInfo">输出结果数据的序列化元数据。</param>
    /// <param name="outputPayloadType">可选的输出类型名覆盖；缺省为输出类型名。</param>
    /// <param name="cancellationToken">可选的取消令牌。</param>
    /// <returns>进程退出码（0 表示成功，130 表示取消，1 表示失败或故障）。</returns>
    public static async Task<int> RunAsync<TInput, TOutput>(
        string[] args,
        Func<TInput, ExecutionContextMdl, Task<TOutput>> handler,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        string? outputPayloadType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(inputJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(outputJsonTypeInfo);

        await using var session = new AbilitySession(args);
        return await session.RunUnaryAsync(
            handler,
            inputJsonTypeInfo,
            outputJsonTypeInfo,
            outputPayloadType,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式任务执行入口：接管命令行参数、建立通信与环境、按额度流式枚举并提交元素。
    /// </summary>
    /// <typeparam name="TInput">业务输入模型类型。</typeparam>
    /// <typeparam name="TOutput">流结果项数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="args">命令行参数数组（包含 --ipc &lt;pipeName&gt;）。</param>
    /// <param name="handler">流式业务处理委托。</param>
    /// <param name="inputJsonTypeInfo">输入模型的序列化元数据。</param>
    /// <param name="outputJsonTypeInfo">流结果项数据的序列化元数据。</param>
    /// <param name="cancellationToken">可选的取消令牌。</param>
    /// <returns>进程退出码（0 表示成功，130 表示取消，1 表示失败或故障）。</returns>
    public static async Task<int> RunAsync<TInput, TOutput>(
        string[] args,
        Func<TInput, ExecutionContextMdl, IAsyncEnumerable<TOutput>> handler,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(inputJsonTypeInfo);
        ArgumentNullException.ThrowIfNull(outputJsonTypeInfo);

        await using var session = new AbilitySession(args);
        return await session.RunStreamAsync(
            handler,
            inputJsonTypeInfo,
            outputJsonTypeInfo,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式任务执行入口别名：接管命令行参数、建立通信与环境、按额度流式枚举并提交元素。
    /// </summary>
    /// <typeparam name="TInput">业务输入模型类型。</typeparam>
    /// <typeparam name="TOutput">流结果项数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="args">命令行参数数组（包含 --ipc &lt;pipeName&gt;）。</param>
    /// <param name="handler">流式业务处理委托。</param>
    /// <param name="inputJsonTypeInfo">输入模型的序列化元数据。</param>
    /// <param name="outputJsonTypeInfo">流结果项数据的序列化元数据。</param>
    /// <param name="cancellationToken">可选的取消令牌。</param>
    /// <returns>进程退出码（0 表示成功，130 表示取消，1 表示失败或故障）。</returns>
    public static Task<int> RunStreamAsync<TInput, TOutput>(
        string[] args,
        Func<TInput, ExecutionContextMdl, IAsyncEnumerable<TOutput>> handler,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo,
        CancellationToken cancellationToken = default)
        => RunAsync(args, handler, inputJsonTypeInfo, outputJsonTypeInfo, cancellationToken);

    /// <summary>
    /// 测量一元结果完整信封的 UTF-8 字节数（不包含 4 字节小端长度前缀）。
    /// </summary>
    /// <typeparam name="TModel">业务载荷数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="model">待测量的业务载荷数据实例。</param>
    /// <param name="jsonTypeInfo">序列化元数据。</param>
    /// <param name="typeName">类型名覆盖（可选）。</param>
    /// <returns>信封 UTF-8 编码字节数。</returns>
    public static long MeasureResultBytes<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo,
        string? typeName = null)
    {
        return IpcFrame.MeasureSubmitResultEnvelope(typeName ?? jsonTypeInfo.Type.Name, model, jsonTypeInfo);
    }

    /// <summary>
    /// 测量流式结果项完整信封的 UTF-8 字节数（不包含 4 字节小端长度前缀）。
    /// </summary>
    /// <typeparam name="TModel">流结果项数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="model">待测量的流结果项数据实例。</param>
    /// <param name="jsonTypeInfo">序列化元数据。</param>
    /// <returns>信封 UTF-8 编码字节数。</returns>
    public static long MeasureResultItemBytes<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
    {
        return IpcFrame.MeasureResultItemEnvelope(model, jsonTypeInfo);
    }

    /// <summary>
    /// 测量业务载荷（模型或 DTO 等纯数据结果）原始 UTF-8 JSON 字节数，对应 1 MiB 载荷上限检查。
    /// </summary>
    /// <typeparam name="TModel">业务载荷数据类型（可为业务模型或 DTO）。</typeparam>
    /// <param name="model">待测量的业务载荷数据实例。</param>
    /// <param name="jsonTypeInfo">序列化元数据。</param>
    /// <returns>原始载荷 UTF-8 字节数。</returns>
    public static long MeasurePayloadBytes<TModel>(
        TModel model,
        JsonTypeInfo<TModel> jsonTypeInfo)
    {
        return IpcFrame.MeasureModelPayloadBytes(model, jsonTypeInfo);
    }
}
