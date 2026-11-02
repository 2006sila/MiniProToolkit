namespace MiniProToolkit.Core;

/// <summary>
/// Hard limits for one whole tree pass. The defaults are the same bounds the
/// optional LibreHardwareMonitor readers use (256 visited nodes, depth 8): a
/// misbehaving driver or a corrupted hardware graph can never turn one sample
/// into an unbounded walk or a <see cref="System.StackOverflowException"/>.
/// </summary>
public readonly record struct TreeWalkBounds
{
    /// <summary>Maximum number of distinct nodes visited by one top pass.</summary>
    public const int DefaultMaximumNodes = 256;

    /// <summary>Deepest node depth (root = 0) that is still inspected.</summary>
    public const int DefaultMaximumDepth = 8;

    public static TreeWalkBounds Default { get; } = new(DefaultMaximumNodes, DefaultMaximumDepth);

    public TreeWalkBounds(int maximumNodes, int maximumDepth)
    {
        MaximumNodes = Math.Max(1, maximumNodes);
        MaximumDepth = Math.Max(0, maximumDepth);
    }

    public int MaximumNodes { get; }

    public int MaximumDepth { get; }
}

/// <summary>
/// What one bounded pass actually did. Callers use it only for a short status
/// hint: a truncated walk still returns every value it found, so a partial read
/// is never turned into an invented (for example zero) measurement.
/// </summary>
public readonly record struct TreeWalkOutcome(
    int VisitedNodes, bool NodeBudgetExhausted, bool DepthLimitReached)
{
    public bool WasTruncated => NodeBudgetExhausted || DepthLimitReached;

    public static TreeWalkOutcome Complete(int visitedNodes) => new(visitedNodes, false, false);
}

/// <summary>
/// Iterative, bounded depth-first walk over a node graph. Kept in Core so the
/// bounds can be unit-tested with plain objects, without loading any driver or
/// touching real hardware.
/// <para>
/// Guarantees: reference identity is the only identity (so equal-looking but
/// distinct nodes are all visited while cycles and diamond paths are skipped
/// exactly once); the node budget belongs to the whole call, never to a subtree,
/// so a wide graph cannot spend the budget once per root; visited nodes, queued
/// nodes and nodes about to be queued all draw on that one budget, so the pending
/// work list stays within the same bound and a hostile (endless) child selector is
/// only read until the first rejected child instead of once per visited node; the
/// depth limit stops descending instead of recursing; a node at the depth limit is
/// still reported as visited, so sensor values on the boundary are not lost.
/// </para>
/// </summary>
public static class BoundedTreeWalk
{
    /// <summary>
    /// Walks roots in order (depth-first, parent before children, children in
    /// enumeration order) and calls <paramref name="visit"/> once per distinct
    /// node. Never throws for a cycle or an oversized graph; it stops at the
    /// bounds and reports that in the returned outcome. Passing
    /// <paramref name="bounds"/> as null uses <see cref="TreeWalkBounds.Default"/>.
    /// </summary>
    public static TreeWalkOutcome Walk<TNode>(
        IEnumerable<TNode>? roots,
        Func<TNode, IEnumerable<TNode>> children,
        Action<TNode, int> visit,
        TreeWalkBounds? bounds = null)
        where TNode : class
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(visit);

        var limits = bounds ?? TreeWalkBounds.Default;
        var visited = new HashSet<TNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(TNode Node, int Depth)>();
        var buffer = new List<TNode>();
        var depthLimited = false;
        var budgetExhausted = false;

        // One shared budget covers visited + queued + about-to-be-queued nodes, so
        // the pending stack can never exceed the node budget. The check runs before
        // an item is accepted, so the source sequence is pulled at most until the
        // first child that has to be rejected - which is what proves the cap - and a
        // hostile, endless selector is never drained one step per visited node.
        // Dropping children is a real truncation and is reported.
        void PushChildren(IEnumerable<TNode> items, int depth)
        {
            buffer.Clear();
            var pulled = 0;
            foreach (var item in items)
            {
                pulled++;
                if (visited.Count + pending.Count + buffer.Count >= limits.MaximumNodes)
                {
                    budgetExhausted = true;
                    break;
                }

                if (item is null)
                {
                    // A selector that only ever yields nulls is as hostile as an
                    // endless one: it still cannot be read past the same budget.
                    if (pulled >= limits.MaximumNodes)
                    {
                        budgetExhausted = true;
                        break;
                    }
                    continue;
                }

                buffer.Add(item);
            }

            // Reversed push keeps the enumeration order of the source graph.
            for (var i = buffer.Count - 1; i >= 0; i--)
                pending.Push((buffer[i], depth));
        }

        if (roots is not null)
            PushChildren(roots, 0);

        while (pending.Count > 0 && visited.Count < limits.MaximumNodes)
        {
            var (node, depth) = pending.Pop();
            // Reference identity: a cycle or a second path to the same object
            // costs one hash lookup and is skipped, so a cyclic graph terminates.
            if (!visited.Add(node))
                continue;

            visit(node, depth);

            // No budget is left for any child of this node: stop before touching the
            // selector, otherwise an endless sequence would be read one item per
            // visited node and the whole walk would stay pinned at the budget.
            if (visited.Count + pending.Count >= limits.MaximumNodes)
            {
                budgetExhausted = true;
                continue;
            }

            if (depth >= limits.MaximumDepth)
            {
                // Not descending. Only report the depth bound when something was
                // actually cut, so a normal shallow tree is never flagged. The probe
                // is bounded as well, so a null-only endless sequence terminates.
                var pulled = 0;
                foreach (var child in children(node))
                {
                    pulled++;
                    if (child is not null)
                    {
                        depthLimited = true;
                        break;
                    }
                    if (pulled >= limits.MaximumNodes)
                    {
                        budgetExhausted = true;
                        break;
                    }
                }
                continue;
            }

            PushChildren(children(node), depth + 1);
        }

        if (pending.Count > 0)
            budgetExhausted = true;

        return new TreeWalkOutcome(visited.Count, budgetExhausted, depthLimited);
    }
}
