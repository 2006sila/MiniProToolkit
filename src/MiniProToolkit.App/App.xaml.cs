using MiniProToolkit.Core;

namespace MiniProToolkit.App;

public partial class App : System.Windows.Application
{
    /// <summary>How long a second launch waits for the running instance to confirm the window activation.</summary>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The single-instance lock of this process, held from the first line of <see cref="OnStartup"/> until
    /// <see cref="OnExit"/>. Only the first instance owns it, and it is released on a real exit only: a cancelled or
    /// failed close (recovery still pending) keeps it, so the next launch still hands off instead of starting hardware
    /// twice.
    /// </summary>
    private ToolkitSingleInstanceLock? _instanceLock;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Earliest stage of the process: no window, no PowerSwitchHardware, no LHM reader and no timer exists yet, so
        // a second instance can be turned away before anything is initialised. Never throws: every failure mode is a
        // state this process has to react to.
        var instanceLock = ToolkitSingleInstanceLock.TryAcquire();
        if (instanceLock.RecoveredAbandoned)
            AppLog.Write("instance lock: previous owner exited without releasing it; continuing as the first instance");
        if (instanceLock.State == ToolkitSingleInstanceState.Unavailable)
            AppLog.Write($"instance lock unavailable code={instanceLock.FailureCode}");

        // Parsed next; it is pure: it only classifies the argument list, it never touches hardware or files.
        var outcome = ToolkitCommandOutcome.From(e.Args);

        if (!outcome.OpenMainWindow && !outcome.RelayCommand)
        {
            // Usage/help or a rejected command line: a purely local report that never opens a window or touches
            // hardware, so the existing semantics (exit 0 / exit 2) are kept. The lock is dropped before the dialog so
            // a concurrent real launch is not turned away by a process that is about to exit.
            instanceLock.Dispose();
            ReportOutcomeWithoutWindow(outcome);
            return;
        }

        if (instanceLock.State == ToolkitSingleInstanceState.Unavailable)
        {
            // Fail closed: without a confirmed lock this process must not create a second hardware instance, and it
            // must not relay a command whose target instance it cannot identify.
            FailClosedOnInstanceLock(instanceLock, outcome);
            return;
        }

        if (outcome.RelayCommand)
        {
            // A CLI control command may only ever run in the running main instance.
            if (instanceLock.Acquired)
            {
                // The lock was free, so no instance is running that could execute this command. Exit with the
                // documented "no instance" code instead of opening a second window.
                instanceLock.Dispose();
                ReportNoRunningInstance(outcome);
                return;
            }

            RelayCommandToRunningInstance(e.Args, outcome,
                instanceKnownByLock: instanceLock.State == ToolkitSingleInstanceState.AlreadyHeld);
            return;
        }

        if (!instanceLock.Acquired)
        {
            // Second plain launch (no arguments, --startup or --demo): never construct MainWindow, PowerSwitchHardware,
            // LHM or a timer. Ask the running instance to raise its window, then exit with its result.
            ActivateRunningInstanceAndExit(outcome);
            return;
        }

        // First instance: keep the lock for the process lifetime and continue with the normal windowed startup.
        _instanceLock = instanceLock;
        var window = new MainWindow(outcome.Demo);
        MainWindow = window;
        StartCommandPipeServer(window);
        window.Show();
        if (outcome.Startup)
            window.Hide();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        // Releases the OS mutex for a real exit only. This method does not run for a cancelled/failed close, so the
        // lock cannot be handed to a second instance while this one is still controlling the hardware.
        var instanceLock = Interlocked.Exchange(ref _instanceLock, null);
        instanceLock?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Starts the per-user command pipe of the main instance. This is the only process allowed to execute a relayed
    /// command, and the server only marshals to the window; a failure (for example the name being taken by another
    /// instance) is logged and the app keeps running without the CLI channel.
    /// </summary>
    private static void StartCommandPipeServer(MainWindow window)
    {
        try
        {
            var server = AppCommandPipeServer.Start(window);
            // Closed only fires for a real exit, so an aborted close (recovery still pending) keeps the channel up.
            window.Closed += (_, _) => server.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("command pipe server could not start; CLI relay is unavailable", ex);
        }
    }

    /// <summary>
    /// Reports a usage/parse-error outcome without opening a window: the summary is log-safe and carries only an
    /// action/error code, never raw arguments. This is the pre-existing behaviour for help and rejected command lines.
    /// </summary>
    private void ReportOutcomeWithoutWindow(ToolkitCommandOutcome outcome)
    {
        AppLog.Write(outcome.SafeLogLine);
        if (outcome.Quiet)
        {
            WriteStdOut(outcome.SafeLogLine);
            // Usage is a static whitelist text, so it is safe to surface on stdout as well.
            if (outcome.Disposition == ToolkitCommandHostDisposition.ShowUsage && outcome.Details is not null)
                WriteStdOut(outcome.Details);
        }
        else
        {
            System.Windows.MessageBox.Show(
                outcome.Details ?? ToolkitCommandLineParser.Usage,
                outcome.IsError ? "MiniProToolkit 命令行未被接受" : "MiniProToolkit 命令行",
                System.Windows.MessageBoxButton.OK,
                outcome.IsError ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
        }
        Shutdown(outcome.ExitCode);
    }

    /// <summary>
    /// Fail-closed exit when the instance lock itself could not be created. Nothing was initialised at this point, and
    /// no second instance is started on a guess; a script sees the dedicated non-zero code on stdout and in the log.
    /// </summary>
    private void FailClosedOnInstanceLock(ToolkitSingleInstanceLock instanceLock, ToolkitCommandOutcome outcome)
    {
        instanceLock.Dispose();
        var code = instanceLock.FailureCode ?? "unknown";
        AppLog.Write($"instance lock unavailable code={code} exit={ToolkitCommandExitCodes.InstanceLockUnavailable}");
        if (outcome.Quiet || outcome.RelayCommand)
        {
            // A command path must never block a script behind a dialog.
            WriteStdOut($"instance lock unavailable code={code} exit={ToolkitCommandExitCodes.InstanceLockUnavailable}");
        }
        else
        {
            System.Windows.MessageBox.Show(
                "无法确认 MiniProToolkit 是否已有实例在运行。为避免两个进程同时控制风扇与 EC，本次启动已停止，未打开任何窗口。" +
                Environment.NewLine + Environment.NewLine + $"（实例锁错误码 {code}，详见日志）",
                "MiniProToolkit 未启动",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        Shutdown(ToolkitCommandExitCodes.InstanceLockUnavailable);
    }

    /// <summary>
    /// Second launch of a plain (windowed) start. It must never build a window or hardware: it asks the running
    /// instance to show/activate its own window over the existing command pipe and exits. A failure exits with the
    /// dedicated code instead of opening a second instance.
    /// </summary>
    private void ActivateRunningInstanceAndExit(ToolkitCommandOutcome outcome)
    {
        var response = SendActivateRequest();
        if (response is { Status: ToolkitCommandIpcStatus.Ok })
        {
            AppLog.Write(
                $"second instance action=activate status=Ok exit={ToolkitCommandExitCodes.Success}");
            WriteStdOut(ToolkitCommandIpcProtocol.DescribeResponse(response));
            Shutdown(ToolkitCommandExitCodes.Success);
            return;
        }

        var status = response?.Status.ToString() ?? "Unavailable";
        AppLog.Write($"second instance action=activate status={status} exit={ToolkitCommandExitCodes.AlreadyRunning}");
        WriteStdOut($"ipc action={ToolkitCommandIpcProtocol.ActivateActionToken} status={status} executed=False");
        // One short hint at most, and never for --quiet scripts or an invisible --startup launch.
        if (!outcome.Quiet && !outcome.Startup)
        {
            System.Windows.MessageBox.Show(
                "MiniProToolkit 已经在运行，但没能把它的窗口切到前台。请从任务栏或系统托盘打开正在运行的窗口。",
                "MiniProToolkit 已在运行",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        Shutdown(ToolkitCommandExitCodes.AlreadyRunning);
    }

    /// <summary>Sends the UI-only activation frame; every failure mode becomes a null response (fail closed).</summary>
    private static ToolkitCommandIpcResponse? SendActivateRequest()
    {
        try
        {
            return AppCommandPipeClient.Send(ToolkitCommandIpcProtocol.CreateActivateRequest(), ActivateTimeout);
        }
        catch (Exception ex)
        {
            AppLog.Write("second instance: activate request failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Command path without a running instance: the lock was free, so only the exit code is reported. No window is
    /// opened and no hardware is touched.
    /// </summary>
    private void ReportNoRunningInstance(ToolkitCommandOutcome outcome)
    {
        AppLog.Write($"{outcome.SafeLogLine} status=Unavailable exit={ToolkitCommandExitCodes.NoRunningInstance}");
        WriteStdOut(ToolkitCommandIpcProtocol.DescribeResponse(
            ToolkitCommandIpcProtocol.Unavailable(string.Empty, outcome.Action,
                "没有找到正在运行的主实例，命令未执行。")));
        Shutdown(ToolkitCommandExitCodes.NoRunningInstance);
    }

    /// <summary>
    /// Command path: hand the parsed command to the already running instance and exit with its result code. No window
    /// is opened and no dialog is shown, so a script is never blocked; without an instance this exits with
    /// <see cref="ToolkitCommandExitCodes.NoRunningInstance"/> instead of starting a second hardware instance.
    /// <para>
    /// <paramref name="instanceKnownByLock"/> is the evidence that another process really holds the instance lock. It
    /// is passed on so a connect timeout/IO failure is reported as "主实例忙或暂时无法连接，命令未发送" (exit code 6)
    /// instead of the false "MiniProToolkit is not started" (exit code 4); nothing was sent, so no "may already have
    /// executed" wording either.
    /// </para>
    /// </summary>
    private void RelayCommandToRunningInstance(IReadOnlyList<string> arguments, ToolkitCommandOutcome outcome,
        bool instanceKnownByLock)
    {
        var parsed = ToolkitCommandLineParser.Parse(arguments);
        if (parsed.Command is null)
        {
            // Cannot happen for a relayed outcome, but never guess an exit code from an unparsed command.
            AppLog.Write("startup command relay rejected: no parsed command");
            Shutdown(ToolkitCommandExitCodes.ParseError);
            return;
        }

        // The explicit --request-id (when the caller supplied one) is reused as is, so a retry of the same command
        // after a timeout can be recognized by the running instance; without it a fresh id is generated per launch.
        // The id itself is never echoed to stdout or the log.
        var request = ToolkitCommandIpcProtocol.CreateRequest(parsed.Command, parsed.RequestId);
        var response = AppCommandPipeClient.Send(request, instanceKnownByLock: instanceKnownByLock);
        var exitCode = ExitCodeFor(response.Status);

        // Log-safe: only the action name, the status and the exit code, never an option value, a request id or the raw
        // command line.
        AppLog.Write($"{outcome.SafeLogLine} status={response.Status} exit={exitCode}");
        WriteStdOut(ToolkitCommandIpcProtocol.DescribeResponse(response));
        if (!string.IsNullOrWhiteSpace(response.Message))
            WriteStdOut(response.Message);
        if (response.Report is not null)
        {
            foreach (var line in ToolkitCommandIpcProtocol.DescribeReport(response.Report))
                WriteStdOut(line);
        }
        Shutdown(exitCode);
    }

    /// <summary>
    /// Wire status to process exit code. The mapping itself lives in Core (<see cref="ToolkitCommandExitCodes.ForIpcStatus"/>)
    /// so it can be unit-tested without this windowed assembly.
    /// </summary>
    private static int ExitCodeFor(ToolkitCommandIpcStatus status) => ToolkitCommandExitCodes.ForIpcStatus(status);

    /// <summary>
    /// Best-effort structured line for scripts. A windowed process may have no console attached or the pipe may
    /// already be closed; neither may change the exit code.
    /// </summary>
    private static void WriteStdOut(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch
        {
            // Ignored on purpose: logging already recorded the same summary.
        }
    }
}
