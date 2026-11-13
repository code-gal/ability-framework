using Ability.Role;
using Ability.Sdk;

namespace Ability.Model;

/// <summary>
/// 执行上下文模型的部分类声明，包含向只读角色 <see cref="IExecutionContext"/> 的投影工厂与适配器实现。
/// </summary>
public sealed partial class ExecutionContextMdl
{
    /// <summary>
    /// 将执行上下文模型投影为只读的 <see cref="IExecutionContext"/> 角色接口。
    /// </summary>
    /// <param name="model">被投影的执行上下文模型实例。</param>
    /// <returns>只读角色投影实例。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="model"/> 为 null 时抛出。</exception>
    public static IExecutionContext AsExecutionContext(ExecutionContextMdl model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ExecutionContextAdapter(model);
    }

    /// <summary>
    /// 将当前模型实例投影为只读的 <see cref="IExecutionContext"/> 角色接口。
    /// </summary>
    /// <remarks>
    /// 保留此实例方法作为既有调用的兼容入口，内部委托至静态工厂 <see cref="AsExecutionContext(ExecutionContextMdl)"/>。
    /// </remarks>
    /// <returns>只读角色投影实例。</returns>
    public IExecutionContext AsExecutionContext() => AsExecutionContext(this);

    /// <summary>
    /// 包装执行模型为只读角色的私有适配器。
    /// </summary>
    /// <param name="model">被包装的模型实例。</param>
    private sealed class ExecutionContextAdapter(ExecutionContextMdl model) : IExecutionContext
    {
        /// <inheritdoc />
        public CancellationToken CancellationToken => model.CancellationToken;

        /// <inheritdoc />
        public TimeProvider TimeProvider => model.TimeProvider;

        /// <inheritdoc />
        public IProgress<RunProgress>? Progress => model.Progress;
    }
}
