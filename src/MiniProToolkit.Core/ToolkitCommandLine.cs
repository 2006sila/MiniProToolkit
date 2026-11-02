namespace MiniProToolkit.Core;

/// <summary>
/// Whitelisted command surface for the future scripting entry point. These values are a closed set: anything the
/// parser does not recognise becomes a structured error instead of an unknown action.
/// </summary>
public enum ToolkitCommandAction
{
    None,
    Help,
    Status,
    ApplyProfile,
    SetMode,
    RestoreAutomatic,

    /// <summary>
    /// UI-only action of the command pipe: bring the window of the already running main instance to the front. It is
    /// deliberately not part of <see cref="ToolkitCommandLineParser.SupportedActions"/> (no script can request it) and
    /// it must never reach the EC, WMI, the fan controller or the Guardian path.
    /// </summary>
    Activate
}

public enum ToolkitCommandErrorCode
{
    None,
    EmptyCommandLine,
    TooManyArguments,
    UnknownAction,
    UnknownOption,
    MissingArgument,
    DuplicateArgument,
    UnexpectedArgument,
    InvalidArgument,
    UnsupportedMode,
    InvalidProfileName,
    ConfirmationRequired
}

/// <summary>Structured parse error. Messages never echo raw user tokens so they are safe to log verbatim.</summary>
public sealed record ToolkitCommandError(ToolkitCommandErrorCode Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>
/// Immutable command description. Holding one of these never performs I/O: a host such as the WPF app must still
/// apply the usual Guardian lease, serialized control queue and confirmation flow before touching hardware.
/// </summary>
public sealed record ToolkitCommand(
    ToolkitCommandAction Action,
    string? ProfileName = null,
    PowerMode? Mode = null,
    bool Confirmed = false)
{
    public bool IsReadOnly => Action is ToolkitCommandAction.Help or ToolkitCommandAction.Status;

    /// <summary>Apply/set/restore need an explicit confirmation flag; the parser refuses to drop it silently.</summary>
    public bool RequiresConfirmation => Action is ToolkitCommandAction.ApplyProfile or ToolkitCommandAction.SetMode or ToolkitCommandAction.RestoreAutomatic;

    /// <summary>Log-safe summary: no command line, no free-form user text beyond the profile name.</summary>
    public string SafeSummary => Action switch
    {
        ToolkitCommandAction.ApplyProfile => $"apply-profile profile={ProfileName} confirmed={Confirmed}",
        ToolkitCommandAction.SetMode => $"set-mode mode={Mode} confirmed={Confirmed}",
        ToolkitCommandAction.RestoreAutomatic => $"restore-automatic confirmed={Confirmed}",
        // Log-safe by construction: activate carries no value at all.
        ToolkitCommandAction.Activate => "activate",
        _ => Action.ToString().ToLowerInvariant()
    };
}

public sealed record ToolkitCommandParseResult(
    ToolkitCommand? Command,
    ToolkitCommandError? Error,
    IReadOnlyList<string> HostFlags,
    string? RequestId = null)
{
    public bool Success => Error is null && Command is not null;
}

/// <summary>
/// Pure, side-effect-free parser for the planned scripting entry point. It only builds or rejects command models;
/// it never reads profiles, never opens leases, never calls hardware and never writes files.
/// </summary>
public static class ToolkitCommandLineParser
{
    public const int MaxTokens = 24;

    /// <summary>
    /// Arguments that belong to the WPF host and are passed through untouched. --quiet only suppresses the host UI
    /// (MessageBox) and switches the structured summary to stdout/AppLog; it never changes the parsed command.
    /// </summary>
    public static readonly IReadOnlyList<string> HostFlagNames = ["--demo", "--startup", "--quiet"];

    /// <summary>
    /// Actions a caller may name on the command line. The IPC-only <see cref="ToolkitCommandAction.Activate"/> is not
    /// part of this list on purpose: it exists so a second launch can raise the window of the first one, not as a
    /// scriptable surface.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedActions =
        ["status", "apply-profile", "set-mode", "restore-automatic", "help"];

    public static string Usage =>
        "用法：" + Environment.NewLine +
        "  MiniProToolkit.exe status" + Environment.NewLine +
        "  MiniProToolkit.exe apply-profile --name <方案名> --confirm" + Environment.NewLine +
        "  MiniProToolkit.exe set-mode --mode balance|performance|silence --confirm" + Environment.NewLine +
        "  MiniProToolkit.exe restore-automatic --confirm" + Environment.NewLine +
        "主机开关：--demo（演示数据）、--startup（登录启动后隐藏窗口）、--quiet（只写 stdout 与日志，不弹窗）。" + Environment.NewLine +
        "可选 --request-id=<32 位十六进制>：同一次写动作的超时重试请复用同一个 id，主实例不会重复执行；该值不会回显。" + Environment.NewLine +
        "有命令时不打开第二个窗口：命令会转发给已在运行的主实例，由它经过 Guardian 租约与既有安全门执行。" + Environment.NewLine +
        "退出码：0 帮助/正常开窗/命令执行成功；2 命令行未被接受；4 没有正在运行的主实例（或管道不可用）；" + Environment.NewLine +
        "        5 主实例拒绝了命令或执行失败；6 主实例在时限内没有返回结果（请用 status 复核）；" + Environment.NewLine +
        "        7 无法确认是否已有实例（进程单实例锁不可用），本次未启动；8 已有实例在运行但未能激活其窗口，本次未启动。" + Environment.NewLine +
        "该入口自身不会直接操作硬件：没有主实例时只返回退出码，不会启动第二个硬件控制实例。";

    /// <summary>Case-insensitive lookup for a host flag collected in <see cref="ToolkitCommandParseResult.HostFlags"/>.</summary>
    public static bool HasHostFlag(IReadOnlyList<string>? hostFlags, string flag) =>
        hostFlags is not null && hostFlags.Contains(flag, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportedAction(string? action) => ParseAction(action) is not null;

    public static ToolkitCommandParseResult Parse(IReadOnlyList<string>? arguments)
    {
        var hostFlags = new List<string>();
        var tokens = new List<string>();
        string? requestId = null;
        foreach (var argument in arguments ?? [])
        {
            if (string.IsNullOrWhiteSpace(argument)) continue;
            var trimmed = argument.Trim();
            var host = HostFlagNames.FirstOrDefault(flag => string.Equals(flag, trimmed, StringComparison.OrdinalIgnoreCase));
            if (host is not null)
            {
                if (!hostFlags.Contains(host, StringComparer.OrdinalIgnoreCase)) hostFlags.Add(host);
                continue;
            }
            // Explicit request id for a retried write. Only the inline "=" form is accepted, so the value can never
            // be re-read as an action/option token; the raw value is never echoed in an error message.
            if (TrySplitOption(trimmed, out var requestIdOption, out var requestIdValue) &&
                string.Equals(requestIdOption, "--request-id", StringComparison.OrdinalIgnoreCase))
            {
                if (requestId is not null)
                    return Fail(hostFlags, ToolkitCommandErrorCode.DuplicateArgument, "--request-id 重复。");
                if (!ToolkitCommandIpcProtocol.IsValidRequestId(requestIdValue))
                    return Fail(hostFlags, ToolkitCommandErrorCode.InvalidArgument, "--request-id 需要 32 位十六进制取值。");
                requestId = requestIdValue;
                continue;
            }
            tokens.Add(trimmed);
        }

        if (tokens.Count == 0) return Fail(hostFlags, ToolkitCommandErrorCode.EmptyCommandLine, "缺少动作。");
        if (tokens.Count > MaxTokens) return Fail(hostFlags, ToolkitCommandErrorCode.TooManyArguments, $"参数过多，最多 {MaxTokens} 个。");

        var action = ParseAction(tokens[0]);
        if (action is null)
            return Fail(hostFlags, ToolkitCommandErrorCode.UnknownAction, $"未知动作。支持的动作：{string.Join(" / ", SupportedActions)}。");

        string? profileName = null;
        PowerMode? mode = null;
        var confirmed = false;
        var seenConfirm = false;

        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!TrySplitOption(token, out var option, out var inlineValue))
                return Fail(hostFlags, ToolkitCommandErrorCode.UnknownOption, "存在无法识别的参数。");
            switch (option.ToLowerInvariant())
            {
                case "--confirm" or "--yes":
                    if (inlineValue is not null) return Fail(hostFlags, ToolkitCommandErrorCode.UnexpectedArgument, "--confirm 不接受取值。");
                    if (seenConfirm) return Fail(hostFlags, ToolkitCommandErrorCode.DuplicateArgument, "--confirm 重复。");
                    seenConfirm = true;
                    confirmed = true;
                    break;
                case "--name" or "-n":
                    if (profileName is not null) return Fail(hostFlags, ToolkitCommandErrorCode.DuplicateArgument, "--name 重复。");
                    if (!TryTakeValue(tokens, ref index, inlineValue, out var name))
                        return Fail(hostFlags, ToolkitCommandErrorCode.MissingArgument, "--name 缺少方案名。");
                    profileName = name;
                    break;
                case "--mode" or "-m":
                    if (mode is not null) return Fail(hostFlags, ToolkitCommandErrorCode.DuplicateArgument, "--mode 重复。");
                    if (!TryTakeValue(tokens, ref index, inlineValue, out var rawMode))
                        return Fail(hostFlags, ToolkitCommandErrorCode.MissingArgument, "--mode 缺少模式。");
                    var parsedMode = ParseMode(rawMode);
                    if (parsedMode is null) return Fail(hostFlags, ToolkitCommandErrorCode.UnsupportedMode, "模式仅支持 balance / performance / silence。");
                    mode = parsedMode;
                    break;
                default:
                    return Fail(hostFlags, ToolkitCommandErrorCode.UnknownOption, "存在无法识别的选项。");
            }
        }

        switch (action.Value)
        {
            case ToolkitCommandAction.Help:
            case ToolkitCommandAction.Status:
                if (profileName is not null || mode is not null || confirmed)
                    return Fail(hostFlags, ToolkitCommandErrorCode.UnexpectedArgument, $"{action.Value.ToString().ToLowerInvariant()} 不接受额外参数。");
                break;
            case ToolkitCommandAction.ApplyProfile:
                if (profileName is null) return Fail(hostFlags, ToolkitCommandErrorCode.MissingArgument, "apply-profile 需要 --name <方案名>。");
                if (mode is not null) return Fail(hostFlags, ToolkitCommandErrorCode.UnexpectedArgument, "apply-profile 不接受 --mode。");
                if (!IsValidProfileName(profileName)) return Fail(hostFlags, ToolkitCommandErrorCode.InvalidProfileName, "方案名需要 1–64 个字符且首尾不能有空格。");
                if (!confirmed) return Fail(hostFlags, ToolkitCommandErrorCode.ConfirmationRequired, "apply-profile 需要显式 --confirm 才会被接受。");
                break;
            case ToolkitCommandAction.SetMode:
                if (mode is null) return Fail(hostFlags, ToolkitCommandErrorCode.MissingArgument, "set-mode 需要 --mode balance|performance|silence。");
                if (profileName is not null) return Fail(hostFlags, ToolkitCommandErrorCode.UnexpectedArgument, "set-mode 不接受 --name。");
                if (!confirmed) return Fail(hostFlags, ToolkitCommandErrorCode.ConfirmationRequired, "set-mode 需要显式 --confirm 才会被接受。");
                break;
            case ToolkitCommandAction.RestoreAutomatic:
                if (profileName is not null) return Fail(hostFlags, ToolkitCommandErrorCode.UnexpectedArgument, "restore-automatic 不接受 --name。");
                if (mode is not null) return Fail(hostFlags, ToolkitCommandErrorCode.UnexpectedArgument, "restore-automatic 不接受 --mode。");
                if (!confirmed) return Fail(hostFlags, ToolkitCommandErrorCode.ConfirmationRequired, "restore-automatic 需要显式 --confirm 才会被接受。");
                break;
        }

        var normalizedName = IsValidProfileName(profileName) ? profileName!.Trim() : null;
        return new ToolkitCommandParseResult(new ToolkitCommand(action.Value, normalizedName, mode, confirmed), null,
            hostFlags, requestId);
    }

    private static ToolkitCommandAction? ParseAction(string? token) => (token ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "help" or "--help" or "-h" or "/?" => ToolkitCommandAction.Help,
        "status" => ToolkitCommandAction.Status,
        "apply-profile" => ToolkitCommandAction.ApplyProfile,
        "set-mode" => ToolkitCommandAction.SetMode,
        "restore-automatic" => ToolkitCommandAction.RestoreAutomatic,
        _ => null
    };

    private static PowerMode? ParseMode(string? token) => (token ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "balance" or "balanced" => PowerMode.Balance,
        "performance" => PowerMode.Performance,
        "silence" or "silent" => PowerMode.Silence,
        _ => null
    };

    private static bool IsValidProfileName(string? profileName)
    {
        var name = profileName?.Trim();
        return !string.IsNullOrEmpty(name) && name.Length <= 64 && name == profileName && !name.Any(char.IsControl);
    }

    private static bool TrySplitOption(string token, out string name, out string? inlineValue)
    {
        name = string.Empty;
        inlineValue = null;
        if (token.Length < 2 || token[0] != '-') return false;
        var separator = token.IndexOf('=');
        if (separator < 0)
        {
            name = token;
            return true;
        }
        name = token[..separator];
        inlineValue = token[(separator + 1)..];
        return name.Length > 1;
    }

    private static bool TryTakeValue(List<string> tokens, ref int index, string? inlineValue, out string value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue.Trim();
            return value.Length > 0;
        }
        if (index + 1 >= tokens.Count)
        {
            value = string.Empty;
            return false;
        }
        var next = tokens[index + 1];
        if (next.StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }
        index++;
        value = next.Trim();
        return value.Length > 0;
    }

    private static ToolkitCommandParseResult Fail(IReadOnlyList<string> hostFlags, ToolkitCommandErrorCode code, string message) =>
        new(null, new ToolkitCommandError(code, message), hostFlags);
}
