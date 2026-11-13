using System.Diagnostics;

namespace Ability.Hosting;

/// <summary>
/// 进程生命周期护栏，负责启动子进程、异步排空有界输出流并在退出或超时时安全回收子进程树。
/// 提供针对单进程实例以及统一权威记录 <see cref="InvocationRecord"/> 的双重启动与回收机制。
/// </summary>
internal sealed class ProcessGuard : IAsyncDisposable
{
    /// <summary>
    /// 强制终止进程树后的最大等待退出时限。
    /// </summary>
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 标准输出和错误流的最大异步排空时限。
    /// </summary>
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 受护栏保护的底层系统进程实例。
    /// </summary>
    private readonly Process _process;

    /// <summary>
    /// 标准输出的有界读取器。
    /// </summary>
    private readonly BoundedProcessOutputReader _stdoutReader;

    /// <summary>
    /// 标准错误输出的有界读取器。
    /// </summary>
    private readonly BoundedProcessOutputReader _stderrReader;

    /// <summary>
    /// 资源释放标识状态（0 为未释放，1 为已释放）。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 初始化进程护栏实例。
    /// </summary>
    /// <param name="process">底层进程实例。</param>
    /// <param name="stdoutReader">标准输出读取器。</param>
    /// <param name="stderrReader">标准错误读取器。</param>
    private ProcessGuard(Process process, BoundedProcessOutputReader stdoutReader, BoundedProcessOutputReader stderrReader)
    {
        _process = process;
        _stdoutReader = stdoutReader;
        _stderrReader = stderrReader;
    }

    /// <summary>
    /// 子进程 ID。
    /// </summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// 进程是否已退出。
    /// </summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// 进程退出码；未退出前读取可能抛出异常。
    /// </summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// 获取子进程退出的异步等待任务，用于在握手前并发侦测子进程提前退出。
    /// </summary>
    public Task ProcessExitTask => _process.WaitForExitAsync();

    /// <summary>
    /// 启动单个子进程并接管输出流排空。
    /// </summary>
    /// <param name="launcher">已准备就绪的启动器。</param>
    /// <param name="pipeName">连接管道名称。</param>
    /// <param name="extraArguments">附加命令行参数。</param>
    /// <param name="environmentVariables">环境变量字典。</param>
    /// <param name="stderrCallback">标准错误单行到达回调。</param>
    /// <returns>进程护栏实例。</returns>
    public static ProcessGuard Start(
        PreparedLauncher launcher,
        string pipeName,
        IReadOnlyList<string>? extraArguments = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Action<string>? stderrCallback = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launcher.FileName,
            WorkingDirectory = launcher.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var prefix in launcher.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefix);
        }

        startInfo.ArgumentList.Add("--ipc");
        startInfo.ArgumentList.Add(pipeName);

        if (extraArguments is not null)
        {
            foreach (var arg in extraArguments)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动子进程：“{launcher.FileName}”。");
        }

        var stdoutReader = new BoundedProcessOutputReader(process.StandardOutput);
        var stderrReader = new BoundedProcessOutputReader(process.StandardError, stderrCallback);

        return new ProcessGuard(process, stdoutReader, stderrReader);
    }

    /// <summary>
    /// 启动一次调用的实际进程，接好输出收集并把进程绑定到权威调用记录。
    /// </summary>
    /// <param name="record">已登记的调用记录，推进其状态到 Starting 并绑定进程。</param>
    /// <param name="launcher">已准备的产物或可执行文件及前缀参数。</param>
    /// <param name="extraArguments">附加启动参数，排在 --ipc 与调用键之后。</param>
    /// <param name="cleanupGracePeriod">停止时等待进程自行退出的宽限期。</param>
    /// <param name="environmentVariables">可选的环境变量字典，注入子进程执行环境。</param>
    /// <param name="cancellationToken">启动协作取消令牌；仅在原生启动前后检查，不中断同步系统调用。</param>
    /// <returns>已绑定实际进程的调用记录。</returns>
    public static async Task<InvocationRecord> StartAsync(
        InvocationRecord record,
        PreparedLauncher launcher,
        IReadOnlyList<string>? extraArguments,
        TimeSpan cleanupGracePeriod,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        record.MarkStarting();
        Process? process = null;
        var started = false;
        var attached = false;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        BoundedProcessOutputReader? stderrReader = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = launcher.WorkingDirectory
            };

            if (environmentVariables is not null)
            {
                foreach (var pair in environmentVariables)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            foreach (var prefix in launcher.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefix);
            }

            startInfo.ArgumentList.Add("--ipc");
            startInfo.ArgumentList.Add(record.InvocationKey);

            if (extraArguments is not null)
            {
                foreach (var arg in extraArguments)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }

            process = new Process { StartInfo = startInfo };
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动 Launcher 产物：“{launcher.FileName}”。");
            }

            started = true;
            var stdoutReader = new BoundedProcessOutputReader(process.StandardOutput);
            stderrReader = new BoundedProcessOutputReader(process.StandardError, record.TraceCollector.AddLine);
            stdout = stdoutReader.ReadTask;
            stderr = stderrReader.ReadTask;

            var stopWasRequested = record.AttachProcess(process, stdout, stderr, () => stderrReader.GetCurrentText());
            attached = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (stopWasRequested)
            {
                await StopAsync(record, cleanupGracePeriod).ConfigureAwait(false);
            }

            return record;
        }
        catch
        {
            if (started)
            {
                if (!attached)
                {
                    record.AttachProcess(
                        process!,
                        stdout ?? Task.FromResult(string.Empty),
                        stderr ?? Task.FromResult(string.Empty),
                        () => stderrReader?.GetCurrentText() ?? string.Empty);
                }
            }
            else
            {
                record.MarkStartFailed();
                process?.Dispose();
            }

            await StopAsync(record, cleanupGracePeriod).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 停止并回收一个调用；同一记录上的回收请求在并发闸门内串行执行。
    /// 强杀后执行有限等待；输出流排空同样施加有限时限。
    /// </summary>
    /// <param name="record">要回收的调用记录。</param>
    /// <param name="gracePeriod">等待进程自行退出的宽限期；超期后强杀进程树。</param>
    /// <returns>进程是否在宽限期内自行退出。</returns>
    public static async Task<bool> StopAsync(InvocationRecord record, TimeSpan gracePeriod)
    {
        await record.ReapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (record.State == InvocationState.Reaped)
            {
                return record.ExitedNaturally ?? true;
            }

            record.RequestStop();
            var process = await ResolveProcessForStopAsync(record).ConfigureAwait(false);
            if (process is null)
            {
                return false;
            }

            var (exitedNaturally, confirmed) = await WaitForTerminationAsync(process, gracePeriod).ConfigureAwait(false);
            await DrainOutputStreamsAsync(record).ConfigureAwait(false);

            if (!confirmed)
            {
                // 强杀超时仍未物理退出时保留记录及资源，不注销句柄
                return false;
            }

            record.MarkExited();
            record.MarkReaped(exitedNaturally);
            process.Dispose();
            return exitedNaturally;
        }
        finally
        {
            record.ReapGate.Release();
            record.ReleaseStopCancellationAfterLifecycle();
        }
    }

    /// <summary>
    /// 取得待停止的进程句柄；尚无句柄的启动中记录先等待启动收口，确无进程时按未启动完成回收。
    /// </summary>
    /// <param name="record">要回收的调用记录。</param>
    /// <returns>待停止的进程句柄；记录确无进程时为 null。</returns>
    private static async Task<Process?> ResolveProcessForStopAsync(InvocationRecord record)
    {
        var process = record.Process;
        if (process is null && record.State == InvocationState.Starting)
        {
            await record.WaitForStartResolutionAsync().ConfigureAwait(false);
            process = record.Process;
        }

        if (process is not null)
        {
            return process;
        }

        if (record.State != InvocationState.Exited && record.State != InvocationState.Reaped)
        {
            record.MarkStartFailed();
        }

        record.MarkReaped(exitedNaturally: false);
        return null;
    }

    /// <summary>
    /// 在宽限期内等待自然退出，必要时强杀整棵进程树，并确认是否已物理退出。
    /// </summary>
    /// <param name="process">目标系统进程。</param>
    /// <param name="gracePeriod">等待进程自行退出的宽限期。</param>
    /// <returns>是否自然退出，以及退出是否已确认。</returns>
    private static async Task<(bool ExitedNaturally, bool Confirmed)> WaitForTerminationAsync(Process process, TimeSpan gracePeriod)
    {
        var exitedNaturally = await WaitForExitWithinAsync(process, gracePeriod).ConfigureAwait(false);
        if (!exitedNaturally && !process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (exitedNaturally || process.HasExited)
        {
            return (exitedNaturally, true);
        }

        try
        {
            using var killTimeoutCts = new CancellationTokenSource(KillWaitTimeout);
            await process.WaitForExitAsync(killTimeoutCts.Token).ConfigureAwait(false);
            return (exitedNaturally, process.HasExited);
        }
        catch (OperationCanceledException)
        {
            return (exitedNaturally, false);
        }
    }

    /// <summary>
    /// 在有限时限内排空标准错误与标准输出任务，超时保留已收集快照供后续观察。
    /// </summary>
    /// <param name="record">要回收的调用记录。</param>
    /// <returns>排空的异步完成任务。</returns>
    private static async Task DrainOutputStreamsAsync(InvocationRecord record)
    {
        if (record.StandardErrorTask is { } stderrTask)
        {
            await DrainWithinBudgetAsync(stderrTask).ConfigureAwait(false);
        }

        if (record.StandardOutputTask is { } stdoutTask)
        {
            await DrainWithinBudgetAsync(stdoutTask).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在输出排空时限内等待指定输出任务结束。
    /// </summary>
    /// <param name="streamTask">输出读取任务。</param>
    /// <returns>等待的异步完成任务。</returns>
    private static async Task DrainWithinBudgetAsync(Task streamTask)
    {
        try
        {
            using var drainCts = new CancellationTokenSource(StreamDrainTimeout);
            await streamTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 在指定宽限期内异步等待进程退出。
    /// </summary>
    /// <param name="process">目标系统进程。</param>
    /// <param name="timeout">等待时限。</param>
    /// <returns>在时限内退出返回 true，超时仍未退出返回 false。</returns>
    public static async Task<bool> WaitForExitWithinAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }

        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(timeout);
        return await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false) == exitTask;
    }

    /// <summary>
    /// 零宽限期的立即回收已退出的调用记录；若已回收则直接返回。
    /// </summary>
    /// <param name="record">待回收的调用记录。</param>
    /// <returns>异步回收任务。</returns>
    public static async Task ReapExitedAsync(InvocationRecord record)
    {
        if (record.State == InvocationState.Reaped)
        {
            return;
        }

        await StopAsync(record, TimeSpan.Zero).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待进程在指定时限内退出；超时则强杀整棵进程树，并有界排空输出流。
    /// 明确核验物理退出状态，强杀超时未退出时不视为已确认。
    /// </summary>
    /// <param name="gracePeriod">等待退出的宽限期。</param>
    /// <returns>进程退出确认结果、退出码与 stderr 诊断文本。</returns>
    public async Task<ProcessExitResult> WaitOrKillAsync(TimeSpan gracePeriod)
    {
        var exited = false;
        var killed = false;
        try
        {
            if (_process.HasExited)
            {
                exited = true;
            }
            else
            {
                using var timeoutCts = new CancellationTokenSource(gracePeriod);
                await _process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exited = true;
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (!exited && !_process.HasExited)
        {
            killed = true;
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                using var killTimeoutCts = new CancellationTokenSource(KillWaitTimeout);
                await _process.WaitForExitAsync(killTimeoutCts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }
        }
        else
        {
            exited = true;
        }

        var confirmed = exited && _process.HasExited;

        string stderr;
        try
        {
            using var stderrDrainCts = new CancellationTokenSource(StreamDrainTimeout);
            stderr = await _stderrReader.ReadTask.WaitAsync(stderrDrainCts.Token).ConfigureAwait(false);
        }
        catch
        {
            stderr = _stderrReader.GetCurrentText();
        }

        try
        {
            using var stdoutDrainCts = new CancellationTokenSource(StreamDrainTimeout);
            await _stdoutReader.ReadTask.WaitAsync(stdoutDrainCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }

        int? exitCode = null;
        if (confirmed)
        {
            try
            {
                exitCode = _process.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }
        }

        return new ProcessExitResult(confirmed, killed, exitCode, stderr);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                using var exitCts = new CancellationTokenSource(KillWaitTimeout);
                await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _process.Dispose();
    }
}

/// <summary>
/// 子进程退出等待与强杀结果模型。
/// </summary>
/// <param name="Confirmed">子进程是否已明确物理退出（未退出时为 false）。</param>
/// <param name="Killed">是否触发了强杀进程树。</param>
/// <param name="ExitCode">进程退出码；未确认退出时为 null。</param>
/// <param name="Stderr">标准错误输出诊断文本。</param>
internal readonly record struct ProcessExitResult(
    bool Confirmed,
    bool Killed,
    int? ExitCode,
    string Stderr);
