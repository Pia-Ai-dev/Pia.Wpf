namespace Pia.Models;

public readonly record struct TourTargetBounds(double X, double Y, double Width, double Height);

/// <summary>One element a guided tour could point at, described in the UIA vocabulary.</summary>
public sealed record TourTarget(
    string AutomationId,
    string? Name,
    string ControlType,
    TourTargetBounds Bounds,
    string OwningView)
{
    // The generated record ToString prints the id and the name, both of which can carry user text.
    public override string ToString() => $"{ControlType} in {OwningView}";
}

/// <summary><paramref name="RootView"/> is the root visual's type name — a window Title is user content.</summary>
public sealed record TourTargetScan(string RootView, bool Truncated, IReadOnlyList<TourTarget> Targets)
{
    public static readonly TourTargetScan Empty = new(string.Empty, false, []);

    // Same reason as TourTarget.ToString: formatting the scan would print every id.
    public override string ToString() => $"{Targets.Count} targets in {RootView}";
}
