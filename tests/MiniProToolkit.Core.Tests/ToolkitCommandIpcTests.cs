using System.Text;
using System.Text.Json;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pure protocol tests for the "control the running instance" channel. Nothing here opens a pipe, starts the app,
/// touches the EC or opens a Guardian lease: only the request/response models, their strict validation and the
/// framing/exit-code contract are exercised.
/// </summary>
public sealed class ToolkitCommandIpcTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static string Body(string fields) =>
        "{\"protocol\":\"" + ToolkitCommandIpcProtocol.Version + "\"," + fields + "}";

    [Fact]
    public void StatusRequestNeedsNoConfirmation()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var request = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, id, "status");
        var parsed = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request));

        Assert.True(parsed.Success);
        Assert.Equal(ToolkitCommandAction.Status, parsed.Command!.Action);
        Assert.False(parsed.Command.Confirmed);
        Assert.True(parsed.Command.IsReadOnly);
        Assert.Equal(id, parsed.RequestId);
    }

    [Fact]
    public void ConfirmedWriteActionsMapOntoTheParsedCommand()
    {
        var profile = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version,
            ToolkitCommandIpcProtocol.NewRequestId(), "apply-profile", "Gaming", null, true);
        var apply = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(profile));
        Assert.True(apply.Success);
        Assert.Equal(ToolkitCommandAction.ApplyProfile, apply.Command!.Action);
        Assert.Equal("Gaming", apply.Command.ProfileName);
        Assert.True(apply.Command.Confirmed);

        var mode = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version,
            ToolkitCommandIpcProtocol.NewRequestId(), "set-mode", null, "silence", true);
        var setMode = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(mode));
        Assert.True(setMode.Success);
        Assert.Equal(PowerMode.Silence, setMode.Command!.Mode);

        var restore = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version,
            ToolkitCommandIpcProtocol.NewRequestId(), "restore-automatic", null, null, true);
        var restored = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(restore));
        Assert.True(restored.Success);
        Assert.True(restored.Command!.RequiresConfirmation);
    }

    [Fact]
    public void WriteActionsWithoutConfirmationAreRejected()
    {
        foreach (var request in new[]
                 {
                     new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, null, "apply-profile", "Gaming"),
                     new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, null, "set-mode", null, "balance"),
                     new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, null, "restore-automatic")
                 })
        {
            var parsed = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request));
            Assert.False(parsed.Success);
            Assert.Equal(ToolkitCommandIpcStatus.Rejected, parsed.Status);
            Assert.Null(parsed.Command);
            // The rejection text must not echo a submitted value.
            Assert.DoesNotContain("Gaming", parsed.Message);
            Assert.DoesNotContain("balance", parsed.Message);
        }
    }

    [Theory]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"SetFanDuty\",\"confirmed\":true}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"exec\",\"confirm\":true}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"help\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"status\",\"profileName\":\"Gaming\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"set-mode\",\"profileName\":\"Gaming\",\"mode\":\"balance\",\"confirmed\":true}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"apply-profile\",\"mode\":\"balance\",\"confirmed\":true}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"status\",\"unknownField\":\"x\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/2\",\"action\":\"status\"}")]
    [InlineData("{\"action\":\"status\"}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"set-mode\",\"mode\":\"turbo\",\"confirmed\":true}")]
    [InlineData("{\"protocol\":\"miniprotoolkit-cli/1\",\"action\":\"set-mode\",\"mode\":\"balance\",\"confirmed\":true,\"requestId\":\"bad id\"}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void MalformedOrUnknownRequestsAreRejected(string payload)
    {
        var parsed = ToolkitCommandIpcProtocol.TryParseRequest(Utf8(payload));
        Assert.False(parsed.Success);
        Assert.Equal(ToolkitCommandIpcStatus.Rejected, parsed.Status);
        Assert.Null(parsed.Command);
        Assert.DoesNotContain("SetFanDuty", parsed.Message);
        Assert.DoesNotContain("turbo", parsed.Message);
    }

    [Fact]
    public void ProfileNameMustStillPassTheParserRules()
    {
        foreach (var name in new[] { "   ", new string('x', 65), "bad\u0007name" })
        {
            var request = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version,
                ToolkitCommandIpcProtocol.NewRequestId(), "apply-profile", name, null, true);
            var parsed = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request));
            Assert.False(parsed.Success);
            Assert.Equal(ToolkitCommandIpcStatus.Rejected, parsed.Status);
        }
    }

    [Fact]
    public void OversizedRequestAndEmptyFrameAreRejectedBeforeParsing()
    {
        var tooLarge = new byte[ToolkitCommandIpcProtocol.MaxRequestBytes + 1];
        Array.Fill(tooLarge, (byte)' ');
        Assert.Equal(ToolkitCommandIpcStatus.Rejected,
            ToolkitCommandIpcProtocol.TryParseRequest(tooLarge).Status);
        Assert.Equal(ToolkitCommandIpcStatus.Rejected,
            ToolkitCommandIpcProtocol.TryParseRequest(ReadOnlySpan<byte>.Empty).Status);
    }

    [Fact]
    public void ResponseRoundTripKeepsTheRequestIdAndNeverEchoesAValue()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var report = new ToolkitCommandIpcReport("balance", 52.5, 2180, 0, "firmwareautomatic", 0, null,
            "disabled", false, true, false, false, "Gaming", false, "ecenergymeter", 1.5);
        var response = ToolkitCommandIpcProtocol.Ok(id, ToolkitCommandAction.Status, "已读取状态。", report);
        var payload = ToolkitCommandIpcProtocol.SerializeResponse(response);

        Assert.True(payload.Length <= ToolkitCommandIpcProtocol.MaxResponseBytes);
        var parsed = ToolkitCommandIpcProtocol.TryParseResponse(payload, id);
        Assert.NotNull(parsed);
        Assert.Equal(ToolkitCommandIpcStatus.Ok, parsed!.Status);
        Assert.Equal("status", parsed.Action);
        Assert.True(parsed.Executed);
        Assert.Equal(2180, parsed.Report!.Fan1Rpm);
        Assert.Equal(52.5, parsed.Report.CpuTemperatureC);

        // A different request id means the answer belongs to another exchange and must be discarded.
        Assert.Null(ToolkitCommandIpcProtocol.TryParseResponse(payload, ToolkitCommandIpcProtocol.NewRequestId()));
        Assert.Null(ToolkitCommandIpcProtocol.TryParseResponse(Utf8("{\"protocol\":\"other\"}"), id));
    }

    [Fact]
    public void OversizedResponsesFallBackToABoundedMessage()
    {
        var huge = new string('x', ToolkitCommandIpcProtocol.MaxResponseBytes);
        var response = ToolkitCommandIpcProtocol.Failed(ToolkitCommandIpcProtocol.NewRequestId(),
            ToolkitCommandAction.SetMode, huge);
        var payload = ToolkitCommandIpcProtocol.SerializeResponse(response);

        Assert.True(payload.Length <= ToolkitCommandIpcProtocol.MaxResponseBytes);
        var parsed = ToolkitCommandIpcProtocol.TryParseResponse(payload, response.RequestId);
        Assert.NotNull(parsed);
        Assert.Equal(ToolkitCommandIpcStatus.Failed, parsed!.Status);
        Assert.True(parsed.Message.Length <= ToolkitCommandIpcProtocol.MaxMessageChars);
    }

    [Fact]
    public void WriteActionsRequireAWellFormedRequestId()
    {
        // The request id is the only handle for "this exact write already ran", so a write without one is refused.
        foreach (var action in new[] { "apply-profile", "set-mode", "restore-automatic" })
        {
            foreach (var id in new string?[] { null, string.Empty, "bad id", new string('a', 31), new string('z', 32) })
            {
                var request = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, id, action,
                    action == "apply-profile" ? "Gaming" : null,
                    action == "set-mode" ? "balance" : null, true);
                var parsed = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request));

                Assert.False(parsed.Success);
                Assert.Equal(ToolkitCommandIpcStatus.Rejected, parsed.Status);
                Assert.Null(parsed.Command);
                Assert.Contains("请求标识", parsed.Message);
            }
        }

        // Read-only status and the UI-only activate stay usable without an id: neither needs deduplication.
        Assert.True(ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(
            new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, null, "status"))).Success);
        Assert.True(ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(
            new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, null, "activate"))).Success);
    }

    [Fact]
    public void AWriteCarryingItsRequestIdStillParsesAndEchoesItBack()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var request = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version, id, "restore-automatic", null, null, true);
        var parsed = ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request));

        Assert.True(parsed.Success);
        Assert.Equal(ToolkitCommandAction.RestoreAutomatic, parsed.Command!.Action);
        Assert.Equal(id, parsed.RequestId);
        Assert.True(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(parsed.Command.Action));
    }

    [Fact]
    public void RequestsAreBuiltFromParsedCommandsOnly()
    {
        var result = ToolkitCommandLineParser.Parse(["set-mode", "--mode", "performance", "--confirm"]);
        Assert.True(result.Success);
        var request = ToolkitCommandIpcProtocol.CreateRequest(result.Command!);

        Assert.Equal(ToolkitCommandIpcProtocol.Version, request.Protocol);
        Assert.Equal("set-mode", request.Action);
        Assert.Equal("performance", request.Mode);
        Assert.Null(request.ProfileName);
        Assert.True(request.Confirmed);
        Assert.Equal(32, request.RequestId!.Length);
        Assert.True(ToolkitCommandIpcProtocol.TryParseRequest(ToolkitCommandIpcProtocol.SerializeRequest(request)).Success);
    }

    [Fact]
    public void ModeTokensStayInsideTheSupportedSet()
    {
        Assert.Equal("balance", ToolkitCommandIpcProtocol.ModeToken(PowerMode.Balance));
        Assert.Equal("performance", ToolkitCommandIpcProtocol.ModeToken(PowerMode.Performance));
        Assert.Equal("silence", ToolkitCommandIpcProtocol.ModeToken(PowerMode.Silence));
    }

    [Fact]
    public void PipeNamesArePerUserAndFreeOfPathsOrAccountNames()
    {
        var first = ToolkitCommandIpcProtocol.PipeNameFor(@"CONTOSO\alice");
        var same = ToolkitCommandIpcProtocol.PipeNameFor(@"CONTOSO\alice");
        var other = ToolkitCommandIpcProtocol.PipeNameFor(@"CONTOSO\bob");

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.DoesNotContain("alice", first);
        Assert.DoesNotContain("CONTOSO", first);
        Assert.DoesNotContain("\\", first);
        Assert.StartsWith("MiniProToolkit.Cli.v1.", first);
        Assert.True(first.Length < 60);
        Assert.Throws<ArgumentException>(() => ToolkitCommandIpcProtocol.PipeNameFor(" "));
    }

    [Fact]
    public void DescribeReportIsStableAndMarksUnknownValues()
    {
        var lines = ToolkitCommandIpcProtocol.DescribeReport(new ToolkitCommandIpcReport(PowerMode: "balance"));
        Assert.Contains("powerMode=balance", lines);
        Assert.Contains("cpuTemperatureC=unknown", lines);
        Assert.Contains("guardianRunning=False", lines);
        Assert.Empty(ToolkitCommandIpcProtocol.DescribeReport(null));

        var line = ToolkitCommandIpcProtocol.DescribeResponse(
            ToolkitCommandIpcProtocol.Failed("id", ToolkitCommandAction.ApplyProfile, "未执行。"));
        Assert.Contains("action=apply-profile", line);
        Assert.Contains("status=Failed", line);
        Assert.Contains("executed=False", line);
    }

    [Fact]
    public void RejectionTextNeverContainsRawJson()
    {
        var payload = Utf8(Body("\"action\":\"apply-profile\",\"profileName\":\"C:\\\\Users\\\\Alice\",\"confirmed\":true,\"extra\":1"));
        var parsed = ToolkitCommandIpcProtocol.TryParseRequest(payload);
        Assert.False(parsed.Success);
        Assert.DoesNotContain("Alice", parsed.Message);
        Assert.DoesNotContain("C:\\", parsed.Message);
        Assert.DoesNotContain("extra", parsed.Message);
    }

    [Fact]
    public void SerializedRequestsAreJsonObjectsWithCamelCaseMembers()
    {
        var request = new ToolkitCommandIpcRequest(ToolkitCommandIpcProtocol.Version,
            ToolkitCommandIpcProtocol.NewRequestId(), "apply-profile", "Gaming", null, true);
        var json = Encoding.UTF8.GetString(ToolkitCommandIpcProtocol.SerializeRequest(request));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal("apply-profile", document.RootElement.GetProperty("action").GetString());
        Assert.True(document.RootElement.GetProperty("confirmed").GetBoolean());
        Assert.Equal(ToolkitCommandIpcProtocol.Version, document.RootElement.GetProperty("protocol").GetString());
    }

    /// <summary>
    /// The client budget has to outlive the server's worst case with a margin, otherwise a slow but successful write
    /// is reported as a timeout. Pinned here so the constant cannot be shortened without failing a test.
    /// </summary>
    [Fact]
    public void ClientResponseBudgetOutlivesTheServerWorstCase()
    {
        Assert.Equal(40_000, ToolkitCommandIpcProtocol.ServerWorstCaseMilliseconds);
        Assert.True(ToolkitCommandIpcProtocol.DefaultClientResponseTimeoutMilliseconds >= 45_000);
        Assert.True(ToolkitCommandIpcProtocol.DefaultClientResponseTimeoutMilliseconds >=
                    ToolkitCommandIpcProtocol.ServerWorstCaseMilliseconds + 5_000);
    }

    /// <summary>
    /// A failure after the request was sent (write/read error, EOF, unusable answer) must map onto "timed out" and
    /// never onto "no instance": the running instance may already have changed the EC state.
    /// </summary>
    [Theory]
    [InlineData(ToolkitCommandAction.Status)]
    [InlineData(ToolkitCommandAction.ApplyProfile)]
    [InlineData(ToolkitCommandAction.SetMode)]
    [InlineData(ToolkitCommandAction.RestoreAutomatic)]
    public void PostConnectLossIsReportedAsTimedOutAndAsksForStatus(ToolkitCommandAction action)
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var response = ToolkitCommandIpcProtocol.ConnectionLostAfterSend(id, action);

        Assert.Equal(ToolkitCommandIpcStatus.TimedOut, response.Status);
        Assert.NotEqual(ToolkitCommandIpcStatus.Unavailable, response.Status);
        Assert.False(response.Executed);
        Assert.Equal(id, response.RequestId);
        Assert.Equal(ToolkitCommandIpcProtocol.ActionToken(action), response.Action);
        Assert.Equal(ToolkitCommandIpcProtocol.Version, response.Protocol);
        Assert.Contains("可能已经执行", response.Message);
        Assert.Contains("status", response.Message);
        Assert.True(response.Message.Length <= ToolkitCommandIpcProtocol.MaxMessageChars);

        // Exit codes stay untouched: a post-connect loss is 6, "no instance" is 4.
        Assert.Equal(6, ToolkitCommandExitCodes.CommandTimedOut);
        Assert.Equal(4, ToolkitCommandExitCodes.NoRunningInstance);
    }

    [Fact]
    public void PostConnectLossAnswerSurvivesTheWireAndKeepsStrictRequestIdMatching()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        var payload = ToolkitCommandIpcProtocol.SerializeResponse(
            ToolkitCommandIpcProtocol.ConnectionLostAfterSend(id, ToolkitCommandAction.ApplyProfile));

        var parsed = ToolkitCommandIpcProtocol.TryParseResponse(payload, id);
        Assert.NotNull(parsed);
        Assert.Equal(ToolkitCommandIpcStatus.TimedOut, parsed!.Status);
        Assert.False(parsed.Executed);
        // Strict requestId check is unchanged: an answer for another exchange is still discarded (a discarded answer is
        // treated by the client as a post-connect loss, never as "executed").
        Assert.Null(ToolkitCommandIpcProtocol.TryParseResponse(payload, ToolkitCommandIpcProtocol.NewRequestId()));
    }

    [Fact]
    public void EveryTimeoutAnswerTellsTheCallerToRecheckWithStatus()
    {
        var id = ToolkitCommandIpcProtocol.NewRequestId();
        foreach (var response in new[]
                 {
                     ToolkitCommandIpcProtocol.TimedOut(id, ToolkitCommandAction.Status),
                     ToolkitCommandIpcProtocol.ConnectionLostAfterSend(id, ToolkitCommandAction.Status)
                 })
        {
            Assert.Equal(ToolkitCommandIpcStatus.TimedOut, response.Status);
            Assert.Contains("可能已经执行", response.Message);
            Assert.Contains("status", response.Message);
        }
    }

    [Fact]
    public void ExitCodesStayDistinctAndNonZeroForFailures()    {
        Assert.Equal(0, ToolkitCommandExitCodes.Success);
        Assert.Equal(2, ToolkitCommandExitCodes.ParseError);
        Assert.Equal(4, ToolkitCommandExitCodes.NoRunningInstance);
        Assert.Equal(5, ToolkitCommandExitCodes.CommandRejected);
        Assert.Equal(6, ToolkitCommandExitCodes.CommandTimedOut);
        Assert.True(ToolkitCommandExitCodes.RelayPending < 0);
    }
}
