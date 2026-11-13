using Microsoft.Extensions.Logging;

namespace Ability.Sdk;

/// <summary>
/// 结构化日志出口提供程序，将标准 <see cref="ILogger"/> 日志转换为跨进程结构化 stderr 诊断记录。
/// </summary>
public sealed class AbilityLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// 为指定的类别名称创建日志器实例。
    /// </summary>
    /// <param name="categoryName">日志类别名称。</param>
    /// <returns>已连接到结构化诊断出口的日志器实例。</returns>
    public ILogger CreateLogger(string categoryName) => new AbilityLogger(categoryName);

    /// <summary>
    /// 释放提供程序资源（结构化出口由进程统一持有，无须局部释放）。
    /// </summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// 日志接入静态入口门面，为技能开发者提供开箱即用的标准日志工厂与创建方法。
/// </summary>
public static class AbilityLogging
{
    /// <summary>
    /// 获取全局单例日志工厂实例。
    /// </summary>
    public static ILoggerFactory Factory { get; } = new AbilityLoggerFactory();

    /// <summary>
    /// 根据指定的日志类别名称创建日志器。
    /// </summary>
    /// <param name="categoryName">日志类别名称。</param>
    /// <returns>日志器实例。</returns>
    public static ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return Factory.CreateLogger(categoryName);
    }
}

/// <summary>
/// 专用于向结构化诊断输出日志的内置工厂实现。
/// </summary>
internal sealed class AbilityLoggerFactory : ILoggerFactory
{
    /// <summary>
    /// 单例日志提供程序实例。
    /// </summary>
    private readonly AbilityLoggerProvider _provider = new();

    /// <summary>
    /// 创建对应类别的日志器。
    /// </summary>
    /// <param name="categoryName">日志类别名称。</param>
    /// <returns>日志器实例。</returns>
    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    /// <summary>
    /// 注册外部日志提供程序；此处为空操作以保持统一的单出口诊断格式。
    /// </summary>
    /// <param name="provider">日志提供程序。</param>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <summary>
    /// 释放日志工厂及持有的提供程序。
    /// </summary>
    public void Dispose()
    {
        _provider.Dispose();
    }
}

/// <summary>
/// 结构化诊断日志器，执行异常脱敏安全审查并写入诊断缓冲。
/// </summary>
/// <param name="categoryName">日志类别名称。</param>
internal sealed class AbilityLogger(string categoryName) : ILogger
{
    /// <summary>
    /// 开启日志逻辑作用域；单行结构化诊断不承载作用域数据，返回 null。
    /// </summary>
    /// <typeparam name="TState">作用域状态类型。</typeparam>
    /// <param name="state">作用域标识符。</param>
    /// <returns>可释放作用域对象。</returns>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <summary>
    /// 判断指定的日志级别是否启用；默认放行除 None 之外的所有级别。
    /// </summary>
    /// <param name="logLevel">待检查的日志级别。</param>
    /// <returns>是否启用记录。</returns>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <summary>
    /// 写入一条日志记录；格式化前用安全替身替换原始异常对象，防止未审查信息外泄。
    /// </summary>
    /// <typeparam name="TState">日志状态类型。</typeparam>
    /// <param name="logLevel">日志级别。</param>
    /// <param name="eventId">事件标识。</param>
    /// <param name="state">日志状态数据。</param>
    /// <param name="exception">关联的异常对象。</param>
    /// <param name="formatter">消息格式化委托。</param>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
        {
            return;
        }

        var safeSummary = exception is null ? null : AbilityDiagnostics.ExceptionSummary(exception);
        var safeException = safeSummary is null ? null : new SafeLogException(safeSummary);
        string message;
        try
        {
            message = formatter(state, safeException);
        }
        catch (Exception formatException)
        {
            message = $"日志格式化失败：{AbilityDiagnostics.ExceptionSummary(formatException)}";
        }

        if (safeSummary is not null && !message.Contains(safeSummary, StringComparison.Ordinal))
        {
            message = $"{message}｜{safeSummary}";
        }

        AbilityDiagnostics.Log(logLevel.ToString(), categoryName, eventId.Id, message);
    }
}

/// <summary>
/// 交给日志格式化函数的异常安全替身，仅包含经审查的安全摘要，不携带原始异常堆栈与消息。
/// </summary>
/// <param name="safeDescription">经安全审查的异常说明。</param>
internal sealed class SafeLogException(string safeDescription) : Exception(safeDescription);
