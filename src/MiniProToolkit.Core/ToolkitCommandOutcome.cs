namespace MiniProToolkit.Core;

/// <summary>
/// Process exit codes of the scripting entry point. A recognised command must never look like success to a calling
/// script unless the running main instance confirmed it, so every failure mode has its own non-zero code.
/// </summary>
public static class ToolkitCommandExitCodes
{
    /// <summary>Usage/help output, or a plain windowed launch (no command was requested).</summary>
    public const int Success = 0;

    /// <summary>The argument list was rejected by the parser; nothing was sent anywhere.</summary>
    public const int ParseError = 2;

    /// <summary>
    /// Internal sentinel: the command was accepted for relay but the host has not talked to the running instance yet.
    /// The host must replace it with the result code below before it shuts down; it is never returned to a shell.
    /// </summary>
    public const int RelayPending = -1;

    /// <summary>
    /// No accepting main instance: not running, IPC disabled, a protocol mismatch, or an untrusted pipe server. A
    /// connect timeout only lands here when the instance lock gave no evidence that an instance exists - with that
    /// evidence the answer is the "busy or temporarily unreachable" timeout (exit code 6).
    /// </summary>
    public const int NoRunningInstance = 4;

    /// <summary>The running instance refused the command (safety gate, unknown profile) or execution failed.</summary>
    public const int CommandRejected = 5;

    /// <summary>
    /// The instance accepted the command but did not answer in time; its state may already have changed. The same code
    /// is also used when another process holds the instance lock but its channel did not accept the connection in
    /// time: in that case the answer text says "主实例忙或暂时无法连接，命令未发送" and nothing was sent (see
    /// <see cref="ToolkitCommandIpcProtocol.ClassifyPreDispatchFailure"/>), so a retry is safe. It is never the
    /// "no instance" code 4.
    /// </summary>
    public const int CommandTimedOut = 6;

    /// <summary>
    /// The process could not confirm whether it is the only instance (the instance lock could not be created). Nothing
    /// was started: no window, no hardware handle, no command was relayed.
    /// </summary>
    public const int InstanceLockUnavailable = 7;

    /// <summary>
    /// Another instance owns the toolkit and could not be asked to show its window. This launch exited instead of
    /// creating a second window/hardware instance, so the requested activation may simply be retried later.
    /// </summary>
    public const int AlreadyRunning = 8;

    /// <summary>
    /// Maps one wire status onto the process exit code a script sees. Pure, so the mapping (in particular "a busy /
    /// unreachable pre-dispatch failure is 6, not the 'no instance' 4") is testable without the app project.
    /// </summary>
    public static int ForIpcStatus(ToolkitCommandIpcStatus status) => status switch
    {
        ToolkitCommandIpcStatus.Ok => Success,
        ToolkitCommandIpcStatus.TimedOut => CommandTimedOut,
        ToolkitCommandIpcStatus.Unavailable => NoRunningInstance,
        _ => CommandRejected
    };
}

/// <summary>What the host process should do with a parsed command line.</summary>
public enum ToolkitCommandHostDisposition
{
    /// <summary>Bare launch (no arguments, or only host flags such as --demo / --startup): show the main window.</summary>
    OpenMainWindow,

    /// <summary>Usage was requested: print it and exit with <see cref="ToolkitCommandExitCodes.Success"/>.</summary>
    ShowUsage,

    /// <summary>The parser rejected the argument list: exit with <see cref="ToolkitCommandExitCodes.ParseError"/>.</summary>
    RejectedCommandLine,

    /// <summary>
    /// A whitelisted command was parsed. The host must relay it to the already running main instance over the
    /// per-user command pipe and exit with that instance's result code. The host must never open a second window
    /// or touch hardware itself when no instance answers.
    /// </summary>
    RelayToRunningInstance
}

/// <summary>
/// Host-facing decision derived from <see cref="ToolkitCommandLineParser"/>. Every text field is log-safe: it carries
/// only a disposition, an action name and a structured error code. Raw arguments, option values (including profile
/// names), paths and pattern text are never copied into <see cref="SafeLogLine"/> or <see cref="Details"/>.
/// </summary>
public sealed record ToolkitCommandOutcome(
    ToolkitCommandHostDisposition Disposition,
    int ExitCode,
    bool Quiet,
    bool Demo,
    bool Startup,
    ToolkitCommandAction Action,
    ToolkitCommandErrorCode ErrorCode,
    string SafeLogLine,
    string? Details)
{
    /// <summary>Plain launch: the host continues into the normal windowed startup path.</summary>
    public bool OpenMainWindow => Disposition == ToolkitCommandHostDisposition.OpenMainWindow;

    /// <summary>The parsed command must be relayed to the running instance; the host owns the exit code.</summary>
    public bool RelayCommand => Disposition == ToolkitCommandHostDisposition.RelayToRunningInstance;

    /// <summary>Only a rejected command line counts as an error for UI wording.</summary>
    public bool IsError => Disposition == ToolkitCommandHostDisposition.RejectedCommandLine;

    /// <summary>
    /// A command always reports through stdout and the exit code instead of a modal dialog, so a script is never
    /// blocked by a MessageBox; --quiet additionally silences the usage dialog.
    /// </summary>
    /// <remarks>
    /// Only usage and parse errors own an exit code on their own. Every windowed launch is decided by the instance
    /// lock first (first instance opens the window, a second one activates it), so a plain launch never looks like a
    /// finished command here.
    /// </remarks>
    public bool ShowDialog =>
        !Quiet && Disposition is ToolkitCommandHostDisposition.ShowUsage or ToolkitCommandHostDisposition.RejectedCommandLine;

    /// <summary>
    /// Maps a raw argument list onto a host disposition. Pure and side-effect free: it opens no lease, reads no
    /// profile, writes no file and touches no hardware. The caller owns logging, stdout and the process exit code.
    /// </summary>
    public static ToolkitCommandOutcome From(IReadOnlyList<string>? arguments)
    {
        var result = ToolkitCommandLineParser.Parse(arguments);
        var quiet = ToolkitCommandLineParser.HasHostFlag(result.HostFlags, "--quiet");
        var demo = ToolkitCommandLineParser.HasHostFlag(result.HostFlags, "--demo");
        var startup = ToolkitCommandLineParser.HasHostFlag(result.HostFlags, "--startup");

        if (result.Success && result.Command is not null)
        {
            var command = result.Command;
            if (command.Action == ToolkitCommandAction.Help)
                return new(ToolkitCommandHostDisposition.ShowUsage, ToolkitCommandExitCodes.Success,
                    quiet, demo, startup, command.Action, ToolkitCommandErrorCode.None,
                    "startup command=help exit=0",
                    ToolkitCommandLineParser.Usage);

            // Recognised: the host relays it to the running instance. ExitCode stays a sentinel here because only
            // the instance's answer can decide between 0, 5 and 6; a missing instance is 4.
            return new(ToolkitCommandHostDisposition.RelayToRunningInstance, ToolkitCommandExitCodes.RelayPending,
                quiet, demo, startup, command.Action, ToolkitCommandErrorCode.None,
                $"startup command=relay action={command.Action}",
                null);
        }

        var error = result.Error;
        // No command at all: the host keeps the existing behaviour and just opens the main window.
        if (error is null || error.Code == ToolkitCommandErrorCode.EmptyCommandLine)
            return new(ToolkitCommandHostDisposition.OpenMainWindow, ToolkitCommandExitCodes.Success,
                quiet, demo, startup, ToolkitCommandAction.None, ToolkitCommandErrorCode.None,
                "startup command=plain exit=0",
                null);

        return new(ToolkitCommandHostDisposition.RejectedCommandLine, ToolkitCommandExitCodes.ParseError,
            quiet, demo, startup, ToolkitCommandAction.None, error.Code,
            $"startup command rejected code={error.Code} exit={ToolkitCommandExitCodes.ParseError}",
            $"命令行未被接受（错误码 {error.Code}）：{error.Message}{Environment.NewLine}{Environment.NewLine}{ToolkitCommandLineParser.Usage}");
    }
}
