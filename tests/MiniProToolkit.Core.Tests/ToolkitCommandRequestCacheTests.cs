using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pure logic of the write-request deduplication cache plus the protocol answers built from it. Nothing here starts
/// the app, opens a pipe, touches the EC or opens a Guardian lease: the cache is only asked what it would do with a
/// request id, and the answers are validated as protocol frames.
/// </summary>
public sealed class ToolkitCommandRequestCacheTests
{
    private static string Id() => ToolkitCommandIpcProtocol.NewRequestId();

    private static ToolkitCommandIpcResponse OkAnswer(string id, ToolkitCommandAction action) =>
        ToolkitCommandIpcProtocol.Ok(id, action, "已执行。");

    [Fact]
    public void FirstRequestIsDispatchedAndTheDuplicateIsAnsweredFromTheCache()
    {
        var cache = new ToolkitCommandRequestCache();
        var id = Id();

        var first = cache.Begin(id, ToolkitCommandAction.SetMode);
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Dispatch, first.Kind);
        Assert.True(first.ShouldExecute);
        Assert.Null(first.CachedAnswer);

        var answer = OkAnswer(id, ToolkitCommandAction.SetMode);
        cache.Complete(id, ToolkitCommandAction.SetMode, answer);

        var duplicate = cache.Begin(id, ToolkitCommandAction.SetMode);
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay, duplicate.Kind);
        Assert.False(duplicate.ShouldExecute);
        Assert.Same(answer, duplicate.CachedAnswer);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void DifferentRequestIdsAndDifferentActionsStayIndependent()
    {
        var cache = new ToolkitCommandRequestCache();
        var first = Id();
        var second = Id();

        Assert.True(cache.Begin(first, ToolkitCommandAction.SetMode).ShouldExecute);
        Assert.True(cache.Begin(second, ToolkitCommandAction.SetMode).ShouldExecute);

        cache.Complete(first, ToolkitCommandAction.SetMode, OkAnswer(first, ToolkitCommandAction.SetMode));

        // Same id, other action: an independent exchange, so it may run once as well.
        Assert.True(cache.Begin(first, ToolkitCommandAction.RestoreAutomatic).ShouldExecute);
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay,
            cache.Begin(first, ToolkitCommandAction.SetMode).Kind);
        Assert.Equal(ToolkitCommandDispatchDecisionKind.AlreadyInFlight,
            cache.Begin(second, ToolkitCommandAction.SetMode).Kind);
    }

    [Fact]
    public void DuplicateWhileTheFirstAttemptIsStillRunningIsNeverExecutedAgain()
    {
        var cache = new ToolkitCommandRequestCache();
        var id = Id();

        Assert.True(cache.Begin(id, ToolkitCommandAction.ApplyProfile).ShouldExecute);

        var duplicate = cache.Begin(id, ToolkitCommandAction.ApplyProfile);
        Assert.Equal(ToolkitCommandDispatchDecisionKind.AlreadyInFlight, duplicate.Kind);
        Assert.False(duplicate.ShouldExecute);
        Assert.Null(duplicate.CachedAnswer);

        // An attempt that timed out without an answer keeps the id blocked: retrying it must not re-run the write.
        Assert.Equal(ToolkitCommandDispatchDecisionKind.AlreadyInFlight,
            cache.Begin(id, ToolkitCommandAction.ApplyProfile).Kind);

        // The late answer still ends up in the cache, so later duplicates get it instead of a second execution.
        var answer = OkAnswer(id, ToolkitCommandAction.ApplyProfile);
        cache.Complete(id, ToolkitCommandAction.ApplyProfile, answer);
        Assert.Same(answer, cache.Begin(id, ToolkitCommandAction.ApplyProfile).CachedAnswer);
    }

    [Fact]
    public void FailedAnswersAreRememberedSoARetryDoesNotRunTheWriteAgain()
    {
        var cache = new ToolkitCommandRequestCache();
        var id = Id();

        Assert.True(cache.Begin(id, ToolkitCommandAction.RestoreAutomatic).ShouldExecute);
        var failed = ToolkitCommandIpcProtocol.Failed(id, ToolkitCommandAction.RestoreAutomatic, "未执行。");
        cache.Complete(id, ToolkitCommandAction.RestoreAutomatic, failed);

        var replay = ToolkitCommandIpcProtocol.Replayed(id, cache.Begin(id, ToolkitCommandAction.RestoreAutomatic).CachedAnswer!);
        Assert.Equal(ToolkitCommandIpcStatus.Failed, replay.Status);
        Assert.False(replay.Executed);
    }

    [Fact]
    public void CacheIsBoundedAndDropsTheLeastRecentlyUsedId()
    {
        var now = 0L;
        var cache = new ToolkitCommandRequestCache(2, TimeSpan.FromSeconds(60), () => now);
        var ids = new[] { Id(), Id(), Id() };

        Assert.True(cache.Begin(ids[0], ToolkitCommandAction.SetMode).ShouldExecute);
        cache.Complete(ids[0], ToolkitCommandAction.SetMode, OkAnswer(ids[0], ToolkitCommandAction.SetMode));
        Assert.True(cache.Begin(ids[1], ToolkitCommandAction.SetMode).ShouldExecute);

        // Using ids[0] again makes it the most recently used entry, so ids[1] is the one that has to go.
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay, cache.Begin(ids[0], ToolkitCommandAction.SetMode).Kind);
        Assert.True(cache.Begin(ids[2], ToolkitCommandAction.SetMode).ShouldExecute);
        Assert.Equal(2, cache.Count);

        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay, cache.Begin(ids[0], ToolkitCommandAction.SetMode).Kind);
        // The evicted id is no longer remembered; that is the documented bound (a fresh id may execute again).
        Assert.True(cache.Begin(ids[1], ToolkitCommandAction.SetMode).ShouldExecute);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void RetentionWindowExpiresSoALaterRetryIsDispatchableAgain()
    {
        var now = 0L;
        var cache = new ToolkitCommandRequestCache(retention: TimeSpan.FromSeconds(60), monotonicMilliseconds: () => now);
        var id = Id();

        Assert.True(cache.Begin(id, ToolkitCommandAction.SetMode).ShouldExecute);
        cache.Complete(id, ToolkitCommandAction.SetMode, OkAnswer(id, ToolkitCommandAction.SetMode));

        now = 59_999;
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay, cache.Begin(id, ToolkitCommandAction.SetMode).Kind);

        // The window slides with every replay, and only expires once it really elapsed.
        now += 59_999;
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay, cache.Begin(id, ToolkitCommandAction.SetMode).Kind);
        now += 60_000;
        Assert.True(cache.Begin(id, ToolkitCommandAction.SetMode).ShouldExecute);
    }

    [Fact]
    public void CompleteWithoutBeginStillMakesTheIdReplayable()
    {
        var cache = new ToolkitCommandRequestCache();
        var id = Id();
        cache.Complete(id, ToolkitCommandAction.ApplyProfile, OkAnswer(id, ToolkitCommandAction.ApplyProfile));
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay,
            cache.Begin(id, ToolkitCommandAction.ApplyProfile).Kind);
    }

    [Fact]
    public void ReplayMatchesTheRequestIdCaseInsensitively()
    {
        var cache = new ToolkitCommandRequestCache();
        var id = "0123456789ABCDEF0123456789ABCDEF";
        Assert.True(cache.Begin(id, ToolkitCommandAction.SetMode).ShouldExecute);
        cache.Complete(id, ToolkitCommandAction.SetMode, OkAnswer(id, ToolkitCommandAction.SetMode));
        Assert.Equal(ToolkitCommandDispatchDecisionKind.Replay,
            cache.Begin(id.ToLowerInvariant(), ToolkitCommandAction.SetMode).Kind);
    }

    [Fact]
    public void DefaultsAreBoundedAndOutliveTheServerWorstCase()
    {
        // A retry has to be recognized for longer than the server may spend on the first attempt.
        Assert.Equal(32, ToolkitCommandRequestCache.DefaultCapacity);
        Assert.Equal(TimeSpan.FromSeconds(60), ToolkitCommandRequestCache.DefaultRetention);
        Assert.True(ToolkitCommandRequestCache.DefaultRetention.TotalMilliseconds >
                    ToolkitCommandIpcProtocol.ServerWorstCaseMilliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolkitCommandRequestCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolkitCommandRequestCache(4, TimeSpan.Zero));
        Assert.ThrowsAny<ArgumentException>(() =>
            new ToolkitCommandRequestCache().Begin(" ", ToolkitCommandAction.SetMode));
        Assert.Throws<ArgumentNullException>(() =>
            new ToolkitCommandRequestCache().Complete(Id(), ToolkitCommandAction.SetMode, null!));
    }

    [Fact]
    public void OnlyWriteActionsRequireDeduplication()
    {
        Assert.True(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.ApplyProfile));
        Assert.True(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.SetMode));
        Assert.True(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.RestoreAutomatic));

        // status must answer with a fresh snapshot every time and activate only raises the window.
        Assert.False(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.Status));
        Assert.False(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.Activate));
        Assert.False(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.Help));
        Assert.False(ToolkitCommandIpcProtocol.RequiresDispatchDeduplication(ToolkitCommandAction.None));
    }

    [Fact]
    public void ReplayedAnswerIsNotExecutedKeepsItsStatusAndSurvivesTheWire()
    {
        var id = Id();
        var first = ToolkitCommandIpcProtocol.Ok(id, ToolkitCommandAction.SetMode, "已切换到均衡模式。");
        var replay = ToolkitCommandIpcProtocol.Replayed(id, first);

        Assert.Equal(ToolkitCommandIpcStatus.Ok, replay.Status);
        Assert.False(replay.Executed);
        Assert.Equal(id, replay.RequestId);
        Assert.Equal("set-mode", replay.Action);
        Assert.Equal(ToolkitCommandIpcProtocol.Version, replay.Protocol);
        Assert.Contains("未再次执行", replay.Message);
        // The request id itself is never part of an answer text.
        Assert.DoesNotContain(id, replay.Message);
        Assert.True(replay.Message.Length <= ToolkitCommandIpcProtocol.MaxMessageChars);

        var parsed = ToolkitCommandIpcProtocol.TryParseResponse(ToolkitCommandIpcProtocol.SerializeResponse(replay), id);
        Assert.NotNull(parsed);
        Assert.False(parsed!.Executed);
        Assert.Equal(ToolkitCommandIpcStatus.Ok, parsed.Status);
    }

    [Fact]
    public void DuplicateInFlightAnswerIsATimeoutThatAsksForStatus()
    {
        var id = Id();
        var answer = ToolkitCommandIpcProtocol.DuplicateInFlight(id, ToolkitCommandAction.RestoreAutomatic);

        Assert.Equal(ToolkitCommandIpcStatus.TimedOut, answer.Status);
        Assert.NotEqual(ToolkitCommandIpcStatus.Unavailable, answer.Status);
        Assert.False(answer.Executed);
        Assert.Equal(id, answer.RequestId);
        Assert.Equal("restore-automatic", answer.Action);
        Assert.Contains("status", answer.Message);
        Assert.Contains("未再次执行", answer.Message);

        // Exit code 6 ("may already have executed, re-check with status"), never 4 ("nothing was sent") and never 0.
        Assert.Equal(6, ToolkitCommandExitCodes.CommandTimedOut);
        Assert.NotEqual(ToolkitCommandExitCodes.NoRunningInstance, ToolkitCommandExitCodes.CommandTimedOut);
        Assert.Null(ToolkitCommandIpcProtocol.TryParseResponse(
            ToolkitCommandIpcProtocol.SerializeResponse(answer), ToolkitCommandIpcProtocol.NewRequestId()));
    }
}
