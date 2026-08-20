namespace Pia.Services.Interfaces;

public sealed class PolicyChangedEventArgs : EventArgs
{
    /// <summary>Only keys the new document still sets, so an unpin or a withdrawal contributes none.</summary>
    public required IReadOnlySet<string> ValuesChanged { get; init; }

    /// <summary>Symmetric difference of the enforced key sets.</summary>
    public required IReadOnlySet<string> EnforcementChanged { get; init; }
}
