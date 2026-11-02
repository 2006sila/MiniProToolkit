namespace MiniProToolkit.Core;

/// <summary>Transient WMI projection only; never persisted and contains no native objects.</summary>
public sealed record BrightnessPanelCandidate(
    string? InstanceName = null,
    object? Active = null,
    object? OutputTechnology = null,
    object? CurrentBrightness = null,
    string? MethodInstanceName = null);

/// <summary>
/// Fail-closed, staged target selection. No I/O or write delegates are accepted.
/// Counts include malformed and duplicate rows; selection never picks the first match.
/// </summary>
public static class BrightnessPanelSelection
{
    public static bool SameInstance(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static int? CurrentPercent(object? value) => value switch
    {
        byte number when number <= 100 => number,
        ushort number when number <= 100 => number,
        int number when number is >= 0 and <= 100 => number,
        uint number when number <= 100 => (int)number,
        _ => null
    };

    public static BrightnessPanelCandidate? SelectBrightness(
        IReadOnlyList<BrightnessPanelCandidate> candidates)
    {
        if (candidates.Count != 1)
            return null;
        var candidate = candidates[0];
        return !string.IsNullOrWhiteSpace(candidate.InstanceName) &&
            CurrentPercent(candidate.CurrentBrightness) is not null ? candidate : null;
    }

    public static bool IsBuiltInPanel(string? targetInstanceName,
        IReadOnlyList<BrightnessPanelCandidate> connections)
    {
        var matches = 0;
        var activeCount = 0;
        var builtIn = false;
        foreach (var connection in connections)
        {
            // Unknown identity/state/type cannot establish an unambiguous active target.
            if (string.IsNullOrWhiteSpace(connection.InstanceName) ||
                connection.Active is not bool active || Technology(connection.OutputTechnology) is not { } technology)
                return false;
            if (active)
                activeCount++;
            if (!SameInstance(connection.InstanceName, targetInstanceName))
                continue;
            matches++;
            builtIn = active && technology is 6u or 11u or 13u or 0x80000000u;
        }
        return matches == 1 && activeCount == 1 && builtIn;
    }

    public static int CountMethodInstances(string? targetInstanceName,
        IReadOnlyList<BrightnessPanelCandidate> methods)
    {
        if (methods.Count != 1)
            return methods.Count;
        return SameInstance(methods[0].MethodInstanceName, targetInstanceName) ? 1 : 0;
    }

    private static uint? Technology(object? value) => value switch
    {
        uint number => number,
        int number => unchecked((uint)number),
        _ => null
    };
}
