using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

public sealed class ToolkitCommandLineTests
{
    [Fact]
    public void StatusIsAcceptedWithoutConfirmation()
    {
        var result = ToolkitCommandLineParser.Parse(["status"]);
        Assert.True(result.Success);
        Assert.Null(result.Error);
        var command = Assert.IsType<ToolkitCommand>(result.Command);
        Assert.Equal(ToolkitCommandAction.Status, command.Action);
        Assert.True(command.IsReadOnly);
        Assert.False(command.RequiresConfirmation);
        Assert.Empty(result.HostFlags);
    }

    [Fact]
    public void ApplyProfileRequiresNameAndExplicitConfirmation()
    {
        var missingName = ToolkitCommandLineParser.Parse(["apply-profile", "--confirm"]);
        Assert.False(missingName.Success);
        Assert.Equal(ToolkitCommandErrorCode.MissingArgument, missingName.Error?.Code);

        var missingConfirm = ToolkitCommandLineParser.Parse(["apply-profile", "--name", "Gaming"]);
        Assert.False(missingConfirm.Success);
        Assert.Equal(ToolkitCommandErrorCode.ConfirmationRequired, missingConfirm.Error?.Code);
        Assert.Null(missingConfirm.Command);

        var accepted = ToolkitCommandLineParser.Parse(["apply-profile", "--name=Gaming", "--confirm"]);
        Assert.True(accepted.Success);
        var command = Assert.IsType<ToolkitCommand>(accepted.Command);
        Assert.Equal(ToolkitCommandAction.ApplyProfile, command.Action);
        Assert.Equal("Gaming", command.ProfileName);
        Assert.True(command.Confirmed);
        Assert.True(command.RequiresConfirmation);
        Assert.False(command.IsReadOnly);
        Assert.Equal("apply-profile profile=Gaming confirmed=True", command.SafeSummary);
    }

    [Fact]
    public void SetModeParsesWhitelistedModesOnly()
    {
        foreach (var (raw, expected) in new[]
                 {
                     ("balance", PowerMode.Balance),
                     ("Performance", PowerMode.Performance),
                     ("SILENCE", PowerMode.Silence)
                 })
        {
            var result = ToolkitCommandLineParser.Parse(["set-mode", "--mode", raw, "--confirm"]);
            Assert.True(result.Success);
            Assert.Equal(expected, result.Command!.Mode!.Value);
            Assert.Equal(ToolkitCommandAction.SetMode, result.Command.Action);
        }

        var unsupported = ToolkitCommandLineParser.Parse(["set-mode", "--mode", "turbo", "--confirm"]);
        Assert.False(unsupported.Success);
        Assert.Equal(ToolkitCommandErrorCode.UnsupportedMode, unsupported.Error?.Code);

        var missing = ToolkitCommandLineParser.Parse(["set-mode", "--confirm"]);
        Assert.Equal(ToolkitCommandErrorCode.MissingArgument, missing.Error?.Code);
    }

    [Fact]
    public void RestoreAutomaticRequiresConfirmationAndRejectsExtraOptions()
    {
        var missingConfirm = ToolkitCommandLineParser.Parse(["restore-automatic"]);
        Assert.Equal(ToolkitCommandErrorCode.ConfirmationRequired, missingConfirm.Error?.Code);

        var accepted = ToolkitCommandLineParser.Parse(["restore-automatic", "--yes"]);
        Assert.True(accepted.Success);
        Assert.Equal(ToolkitCommandAction.RestoreAutomatic, accepted.Command!.Action);

        var extra = ToolkitCommandLineParser.Parse(["restore-automatic", "--confirm", "--mode", "balance"]);
        Assert.Equal(ToolkitCommandErrorCode.UnexpectedArgument, extra.Error?.Code);
    }

    [Fact]
    public void UnknownActionAndOptionsReturnStructuredErrorsWithoutEchoingInput()
    {
        var unknownAction = ToolkitCommandLineParser.Parse(["rm -rf /tmp/secret-token"]);
        Assert.False(unknownAction.Success);
        Assert.Equal(ToolkitCommandErrorCode.UnknownAction, unknownAction.Error?.Code);
        Assert.DoesNotContain("secret-token", unknownAction.Error!.Message);
        Assert.Contains("status", unknownAction.Error.Message);

        var unknownOption = ToolkitCommandLineParser.Parse(["status", "--profile=secret-token"]);
        Assert.Equal(ToolkitCommandErrorCode.UnknownOption, unknownOption.Error?.Code);
        Assert.DoesNotContain("secret-token", unknownOption.Error!.Message);

        var notAnOption = ToolkitCommandLineParser.Parse(["status", "secret-token"]);
        Assert.Equal(ToolkitCommandErrorCode.UnknownOption, notAnOption.Error?.Code);
        Assert.DoesNotContain("secret-token", notAnOption.Error!.Message);
    }

    [Fact]
    public void EmptyAndOversizedInputReturnStructuredErrors()
    {
        Assert.Equal(ToolkitCommandErrorCode.EmptyCommandLine, ToolkitCommandLineParser.Parse(null).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.EmptyCommandLine, ToolkitCommandLineParser.Parse([]).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.EmptyCommandLine, ToolkitCommandLineParser.Parse(["   "]).Error?.Code);

        var oversized = new List<string> { "status" };
        oversized.AddRange(Enumerable.Repeat("x", ToolkitCommandLineParser.MaxTokens));
        var result = ToolkitCommandLineParser.Parse(oversized);
        Assert.Equal(ToolkitCommandErrorCode.TooManyArguments, result.Error?.Code);
    }

    [Fact]
    public void DuplicateAndConflictingArgumentsAreRejected()
    {
        Assert.Equal(ToolkitCommandErrorCode.DuplicateArgument,
            ToolkitCommandLineParser.Parse(["apply-profile", "--name", "A", "--name", "B", "--confirm"]).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.DuplicateArgument,
            ToolkitCommandLineParser.Parse(["restore-automatic", "--confirm", "--confirm"]).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.UnexpectedArgument,
            ToolkitCommandLineParser.Parse(["apply-profile", "--name", "A", "--mode", "balance", "--confirm"]).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.UnexpectedArgument,
            ToolkitCommandLineParser.Parse(["status", "--confirm"]).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.UnexpectedArgument,
            ToolkitCommandLineParser.Parse(["set-mode", "--mode", "balance", "--name", "A", "--confirm"]).Error?.Code);
    }

    [Fact]
    public void InvalidProfileNamesAreRejectedAndSurroundingSpaceIsTrimmed()
    {
        Assert.Equal(ToolkitCommandErrorCode.MissingArgument,
            ToolkitCommandLineParser.Parse(["apply-profile", "--name=", "--confirm"]).Error?.Code);
        Assert.Equal(ToolkitCommandErrorCode.InvalidProfileName,
            ToolkitCommandLineParser.Parse(["apply-profile", $"--name={new string('x', 65)}", "--confirm"]).Error?.Code);
        var trimmed = ToolkitCommandLineParser.Parse(["apply-profile", "--name", "  Gaming  ", "--confirm"]);
        Assert.True(trimmed.Success);
        Assert.Equal("Gaming", trimmed.Command!.ProfileName);
    }

    [Fact]
    public void HostFlagsArePassedThroughAndIgnoredByTheCommandParser()
    {
        var result = ToolkitCommandLineParser.Parse(["--demo", "status", "--startup"]);
        Assert.True(result.Success);
        Assert.Equal(ToolkitCommandAction.Status, result.Command!.Action);
        Assert.Equal(new[] { "--demo", "--startup" }, result.HostFlags.ToArray());

        var failure = ToolkitCommandLineParser.Parse(["--demo", "bogus-action"]);
        Assert.Equal(ToolkitCommandErrorCode.UnknownAction, failure.Error?.Code);
        Assert.Equal(new[] { "--demo" }, failure.HostFlags.ToArray());
    }

    [Fact]
    public void QuietHostFlagIsOptionalAndNeverChangesTheParsedCommand()
    {
        var withoutQuiet = ToolkitCommandLineParser.Parse(["set-mode", "--mode", "balance", "--confirm"]);
        var withQuiet = ToolkitCommandLineParser.Parse(["--quiet", "set-mode", "--mode", "balance", "--confirm"]);
        Assert.True(withQuiet.Success);
        Assert.Equal(ToolkitCommandAction.SetMode, withQuiet.Command!.Action);
        Assert.Equal(withoutQuiet.Command!.Mode, withQuiet.Command.Mode);
        Assert.True(withQuiet.Command.Confirmed);
        Assert.Equal(new[] { "--quiet" }, withQuiet.HostFlags.ToArray());
        Assert.True(ToolkitCommandLineParser.HasHostFlag(withQuiet.HostFlags, "--QUIET"));
        Assert.False(ToolkitCommandLineParser.HasHostFlag(withoutQuiet.HostFlags, "--quiet"));
        Assert.False(ToolkitCommandLineParser.HasHostFlag(null, "--quiet"));

        var quietOnly = ToolkitCommandLineParser.Parse(["--quiet"]);
        Assert.False(quietOnly.Success);
        Assert.Equal(ToolkitCommandErrorCode.EmptyCommandLine, quietOnly.Error?.Code);
        Assert.Equal(new[] { "--quiet" }, quietOnly.HostFlags.ToArray());
    }

    [Fact]
    public void HelpIsWhitelistedAndNeverClaimsTheEntryPointIsWiredUp()
    {
        var help = ToolkitCommandLineParser.Parse(["--help"]);
        Assert.True(help.Success);
        Assert.Equal(ToolkitCommandAction.Help, help.Command!.Action);
        Assert.Contains("不会直接操作硬件", ToolkitCommandLineParser.Usage);
        Assert.Equal(new[] { "status", "apply-profile", "set-mode", "restore-automatic", "help" }, ToolkitCommandLineParser.SupportedActions.ToArray());
        Assert.True(ToolkitCommandLineParser.IsSupportedAction("SET-MODE"));
        Assert.False(ToolkitCommandLineParser.IsSupportedAction("shutdown"));
    }

    /// <summary>
    /// An explicit request id is the caller's handle for "this is the same write again". It is kept verbatim for the
    /// relay but never becomes part of a log-safe summary, a host flag or the usage text.
    /// </summary>
    [Fact]
    public void ExplicitRequestIdIsAcceptedAndStaysOutOfTheParseSummary()
    {
        var id = "0123456789ABCDEF0123456789abcdef";
        var result = ToolkitCommandLineParser.Parse(["--request-id=" + id, "set-mode", "--mode", "balance", "--confirm"]);

        Assert.True(result.Success);
        Assert.Equal(id, result.RequestId);
        Assert.Equal(ToolkitCommandAction.SetMode, result.Command!.Action);
        Assert.Equal(PowerMode.Balance, result.Command.Mode);
        Assert.Empty(result.HostFlags);
        Assert.DoesNotContain(id, result.Command.SafeSummary);
        Assert.Contains("--request-id", ToolkitCommandLineParser.Usage);
        Assert.DoesNotContain(id, ToolkitCommandLineParser.Usage);
    }

    [Fact]
    public void WithoutAnExplicitRequestIdTheParseResultCarriesNone()
    {
        Assert.Null(ToolkitCommandLineParser.Parse(["status"]).RequestId);
        Assert.Null(ToolkitCommandLineParser.Parse(["restore-automatic", "--confirm"]).RequestId);
    }

    [Theory]
    [InlineData("--request-id")]
    [InlineData("--request-id=")]
    [InlineData("--request-id=secret-token")]
    [InlineData("--request-id=0123456789abcdef")]
    [InlineData("--request-id=0123456789abcdef0123456789abcdef00")]
    public void MalformedRequestIdsAreRejectedWithoutEchoingTheValue(string raw)
    {
        var result = ToolkitCommandLineParser.Parse([raw, "set-mode", "--mode", "balance", "--confirm"]);

        Assert.False(result.Success);
        Assert.Equal(ToolkitCommandErrorCode.InvalidArgument, result.Error?.Code);
        Assert.Null(result.Command);
        Assert.Null(result.RequestId);
        Assert.DoesNotContain("secret-token", result.Error!.Message);
        Assert.DoesNotContain("0123456789abcdef", result.Error.Message);
    }

    [Fact]
    public void DuplicateRequestIdsAreRejected()
    {
        var first = new string('a', 32);
        var second = new string('b', 32);
        var result = ToolkitCommandLineParser.Parse([
            "--request-id=" + first, "--request-id=" + second, "restore-automatic", "--confirm"]);

        Assert.Equal(ToolkitCommandErrorCode.DuplicateArgument, result.Error?.Code);
        Assert.Null(result.Command);
    }
}
