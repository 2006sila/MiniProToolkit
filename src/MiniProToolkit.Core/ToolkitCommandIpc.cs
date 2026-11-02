using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniProToolkit.Core;

/// <summary>
/// Why a relay attempt ended before the client handed over a single request byte. Both cases are pre-dispatch, so
/// nothing was executed; they are kept apart because only one of them may be reported as "the main instance is
/// busy": a pipe peer that fails the server-identity check is a refusal and must never be softened into "busy".
/// </summary>
public enum ToolkitCommandConnectFailure
{
    /// <summary>The connect window elapsed (or the channel broke) before a verified server accepted the connection.</summary>
    Unreachable,

    /// <summary>A pipe existed, but the connected peer is not the expected main instance, so no byte may be sent.</summary>
    UntrustedServer
}

/// <summary>
/// Wire status of one command exchange. The set is closed so the CLI never has to interpret free-form text:
/// <see cref="Ok"/> means "the running instance executed the read-only query or the write and reported success",
/// every other value is a failure that maps to a dedicated process exit code.
/// </summary>
public enum ToolkitCommandIpcStatus
{
    Ok,
    Rejected,
    Failed,
    TimedOut,
    Unavailable
}

/// <summary>
/// One request on the app command pipe. Every field is a closed value: the action token is one of
/// <see cref="ToolkitCommandLineParser.SupportedActions"/> or the UI-only <see cref="ToolkitCommandIpcProtocol.ActivateActionToken"/>,
/// <see cref="ProfileName"/> is the same 1–64 character name the parser accepts (never a path or a command line) and
/// <see cref="Mode"/> is one of the three power modes. <see cref="RequestId"/> is 32 hex characters and is mandatory
/// for the three write actions (it is what makes a retry of the same write recognizable); <c>status</c> and
/// <c>activate</c> work without one. Unknown JSON members are rejected by
/// <see cref="ToolkitCommandIpcProtocol.TryParseRequest"/>, so a caller cannot smuggle a hardware method name, a script
/// or an extra option into the running instance.
/// </summary>
public sealed record ToolkitCommandIpcRequest(
    [property: JsonPropertyName("protocol")] string? Protocol = null,
    [property: JsonPropertyName("requestId")] string? RequestId = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("profileName")] string? ProfileName = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("confirmed")] bool Confirmed = false);

/// <summary>
/// Read-only status snapshot built by the running main instance. Only already-displayed fields are copied: no file
/// path, no command line, no network adapter or process identity. Every value is nullable so "unknown" stays distinct
/// from a real zero.
/// </summary>
public sealed record ToolkitCommandIpcReport(
    [property: JsonPropertyName("powerMode")] string? PowerMode = null,
    [property: JsonPropertyName("cpuTemperatureC")] double? CpuTemperatureC = null,
    [property: JsonPropertyName("fan1Rpm")] int? Fan1Rpm = null,
    [property: JsonPropertyName("fan2Rpm")] int? Fan2Rpm = null,
    [property: JsonPropertyName("fanControlMode")] string? FanControlMode = null,
    [property: JsonPropertyName("targetDuty")] int? TargetDuty = null,
    [property: JsonPropertyName("targetRpm")] int? TargetRpm = null,
    [property: JsonPropertyName("rpmFeedbackState")] string? RpmFeedbackState = null,
    [property: JsonPropertyName("fanFault")] bool FanFault = false,
    [property: JsonPropertyName("guardianRunning")] bool GuardianRunning = false,
    [property: JsonPropertyName("curveEnabled")] bool CurveEnabled = false,
    [property: JsonPropertyName("manualLeaseActive")] bool ManualLeaseActive = false,
    [property: JsonPropertyName("appliedProfile")] string? AppliedProfile = null,
    [property: JsonPropertyName("demo")] bool Demo = false,
    [property: JsonPropertyName("displaySource")] string? DisplaySource = null,
    [property: JsonPropertyName("snapshotAgeSeconds")] double? SnapshotAgeSeconds = null);

/// <summary>Server answer. <see cref="Action"/> is the echoed action name, never a raw argument list.</summary>
public sealed record ToolkitCommandIpcResponse(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] ToolkitCommandIpcStatus Status,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("executed")] bool Executed,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("report")] ToolkitCommandIpcReport? Report = null);

/// <summary>Result of validating one request frame. A rejected request carries no command at all.</summary>
public sealed record ToolkitCommandIpcParseResult(
    ToolkitCommandIpcRequest? Request,
    ToolkitCommand? Command,
    ToolkitCommandIpcStatus Status,
    string Message)
{
    public bool Success => Status == ToolkitCommandIpcStatus.Ok && Request is not null && Command is not null;

    /// <summary>Echoable id: the validated request id, or an empty string when the frame could not be read.</summary>
    public string RequestId => Request?.RequestId ?? string.Empty;
}

/// <summary>
/// Pure protocol for the "control the running instance" channel: request/response models, strict JSON rules, frame
/// sizing and the per-user pipe name. Nothing here opens a pipe, reads a file or touches hardware; the app owns both
/// ends of the channel and only it may execute a parsed command.
/// </summary>
public static class ToolkitCommandIpcProtocol
{
    /// <summary>Bumped whenever the request/response shape changes; a mismatch is treated as "no instance".</summary>
    public const string Version = "miniprotoolkit-cli/1";

    /// <summary>4-byte little-endian payload length followed by the UTF-8 JSON body.</summary>
    public const int FrameHeaderBytes = 4;

    /// <summary>Hard cap on a single request frame; anything larger is rejected before parsing.</summary>
    public const int MaxRequestBytes = 4096;

    /// <summary>Hard cap on a single response frame; the server falls back to a minimal rejection above it.</summary>
    public const int MaxResponseBytes = 8192;

    /// <summary>Response text is truncated to this many characters so an exception text cannot flood the client.</summary>
    public const int MaxMessageChars = 400;

    /// <summary>
    /// Worst-case wall clock the server may spend on one command: it starts the handler within 10 s and bounds a write
    /// command's execution to 30 s (<c>AppCommandPipeServer</c>). A client budget derived from this number cannot
    /// expire while the server is still able to run and answer the command, so "slow" is never reported as "failed".
    /// </summary>
    public const int ServerWorstCaseMilliseconds = 40_000;

    /// <summary>
    /// Default client-side answer budget: the server worst case plus a 5 s margin for pipe scheduling, dispatcher
    /// backlog and process startup. Shrinking this below <see cref="ServerWorstCaseMilliseconds"/> would turn a
    /// successful command into a false "timed out" (and the state on the EC may already have changed).
    /// </summary>
    public const int DefaultClientResponseTimeoutMilliseconds = ServerWorstCaseMilliseconds + 5_000;

    private const int RequestIdLength = 32;

    private static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Fixed, path-free pipe name. The suffix is a truncated hash of the caller-supplied user discriminator, so two
    /// users on one machine get different pipes without the name exposing an account or a directory.
    /// </summary>
    public static string PipeNameFor(string userDiscriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDiscriminator);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"MiniProToolkit.Cli.v1|{userDiscriminator}"));
        return "MiniProToolkit.Cli.v1." + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>Fresh one-shot request id (32 hex characters). A server handles exactly one request per connection.</summary>
    public static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>True when the argument is exactly 32 hexadecimal characters (the only accepted request id shape).</summary>
    public static bool IsValidRequestId(string? requestId) =>
        requestId is { Length: RequestIdLength } && requestId.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// True for the actions whose execution must be deduplicated by request id. Read-only <c>status</c> is a fresh
    /// snapshot on every call and the UI-only <c>activate</c> only raises the window, so neither is ever replayed;
    /// every write that may change the EC state is.
    /// </summary>
    public static bool RequiresDispatchDeduplication(ToolkitCommandAction action) =>
        action is ToolkitCommandAction.ApplyProfile or ToolkitCommandAction.SetMode or
            ToolkitCommandAction.RestoreAutomatic;

    public static string ActionToken(ToolkitCommandAction action) => action switch
    {
        ToolkitCommandAction.Status => "status",
        ToolkitCommandAction.ApplyProfile => "apply-profile",
        ToolkitCommandAction.SetMode => "set-mode",
        ToolkitCommandAction.RestoreAutomatic => "restore-automatic",
        ToolkitCommandAction.Activate => ActivateActionToken,
        ToolkitCommandAction.Help => "help",
        _ => "none"
    };

    /// <summary>
    /// Wire token of the UI-only activation action. It is not a command line action: only the app itself sends it, and
    /// only to make the running instance show/activate its own window, so it can never name a value or a hardware path.
    /// </summary>
    public const string ActivateActionToken = "activate";

    public static string ModeToken(PowerMode mode) => mode switch
    {
        PowerMode.Balance => "balance",
        PowerMode.Performance => "performance",
        PowerMode.Silence => "silence",
        _ => "unknown"
    };

    /// <summary>Maps a parsed command onto the wire request. The action name and values already passed the parser.</summary>
    public static ToolkitCommandIpcRequest CreateRequest(ToolkitCommand command, string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new ToolkitCommandIpcRequest(
            Version,
            string.IsNullOrWhiteSpace(requestId) ? NewRequestId() : requestId,
            ActionToken(command.Action),
            command.ProfileName,
            command.Mode is { } mode ? ModeToken(mode) : null,
            command.Confirmed);
    }

    /// <summary>
    /// Builds the UI-only activation request a second launch sends to the running instance. It carries no value field
    /// and no confirmation flag, so the wire frame can only mean "show your window".
    /// </summary>
    public static ToolkitCommandIpcRequest CreateActivateRequest(string? requestId = null) =>
        new(Version,
            string.IsNullOrWhiteSpace(requestId) ? NewRequestId() : requestId,
            ActivateActionToken,
            null,
            null,
            false);

    /// <summary>
    /// Validates one request frame. Unknown members, an unknown action, a mismatched protocol, a malformed body and a
    /// write action without confirmation are all rejected here, before anything can be dispatched to the UI thread.
    /// The returned message never echoes a received value.
    /// </summary>
    public static ToolkitCommandIpcParseResult TryParseRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return Reject("请求为空。");
        if (payload.Length > MaxRequestBytes)
            return Reject("请求超过允许的大小。");

        ToolkitCommandIpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ToolkitCommandIpcRequest>(payload, RequestOptions);
        }
        catch (JsonException)
        {
            return Reject("请求不是合法的协议消息。");
        }
        catch (NotSupportedException)
        {
            return Reject("请求不是合法的协议消息。");
        }

        if (request is null)
            return Reject("请求不是合法的协议消息。");
        if (!string.Equals(request.Protocol, Version, StringComparison.Ordinal))
            return Reject("协议版本不匹配。");
        if (request.RequestId is not null && !IsValidRequestId(request.RequestId))
            return Reject("请求标识不合法。");

        if (string.IsNullOrWhiteSpace(request.Action))
            return Reject("请求动作不在白名单内。");

        var action = request.Action.Trim().ToLowerInvariant();

        // UI-only activation of the running instance: no value field, no confirmation flag and no command line action
        // behind it. It is validated here so it can never be turned into a hardware request.
        if (action == ActivateActionToken)
        {
            if (request.ProfileName is not null || request.Mode is not null || request.Confirmed)
                return Reject("activate 不接受参数。");
            return new ToolkitCommandIpcParseResult(request, new ToolkitCommand(ToolkitCommandAction.Activate),
                ToolkitCommandIpcStatus.Ok, "accepted");
        }

        if (!ToolkitCommandLineParser.IsSupportedAction(action))
            return Reject("请求动作不在白名单内。");
        // Field/action consistency first: a value that does not belong to the action is refused instead of ignored.
        switch (action)
        {
            case "status" or "restore-automatic":
                if (request.ProfileName is not null || request.Mode is not null)
                    return Reject("该动作不接受参数。");
                break;
            case "apply-profile":
                if (request.Mode is not null)
                    return Reject("apply-profile 不接受模式参数。");
                break;
            case "set-mode":
                if (request.ProfileName is not null)
                    return Reject("set-mode 不接受方案名。");
                break;
            default:
                return Reject("请求动作不在白名单内。");
        }

        if (action == "restore-automatic" && request.Confirmed != true)
            return Reject("restore-automatic 需要显式确认。");
        if (action == "set-mode" && request.Confirmed != true)
            return Reject("set-mode 需要显式确认。");
        if (action == "apply-profile" && request.Confirmed != true)
            return Reject("apply-profile 需要显式确认。");

        // Reuse the command line parser so profile-name/mode/confirmation rules have exactly one implementation.
        // The inline "=" form keeps a value from ever being re-read as an option token.
        var arguments = new List<string> { action };
        if (request.ProfileName is not null)
        {
            arguments.Add("--name=" + request.ProfileName);
        }
        if (request.Mode is not null)
        {
            arguments.Add("--mode=" + request.Mode);
        }
        if (request.Confirmed)
            arguments.Add("--confirm");

        var parsed = ToolkitCommandLineParser.Parse(arguments);
        if (!parsed.Success || parsed.Command is null)
            return new ToolkitCommandIpcParseResult(request, null, ToolkitCommandIpcStatus.Rejected,
                parsed.Error?.Message ?? "请求未被接受。");

        // A write action has to name its exchange: the request id is the only handle the server has for "this exact
        // write was already executed", so retrying the very same frame must never run it a second time. Read-only
        // status and the UI-only activate stay usable without one.
        if (RequiresDispatchDeduplication(parsed.Command.Action) && !IsValidRequestId(request.RequestId))
            return new ToolkitCommandIpcParseResult(request, null, ToolkitCommandIpcStatus.Rejected,
                "写动作需要合法的请求标识。");

        // Defense in depth: the command the executor receives must still be confirmed for every write action.
        if (parsed.Command.RequiresConfirmation && !parsed.Command.Confirmed)
            return new ToolkitCommandIpcParseResult(request, null, ToolkitCommandIpcStatus.Rejected,
                "该动作需要显式确认。");
        if (parsed.Command.Action == ToolkitCommandAction.Help)
            return new ToolkitCommandIpcParseResult(request, null, ToolkitCommandIpcStatus.Rejected,
                "help 不需要通过运行中的实例执行。");

        return new ToolkitCommandIpcParseResult(request, parsed.Command, ToolkitCommandIpcStatus.Ok, "accepted");
    }

    public static byte[] SerializeRequest(ToolkitCommandIpcRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, RequestOptions);

    /// <summary>Serializes a response, falling back to a minimal rejection when the payload would exceed the cap.</summary>
    public static byte[] SerializeResponse(ToolkitCommandIpcResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, ResponseOptions);
        if (payload.Length <= MaxResponseBytes)
            return payload;
        return JsonSerializer.SerializeToUtf8Bytes(
            new ToolkitCommandIpcResponse(response.Protocol, response.RequestId, response.Status, response.Action,
                response.Executed, "应答过大，已省略内容。", null),
            ResponseOptions);
    }

    /// <summary>
    /// Parses a response frame. Returns null when the body is not a compatible response or echoes a different request
    /// id; the client then reports <see cref="ConnectionLostAfterSend"/> (the command may already have run), never a
    /// retry, never a fallback to hardware and never "no instance".
    /// </summary>
    public static ToolkitCommandIpcResponse? TryParseResponse(ReadOnlySpan<byte> payload, string? expectedRequestId)
    {
        if (payload.Length == 0 || payload.Length > MaxResponseBytes)
            return null;
        ToolkitCommandIpcResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ToolkitCommandIpcResponse>(payload, ResponseOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        if (response is null)
            return null;
        if (!string.Equals(response.Protocol, Version, StringComparison.Ordinal))
            return null;
        if (!string.IsNullOrEmpty(response.RequestId) &&
            !string.Equals(response.RequestId, expectedRequestId, StringComparison.Ordinal))
            return null;
        return response;
    }

    public static ToolkitCommandIpcResponse Ok(string requestId, ToolkitCommandAction action, string message,
        ToolkitCommandIpcReport? report = null) =>
        Build(ToolkitCommandIpcStatus.Ok, requestId, action, true, message, report);

    public static ToolkitCommandIpcResponse Rejected(string requestId, ToolkitCommandAction action, string message) =>
        Build(ToolkitCommandIpcStatus.Rejected, requestId, action, false, message, null);

    public static ToolkitCommandIpcResponse Failed(string requestId, ToolkitCommandAction action, string message) =>
        Build(ToolkitCommandIpcStatus.Failed, requestId, action, false, message, null);

    /// <summary>
    /// Answer for "the request was on the wire but no answer arrived in time". <see cref="ToolkitCommandIpcResponse.Executed"/>
    /// is always false: the command may already have run, so the text asks the caller to confirm with <c>status</c>.
    /// </summary>
    public static ToolkitCommandIpcResponse TimedOut(string requestId, ToolkitCommandAction action) =>
        Build(ToolkitCommandIpcStatus.TimedOut, requestId, action, false,
            "运行中的实例未在时限内完成该命令；命令可能已经执行，请用 status 复核。", null);

    /// <summary>
    /// Answer for "the request was already on the wire when the channel broke": a write/read failure, an EOF instead
    /// of an answer, or an answer that cannot be parsed. The running instance may have executed the command, so this
    /// must never be reported as "nothing happened": the status stays <see cref="ToolkitCommandIpcStatus.TimedOut"/>
    /// (exit code 6, never the "no instance" code 4) and the text asks the caller to confirm with <c>status</c>.
    /// </summary>
    public static ToolkitCommandIpcResponse ConnectionLostAfterSend(string requestId, ToolkitCommandAction action) =>
        Build(ToolkitCommandIpcStatus.TimedOut, requestId, action, false,
            "命令已发送，但命令通道在收到应答前断开或应答无法识别，无法确认是否执行；命令可能已经执行，请用 status 复核。",
            null);

    public static ToolkitCommandIpcResponse Unavailable(string requestId, ToolkitCommandAction action, string message) =>
        Build(ToolkitCommandIpcStatus.Unavailable, requestId, action, false, message, null);

    /// <summary>Answer text for "no main instance could be found": exit code 4, nothing was sent.</summary>
    public const string NoInstanceMessage =
        "没有找到正在运行的主实例（MiniProToolkit 未启动，或命令通道未就绪），命令未执行。";

    /// <summary>
    /// Answer text for "another process holds the instance lock but its command pipe did not answer in time". The
    /// instance is known to exist, so "not started" would be a false report; nothing was sent, so "may already have
    /// executed" would be equally wrong. It therefore says "busy or temporarily unreachable" and that the command was
    /// not sent, which keeps a retry safe.
    /// </summary>
    public const string BusyOrUnreachableMessage = "主实例忙或暂时无法连接，命令未发送；可稍后重试。";

    /// <summary>
    /// Classifies a failure that happened before any request byte was sent. Pure on purpose, so the exit-code contract
    /// is testable without opening a pipe, starting the app or touching hardware.
    /// <para>
    /// An unreachable channel while another process holds the instance lock means the main instance exists but is busy
    /// (or its channel is not ready): that is reported as <see cref="ToolkitCommandIpcStatus.TimedOut"/> (exit code 6,
    /// never "no instance" 4) with the <see cref="BusyOrUnreachableMessage"/> text - and never as "may already have
    /// executed", because no byte left this process. Without that lock evidence the answer stays "no instance" (exit
    /// code 4); an untrusted peer is always refused (exit code 4) and never called "busy".
    /// </para>
    /// </summary>
    public static ToolkitCommandIpcResponse ClassifyPreDispatchFailure(string requestId, ToolkitCommandAction action,
        bool instanceKnownByLock = false,
        ToolkitCommandConnectFailure failure = ToolkitCommandConnectFailure.Unreachable)
    {
        // A wrong server is a refusal, not a load signal: it must not disguise itself as "the real instance is busy".
        if (failure == ToolkitCommandConnectFailure.UntrustedServer)
            return Unavailable(requestId, action, NoInstanceMessage);
        if (instanceKnownByLock)
            return Build(ToolkitCommandIpcStatus.TimedOut, requestId, action, false, BusyOrUnreachableMessage, null);
        return Unavailable(requestId, action, NoInstanceMessage);
    }

    /// <summary>
    /// Answer for a duplicate request id whose first attempt is still running (or ended without an answer): the
    /// command was <em>not</em> executed a second time, the state may already have changed, so this is a timeout-shaped
    /// answer (exit code 6) that asks the caller to re-check with <c>status</c>.
    /// </summary>
    public static ToolkitCommandIpcResponse DuplicateInFlight(string requestId, ToolkitCommandAction action) =>
        Build(ToolkitCommandIpcStatus.TimedOut, requestId, action, false,
            "同一请求标识的命令已经收到并在处理中，本次未再次执行；命令可能已经执行，请用 status 复核。", null);

    /// <summary>
    /// Answer for a duplicate request id that already completed: the recorded answer of the first execution, marked
    /// with <c>executed = false</c> because nothing ran again. Only the already log-safe fields of the recorded answer
    /// are reused (status, action, message, report) - no new text and never a raw command line.
    /// </summary>
    public static ToolkitCommandIpcResponse Replayed(string requestId, ToolkitCommandIpcResponse cached)
    {
        ArgumentNullException.ThrowIfNull(cached);
        var message = string.IsNullOrWhiteSpace(cached.Message)
            ? "该请求标识已经处理过，未再次执行；以下是首次执行的结果。"
            : "该请求标识已经处理过，未再次执行；以下是首次执行的结果。" + cached.Message;
        return BuildWithToken(cached.Status, requestId, cached.Action, false, message, cached.Report);
    }

    /// <summary>One log-safe line: action name, status and whether something ran. Never a value or a raw argument.</summary>
    public static string DescribeResponse(ToolkitCommandIpcResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return $"ipc action={response.Action} status={response.Status} executed={response.Executed}";
    }

    /// <summary>Stable key=value lines for --quiet scripts; unknown values are rendered as "unknown".</summary>
    public static IReadOnlyList<string> DescribeReport(ToolkitCommandIpcReport? report)
    {
        if (report is null) return [];
        return
        [
            $"powerMode={Text(report.PowerMode)}",
            $"cpuTemperatureC={Number(report.CpuTemperatureC)}",
            $"fan1Rpm={Number(report.Fan1Rpm)}",
            $"fan2Rpm={Number(report.Fan2Rpm)}",
            $"fanControlMode={Text(report.FanControlMode)}",
            $"targetDuty={Number(report.TargetDuty)}",
            $"targetRpm={Number(report.TargetRpm)}",
            $"rpmFeedbackState={Text(report.RpmFeedbackState)}",
            $"fanFault={report.FanFault}",
            $"guardianRunning={report.GuardianRunning}",
            $"curveEnabled={report.CurveEnabled}",
            $"manualLeaseActive={report.ManualLeaseActive}",
            $"demo={report.Demo}",
            $"displaySource={Text(report.DisplaySource)}"
        ];

        static string Number<T>(T? value) where T : struct =>
            value.HasValue ? Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture)! : "unknown";

        static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static ToolkitCommandIpcParseResult Reject(string message) =>
        new(null, null, ToolkitCommandIpcStatus.Rejected, message);

    private static ToolkitCommandIpcResponse Build(ToolkitCommandIpcStatus status, string requestId,
        ToolkitCommandAction action, bool executed, string message, ToolkitCommandIpcReport? report) =>
        BuildWithToken(status, requestId, ActionToken(action), executed, message, report);

    /// <summary>Same as <see cref="Build"/> but keeps an already validated action token (used to echo a replayed answer).</summary>
    private static ToolkitCommandIpcResponse BuildWithToken(ToolkitCommandIpcStatus status, string requestId,
        string actionToken, bool executed, string message, ToolkitCommandIpcReport? report) =>
        new(Version, requestId ?? string.Empty, status, actionToken, executed, Truncate(message), report);

    private static string Truncate(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "没有更多信息。" : message.Trim();
        return text.Length <= MaxMessageChars ? text : text[..MaxMessageChars];
    }
}
