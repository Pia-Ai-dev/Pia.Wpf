using Pia.Services.Interfaces;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Reports every host as unable to sandbox.</summary>
public sealed class UnavailableMxcAvailabilityService : IMxcAvailability
{
    public MxcAvailabilityStatus Status { get; } =
        new(MxcRuntimeState.Unsupported, "Sandboxing is not available in this build.");

    public event EventHandler<MxcAvailabilityStatus>? StatusChanged
    {
        add { }
        remove { }
    }

    public Task<MxcAvailabilityStatus> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public Task<MxcAvailabilityStatus> InstallRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);
}
