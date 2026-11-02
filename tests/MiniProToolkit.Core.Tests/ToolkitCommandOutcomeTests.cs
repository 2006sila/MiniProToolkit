using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Startup-level behaviour: which disposition/exit code the host gets for a parsed command line, and how --quiet
/// changes the UI side. Nothing here executes a command, touches hardware or opens a Guardian lease.
/// </summary>
public sealed class ToolkitCommandOutcomeTests
{
    [Fact]
    public void PlainLaunchOpensTheMainWindowWithSuccess()
    {
        foreach (var arguments in new[] { null, Array.Empty<string>(), new[] { "--demo" }, new[] { "--startup" }, new[] { "--demo", "--startup" } })
        {
            var outcome = ToolkitCommandOutcome.From(arguments);
            Assert.Equal(ToolkitCommandHostDisposition.OpenMainWindow, outcome.Disposition);
            Assert.Equal(ToolkitCommandExitCodes.Success, outcome.ExitCode);
            Assert.Equal(0, outcome.ExitCode);
            Assert.True(outcome.OpenMainWindow);
            Assert.False(outcome.IsError);
            Assert.False(outcome.ShowDialog);
            Assert.Equal(ToolkitCommandAction.None, outcome.Action);
        }

        var demoStartup = ToolkitCommandOutcome.From(["--demo", "--startup"]);
        Assert.True(demoStartup.Demo);
        Assert.True(demoStartup.Startup);
    }

    [Fact]
    public void HelpStaysSuccessfulAndPrintsUsage()
    {
        var outcome = ToolkitCommandOutcome.From(["--help"]);
        Assert.Equal(ToolkitCommandHostDisposition.ShowUsage, outcome.Disposition);
        Assert.Equal(ToolkitCommandExitCodes.Success, outcome.ExitCode);
        Assert.False(outcome.IsError);
        Assert.True(outcome.ShowDialog);
        Assert.Equal(ToolkitCommandAction.Help, outcome.Action);
        Assert.Contains("不会直接操作硬件", outcome.Details);
        Assert.Contains("exit=0", outcome.SafeLogLine);
        Assert.Contains("--quiet", outcome.Details);
    }

    [Fact]
    public void RecognisedCommandsAreRelayedToTheRunningInstance()
    {
        var cases = new (string[] Args, ToolkitCommandAction Action)[]
        {
            (["status"], ToolkitCommandAction.Status),
            (["apply-profile", "--name", "Gaming", "--confirm"], ToolkitCommandAction.ApplyProfile),
            (["set-mode", "--mode", "performance", "--confirm"], ToolkitCommandAction.SetMode),
            (["restore-automatic", "--confirm"], ToolkitCommandAction.RestoreAutomatic)
        };

        foreach (var (args, action) in cases)
        {
            var outcome = ToolkitCommandOutcome.From(args);
            Assert.Equal(ToolkitCommandHostDisposition.RelayToRunningInstance, outcome.Disposition);
            // The exit code is decided by the running instance, not by the parser; nothing may look successful here.
            Assert.Equal(ToolkitCommandExitCodes.RelayPending, outcome.ExitCode);
            Assert.True(outcome.RelayCommand);
            Assert.Equal(action, outcome.Action);
            Assert.False(outcome.OpenMainWindow);
            Assert.False(outcome.IsError);
            // A command never blocks a script behind a MessageBox.
            Assert.False(outcome.ShowDialog);
            Assert.Null(outcome.Details);
            Assert.Contains($"action={action}", outcome.SafeLogLine);
            Assert.DoesNotContain("Gaming", outcome.SafeLogLine);
        }
    }

    [Fact]
    public void ParseErrorsKeepExitCodeTwo()
    {
        foreach (var args in new[]
                 {
                     new[] { "bogus-action" },
                     new[] { "set-mode", "--mode", "turbo", "--confirm" },
                     new[] { "apply-profile", "--name", "Gaming" },
                     new[] { "status", "--confirm" }
                 })
        {
            var outcome = ToolkitCommandOutcome.From(args);
            Assert.Equal(ToolkitCommandHostDisposition.RejectedCommandLine, outcome.Disposition);
            Assert.Equal(ToolkitCommandExitCodes.ParseError, outcome.ExitCode);
            Assert.Equal(2, outcome.ExitCode);
            Assert.True(outcome.IsError);
            Assert.True(outcome.ShowDialog);
            Assert.Equal(ToolkitCommandAction.None, outcome.Action);
            Assert.Contains("exit=2", outcome.SafeLogLine);
        }
    }

    [Fact]
    public void QuietSuppressesDialogsAndKeepsTheExitCode()
    {
        var relay = ToolkitCommandOutcome.From(["--quiet", "status"]);
        Assert.True(relay.Quiet);
        Assert.False(relay.ShowDialog);
        Assert.Equal(ToolkitCommandExitCodes.RelayPending, relay.ExitCode);

        var rejected = ToolkitCommandOutcome.From(["--quiet", "--demo", "bogus-action"]);
        Assert.True(rejected.Quiet);
        Assert.True(rejected.Demo);
        Assert.False(rejected.ShowDialog);
        Assert.Equal(ToolkitCommandExitCodes.ParseError, rejected.ExitCode);

        var help = ToolkitCommandOutcome.From(["--QUIET", "--help"]);
        Assert.True(help.Quiet);
        Assert.False(help.ShowDialog);
        Assert.Equal(ToolkitCommandExitCodes.Success, help.ExitCode);

        // --quiet on its own is just a host flag: the main window still opens.
        var plain = ToolkitCommandOutcome.From(["--quiet"]);
        Assert.True(plain.Quiet);
        Assert.True(plain.OpenMainWindow);
        Assert.Equal(ToolkitCommandExitCodes.Success, plain.ExitCode);
    }

    [Fact]
    public void OutcomeTextsNeverEchoRawArgumentsOrValues()
    {
        const string secretProfile = @"C:\synthetic\Alice\secret-regex-.*";
        var outcome = ToolkitCommandOutcome.From(["--quiet", "apply-profile", "--name", secretProfile, "--confirm"]);
        Assert.Equal(ToolkitCommandHostDisposition.RelayToRunningInstance, outcome.Disposition);

        foreach (var text in new[] { outcome.SafeLogLine, outcome.Details ?? string.Empty })
        {
            Assert.DoesNotContain("Alice", text);
            Assert.DoesNotContain("secret-regex", text);
            Assert.DoesNotContain("C:\\", text);
            Assert.DoesNotContain("apply-profile --name", text);
            Assert.DoesNotContain("--confirm", text);
        }

        var rejected = ToolkitCommandOutcome.From(["--quiet", "status", "--evil=C:\\synthetic\\Alice\\path"]);
        Assert.Equal(ToolkitCommandExitCodes.ParseError, rejected.ExitCode);
        Assert.DoesNotContain("Alice", rejected.SafeLogLine);
        Assert.DoesNotContain("Alice", rejected.Details!);
        Assert.DoesNotContain("--evil", rejected.SafeLogLine);
        Assert.DoesNotContain("--evil", rejected.Details!);
    }
}
