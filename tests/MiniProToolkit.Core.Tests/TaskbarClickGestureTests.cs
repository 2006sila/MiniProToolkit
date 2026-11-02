using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// The taskbar overlay's click rule is a pure predicate, so double-click compatibility is pinned
/// here without a desktop session: a double click must behave exactly like a single click (one
/// open request), not like a second gesture and not like two opens.
/// </summary>
public sealed class TaskbarClickGestureTests
{
    [Fact]
    public void SingleClickRequestsOpen() =>
        Assert.True(TaskbarClickGesture.RequestsOpen(1));

    // A double click arrives as ClickCount 1 then 2: the first release opens, the repeat does not,
    // so the sequence raises the open request exactly once.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ExtraClickOfAMultiClickSequenceDoesNotRequestOpen(int clickCount) =>
        Assert.False(TaskbarClickGesture.RequestsOpen(clickCount));

    [Fact]
    public void DoubleClickOpensExactlyOnce() =>
        Assert.Single(Sequence(1, 2));

    [Fact]
    public void TripleClickStillOpensExactlyOnce() =>
        Assert.Single(Sequence(1, 2, 3));

    [Fact]
    public void TwoSeparateSingleClicksStillOpenTwice()
    {
        // Outside the double-click interval each click restarts at ClickCount 1, so the overlay
        // still opens on every deliberate single click (no throttling was added).
        Assert.Equal(2, Sequence(1, 1).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveClickCountNeverSwallowsAClick(int clickCount) =>
        Assert.True(TaskbarClickGesture.RequestsOpen(clickCount));

    private static List<int> Sequence(params int[] clickCounts) =>
        clickCounts.Where(TaskbarClickGesture.RequestsOpen).ToList();
}
