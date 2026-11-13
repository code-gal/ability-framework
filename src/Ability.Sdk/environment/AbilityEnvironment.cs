using Ability.Model;
using Ability.Role;

namespace Ability.Sdk;

/// <summary>
/// 执行环境维护者，通过 <see cref="AsyncLocal{T}"/> 维护当前异步调用链中的环境上下文与任务临时目录。
/// 独立于具体通信机制，支持纯本地或测试环境下的嵌套与安全恢复。
/// </summary>
public static class AbilityEnvironment
{
    /// <summary>
    /// 当前异步执行链中持有的执行环境上下文。
    /// </summary>
    private static readonly AsyncLocal<ExecutionContextMdl?> AmbientContext = new();

    /// <summary>
    /// 当前异步执行链中持有的任务临时目录绝对路径。
    /// </summary>
    private static readonly AsyncLocal<string?> AmbientTaskTempPath = new();

    /// <summary>
    /// 获取当前异步执行链中的执行环境模型。
    /// 若当前调用未建立环境，将抛出异常以防缺失取消信号与进度汇报。
    /// </summary>
    /// <exception cref="InvalidOperationException">当未建立环境时抛出。</exception>
    public static ExecutionContextMdl Current => AmbientContext.Value
        ?? throw new InvalidOperationException("当前调用没有执行环境；请通过 AbilityEntry.RunAsync 或 AbilityEnvironment.RunAsync 建立。");

    /// <summary>
    /// 获取宿主为本次任务注入的专属临时目录绝对路径。
    /// </summary>
    /// <exception cref="InvalidOperationException">当未注入任务临时目录时抛出。</exception>
    public static string GetTaskTempPath() => AmbientTaskTempPath.Value
        ?? throw new InvalidOperationException("当前调用没有任务临时目录；它由宿主通过 TaskInput 注入，只在有效任务会话内可用。");

    /// <summary>
    /// 在当前异步流中注入任务临时目录路径，并返回用于在作用域结束时恢复原值的句柄。
    /// </summary>
    /// <param name="path">任务临时目录绝对路径。</param>
    /// <returns>恢复原值的作用域释放句柄。</returns>
    internal static IDisposable EnterTaskTempPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var previous = AmbientTaskTempPath.Value;
        AmbientTaskTempPath.Value = path;
        return new TaskTempPathScope(previous);
    }

    /// <summary>
    /// 在指定的执行环境下运行一元异步委托。正常返回、取消或抛出异常时均自动恢复外层父环境。
    /// </summary>
    /// <typeparam name="TResult">返回值类型。</typeparam>
    /// <param name="context">指定的目标执行环境模型。</param>
    /// <param name="body">待执行的异步委托。</param>
    /// <returns>委托返回值。</returns>
    public static async Task<TResult> RunAsync<TResult>(
        ExecutionContextMdl context,
        Func<Task<TResult>> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        var previous = AmbientContext.Value;
        AmbientContext.Value = context;
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            AmbientContext.Value = previous;
        }
    }

    /// <summary>
    /// 在指定的执行环境下枚举流式子流程。每一项元素的推进以及枚举器的释放均在该环境下执行，结束或异常后恢复父环境。
    /// </summary>
    /// <typeparam name="TItem">流元素类型。</typeparam>
    /// <param name="context">目标执行环境模型。</param>
    /// <param name="body">流工厂委托，推迟到首次推进时触发。</param>
    /// <returns>受环境作用域保护的异步流。</returns>
    public static IAsyncEnumerable<TItem> RunAsync<TItem>(
        ExecutionContextMdl context,
        Func<IAsyncEnumerable<TItem>> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        return new ScopedStream<TItem>(context, body);
    }

    /// <summary>
    /// 任务临时目录作用域恢复句柄。
    /// </summary>
    /// <param name="previous">进入作用域前的原临时路径。</param>
    private sealed class TaskTempPathScope(string? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            AmbientTaskTempPath.Value = previous;
        }
    }

    /// <summary>
    /// 受执行环境作用域保护的异步流包装类。
    /// </summary>
    /// <typeparam name="TItem">流元素类型。</typeparam>
    /// <param name="context">绑定的执行环境上下文。</param>
    /// <param name="body">流工厂委托。</param>
    private sealed class ScopedStream<TItem>(
        ExecutionContextMdl context,
        Func<IAsyncEnumerable<TItem>> body) : IAsyncEnumerable<TItem>
    {
        /// <inheritdoc />
        public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new ScopedEnumerator(context, body, cancellationToken);

        /// <summary>
        /// 受执行环境作用域保护的异步流枚举器。
        /// </summary>
        private sealed class ScopedEnumerator : IAsyncEnumerator<TItem>
        {
            /// <summary>
            /// 枚举绑定的执行环境上下文。
            /// </summary>
            private readonly ExecutionContextMdl _context;

            /// <summary>
            /// 底层流工厂委托。
            /// </summary>
            private readonly Func<IAsyncEnumerable<TItem>> _body;

            /// <summary>
            /// 取消令牌。
            /// </summary>
            private readonly CancellationToken _cancellationToken;

            /// <summary>
            /// 底层实际异步枚举器。
            /// </summary>
            private IAsyncEnumerator<TItem>? _inner;

            /// <summary>
            /// 初始化枚举器实例。
            /// </summary>
            /// <param name="context">执行环境上下文。</param>
            /// <param name="body">流工厂委托。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            public ScopedEnumerator(
                ExecutionContextMdl context,
                Func<IAsyncEnumerable<TItem>> body,
                CancellationToken cancellationToken)
            {
                _context = context;
                _body = body;
                _cancellationToken = cancellationToken;
            }

            /// <inheritdoc />
            public TItem Current => _inner is not null
                ? _inner.Current
                : throw new InvalidOperationException("枚举器尚未推进或已结束。");

            /// <inheritdoc />
            public async ValueTask<bool> MoveNextAsync()
            {
                var previous = AmbientContext.Value;
                AmbientContext.Value = _context;
                try
                {
                    if (_inner is null)
                    {
                        var stream = _body();
                        _inner = stream.GetAsyncEnumerator(_cancellationToken);
                    }

                    return await _inner.MoveNextAsync().ConfigureAwait(false);
                }
                finally
                {
                    AmbientContext.Value = previous;
                }
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                if (_inner is null)
                {
                    return;
                }

                var previous = AmbientContext.Value;
                AmbientContext.Value = _context;
                try
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    AmbientContext.Value = previous;
                }
            }
        }
    }
}
