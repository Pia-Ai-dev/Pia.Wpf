namespace Pia.Services.Interfaces;

public enum MxcRuntimeState
{
    /// <summary>The host can sandbox, but the native runtime has not been downloaded and verified yet.</summary>
    NotInstalled,
    Downloading,
    /// <summary>This host cannot create sandboxes at all; the reason says why.</summary>
    Unsupported,
    Ready
}

/// <param name="Reason">Why the state is not <see cref="MxcRuntimeState.Ready"/>, in words a user can act on.</param>
public sealed record MxcAvailabilityStatus(MxcRuntimeState State, string? Reason = null)
{
    public bool IsReady => State == MxcRuntimeState.Ready;
}

/// <summary>Whether this host can run a local MCP server inside an MXC sandbox right now.</summary>
public interface IMxcAvailability
{
    /// <summary>The cached answer; never blocks.</summary>
    MxcAvailabilityStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes, possibly off the UI thread.</summary>
    event EventHandler<MxcAvailabilityStatus>? StatusChanged;

    Task<MxcAvailabilityStatus> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads and verifies the runtime when it is <see cref="MxcRuntimeState.NotInstalled"/>;
    /// otherwise returns the current status. A failure leaves it NotInstalled with the reason.</summary>
    Task<MxcAvailabilityStatus> InstallRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
