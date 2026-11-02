using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Pure-object tests for the bounded hardware-tree walk that the optional CPU/GPU
/// LibreHardwareMonitor readers use for their sub-hardware traversal. No driver,
/// no LibreHardwareMonitor and no real hardware is touched here.
/// </summary>
public sealed class SensorTraversalBoundsTests
{
    private sealed class TestNode
    {
        public TestNode(string id) => Id = id;

        public string Id { get; }

        public List<TestNode> Children { get; } = new();

        public TestNode With(params TestNode[] children)
        {
            Children.AddRange(children);
            return this;
        }

        public override string ToString() => Id;
    }

    /// <summary>
    /// Value-equality lookalike: every instance is "equal", so a walk that used
    /// default equality would visit exactly one of them.
    /// </summary>
    private sealed class EqualLookingNode
    {
        public EqualLookingNode(string id) => Id = id;

        public string Id { get; }

        public List<EqualLookingNode> Children { get; } = new();

        public override bool Equals(object? obj) => obj is EqualLookingNode;

        public override int GetHashCode() => 0;
    }

    private static TreeWalkOutcome Walk(
        IEnumerable<TestNode>? roots, List<string> log, TreeWalkBounds? bounds = null) =>
        BoundedTreeWalk.Walk(roots, node => node.Children, (node, _) => log.Add(node.Id), bounds);

    [Fact]
    public void NormalTreeIsWalkedParentFirstInOrderWithoutTruncation()
    {
        var c = new TestNode("c");
        var d = new TestNode("d");
        var e = new TestNode("e");
        var a = new TestNode("a").With(c);
        var b = new TestNode("b").With(d, e);
        var root = new TestNode("root").With(a, b);
        var log = new List<string>();

        var outcome = Walk([root], log);

        Assert.Equal(["root", "a", "c", "b", "d", "e"], log);
        Assert.Equal(6, outcome.VisitedNodes);
        Assert.False(outcome.WasTruncated);
        Assert.False(outcome.NodeBudgetExhausted);
        Assert.False(outcome.DepthLimitReached);
    }

    [Fact]
    public void CycleTerminatesAndVisitsEachReferenceOnce()
    {
        var a = new TestNode("a");
        var b = new TestNode("b");
        a.With(b);
        b.With(a, a);
        var log = new List<string>();

        var outcome = Walk([a], log);

        Assert.Equal(["a", "b"], log);
        Assert.Equal(2, outcome.VisitedNodes);
        Assert.False(outcome.WasTruncated);
    }

    [Fact]
    public void SelfReferencingNodeTerminates()
    {
        var a = new TestNode("a");
        a.With(a);
        var log = new List<string>();

        var outcome = Walk([a], log);

        Assert.Equal(["a"], log);
        Assert.Equal(1, outcome.VisitedNodes);
        Assert.False(outcome.WasTruncated);
    }

    [Fact]
    public void DiamondSharedChildIsVisitedOnce()
    {
        var shared = new TestNode("shared");
        var left = new TestNode("left").With(shared);
        var right = new TestNode("right").With(shared);
        var root = new TestNode("root").With(left, right);
        var log = new List<string>();

        var outcome = Walk([root], log);

        Assert.Equal(["root", "left", "shared", "right"], log);
        Assert.Equal(4, outcome.VisitedNodes);
        Assert.False(outcome.WasTruncated);
    }

    [Fact]
    public void ReferenceIdentityIsUsedInsteadOfValueEquality()
    {
        var one = new EqualLookingNode("one");
        var two = new EqualLookingNode("two");
        var root = new EqualLookingNode("root");
        root.Children.Add(one);
        root.Children.Add(two);
        var log = new List<string>();

        var outcome = BoundedTreeWalk.Walk(
            [root], node => node.Children, (node, _) => log.Add(node.Id));

        Assert.Equal(["root", "one", "two"], log);
        Assert.Equal(3, outcome.VisitedNodes);
        Assert.False(outcome.WasTruncated);
    }

    [Fact]
    public void DeepChainStopsAtDepthEightAndReportsDepthLimit()
    {
        var deepest = new TestNode("n19");
        var node = deepest;
        for (var i = 18; i >= 0; i--)
            node = new TestNode($"n{i}").With(node);
        var log = new List<string>();

        var outcome = Walk([node], log);

        // Root is depth 0, so n8 (depth 8) is the last node inspected.
        Assert.Equal(9, outcome.VisitedNodes);
        Assert.Equal("n8", log[^1]);
        Assert.True(outcome.DepthLimitReached);
        Assert.False(outcome.NodeBudgetExhausted);
    }

    [Fact]
    public void ChainThatEndsAtDepthEightIsNotReportedAsTruncated()
    {
        var node = new TestNode("n8");
        for (var i = 7; i >= 0; i--)
            node = new TestNode($"n{i}").With(node);
        var log = new List<string>();

        var outcome = Walk([node], log);

        Assert.Equal(9, outcome.VisitedNodes);
        Assert.False(outcome.WasTruncated);
    }

    [Fact]
    public void WideGraphStopsAtTheNodeBudget()
    {
        var root = new TestNode("root");
        for (var i = 0; i < 1000; i++)
            root.Children.Add(new TestNode($"child{i}"));
        var log = new List<string>();

        var outcome = Walk([root], log);

        Assert.Equal(256, outcome.VisitedNodes);
        Assert.Equal(256, log.Count);
        Assert.Equal(log.Count, log.Distinct().Count());
        Assert.True(outcome.NodeBudgetExhausted);
        Assert.False(outcome.DepthLimitReached);
        Assert.Equal("root", log[0]);
    }

    [Fact]
    public void NodeBudgetIsSharedByAllRootsAndNeverResetPerSubtree()
    {
        var roots = new List<TestNode>();
        for (var r = 0; r < 3; r++)
        {
            var root = new TestNode($"root{r}");
            for (var i = 0; i < 200; i++)
                root.Children.Add(new TestNode($"r{r}c{i}"));
            roots.Add(root);
        }
        var log = new List<string>();

        var outcome = Walk(roots, log);

        // 3 x 200 children: a per-subtree budget would allow 600+ visits.
        Assert.Equal(256, outcome.VisitedNodes);
        Assert.Equal(256, log.Distinct().Count());
        Assert.True(outcome.NodeBudgetExhausted);
    }

    [Fact]
    public void CustomBoundsAreHonoured()
    {
        var root = new TestNode("root");
        for (var i = 0; i < 50; i++)
            root.Children.Add(new TestNode($"child{i}"));
        var log = new List<string>();

        var outcome = Walk([root], log, new TreeWalkBounds(4, 2));

        Assert.Equal(4, outcome.VisitedNodes);
        Assert.True(outcome.NodeBudgetExhausted);
    }

    [Fact]
    public void NullRootsAndNullChildrenAreIgnored()
    {
        var log = new List<string>();

        var empty = Walk(null, log);
        Assert.Equal(0, empty.VisitedNodes);
        Assert.False(empty.WasTruncated);

        var noChildren = BoundedTreeWalk.Walk(
            new TestNode[] { null!, new TestNode("a") }, node => node.Children, (node, _) => log.Add(node.Id));
        Assert.Equal(["a"], log);
        Assert.Equal(1, noChildren.VisitedNodes);
        Assert.False(noChildren.WasTruncated);
    }

    [Fact]
    public void EndlessChildSequenceCannotHangTheWalk()
    {
        var root = new TestNode("root");
        var log = new List<string>();
        var produced = 0;

        IEnumerable<TestNode> Endless()
        {
            for (var i = 0; ; i++)
            {
                produced++;
                yield return new TestNode($"child{i}");
            }
        }

        var outcome = BoundedTreeWalk.Walk(
            [root], _ => Endless(), (node, _) => log.Add(node.Id));

        Assert.Equal(256, outcome.VisitedNodes);
        Assert.Equal(TreeWalkBounds.Default.MaximumNodes, log.Count);
        Assert.True(outcome.NodeBudgetExhausted);
        Assert.False(outcome.DepthLimitReached);
        // The shared budget is spent by visited + queued nodes, so the child sequence
        // is pulled only up to the first child that has to be rejected (+1 probe) and
        // the endless tail is never read, not even one item per visited node.
        Assert.True(
            produced <= TreeWalkBounds.Default.MaximumNodes + 1,
            $"produced {produced}");
        // Yielded children plus dequeued nodes together stay within one bounded pass.
        Assert.True(
            produced + log.Count <= 2 * TreeWalkBounds.Default.MaximumNodes + 1,
            $"produced {produced} + dequeued {log.Count}");
    }

    [Fact]
    public void EndlessNullOnlyChildSequenceCannotHangTheWalk()
    {
        var root = new TestNode("root");
        var log = new List<string>();
        var produced = 0;

        IEnumerable<TestNode> EndlessNulls()
        {
            for (var i = 0; ; i++)
            {
                produced++;
                yield return null!;
            }
        }

        var outcome = BoundedTreeWalk.Walk(
            [root], _ => EndlessNulls(), (node, _) => log.Add(node.Id));

        // Nulls are skipped, but skipping them still may not drain the sequence.
        Assert.Equal(["root"], log);
        Assert.Equal(1, outcome.VisitedNodes);
        Assert.True(outcome.NodeBudgetExhausted);
        Assert.True(
            produced <= TreeWalkBounds.Default.MaximumNodes + 1,
            $"produced {produced}");
    }

    [Fact]
    public void EmptyBoundsArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BoundedTreeWalk.Walk<TestNode>(null, null!, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() =>
            BoundedTreeWalk.Walk<TestNode>(null, node => node.Children, null!));
    }

    [Fact]
    public void DefaultBoundsMatchTheReaderLimits()
    {
        Assert.Equal(256, TreeWalkBounds.Default.MaximumNodes);
        Assert.Equal(8, TreeWalkBounds.Default.MaximumDepth);
        Assert.Equal(TreeWalkBounds.Default, new TreeWalkBounds(256, 8));
    }
}
