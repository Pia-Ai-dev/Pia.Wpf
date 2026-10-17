using Microsoft.Extensions.Logging;
using Microsoft.Mxc.Sdk.V1;
using Pia.Services.Interfaces;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>Whether this host can run a sandboxed server now. The SDK is only asked once the native runtime is
/// installed and verified in this process: until then every SDK call throws.</summary>
public sealed class MxcAvailabilityService : IMxcAvailability
{
    private readonly MxcRuntimeProvisioner _provisioner;
    private readonly ILogger<MxcAvailabilityService> _logger;
    private readonly Func<(bool Supported, string? Reason)> _platformSupport;
    private readonly object _gate = new();
    private MxcAvailabilityStatus? _status;
    private Task<MxcAvailabilityStatus>? _install;

    public MxcAvailabilityService(MxcRuntimeProvisioner provisioner, ILogger<MxcAvailabilityService> logger)
        : this(provisioner, logger, SdkPlatformSupport)
    {
    }

    internal MxcAvailabilityService(
        MxcRuntimeProvisioner provisioner,
        ILogger<MxcAvailabilityService> logger,
        Func<(bool Supported, string? Reason)> platformSupport)
    {
        _provisioner = provisioner;
        _logger = logger;
        _platformSupport = platformSupport;
    }

    public event EventHandler<MxcAvailabilityStatus>? StatusChanged;

    /// <summary>Computed on first read, which verifies the installed file and asks the SDK once.</summary>
    public MxcAvailabilityStatus Status
    {
        get
        {
            lock (_gate)
                return _status ??= Compute();
        }
    }

    public Task<MxcAvailabilityStatus> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Publish(Compute()), cancellationToken);

    public Task<MxcAvailabilityStatus> InstallRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<MxcAvailabilityStatus>? mine = null;
        Task<MxcAvailabilityStatus> running;
        lock (_gate)
        {
            _status ??= Compute();
            if (_status.State is MxcRuntimeState.Ready or MxcRuntimeState.Unsupported)
                return Task.FromResult(_status);

            if (_install is null)
            {
                mine = new TaskCompletionSource<MxcAvailabilityStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                _install = mine.Task;
            }

            running = _install;
        }

        if (mine is not null)
        {
            Publish(new MxcAvailabilityStatus(MxcRuntimeState.Downloading));
            _ = RunInstallAsync(progress, mine);
        }

        return running.WaitAsync(cancellationToken);
    }

    // Shared by every caller, so one caller cancelling stops its own wait and never the download.
    private async Task RunInstallAsync(IProgress<ModelDownloadProgress>? progress, TaskCompletionSource<MxcAvailabilityStatus> completion)
    {
        MxcAvailabilityStatus outcome;
        try
        {
            var result = await _provisioner.InstallAsync(progress, CancellationToken.None).ConfigureAwait(false);
            outcome = Publish(result.Installed
                ? Compute()
                : new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled, result.Reason));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Installing the MXC runtime failed unexpectedly");
            outcome = Publish(new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled, $"The sandbox runtime could not be installed: {ex.Message}"));
        }

        lock (_gate)
            _install = null;
        completion.SetResult(outcome);
    }

    private MxcAvailabilityStatus Compute()
    {
        if (!_provisioner.IsInstalled)
            return new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled);

        if (!_provisioner.TryLoad(out var reason))
            return new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled, reason);

        var (supported, unsupportedReason) = _platformSupport();
        return supported
            ? new MxcAvailabilityStatus(MxcRuntimeState.Ready)
            : new MxcAvailabilityStatus(MxcRuntimeState.Unsupported, unsupportedReason ?? "This PC cannot create sandboxes.");
    }

    private MxcAvailabilityStatus Publish(MxcAvailabilityStatus next)
    {
        MxcAvailabilityStatus? previous;
        lock (_gate)
        {
            previous = _status;
            _status = next;
        }

        if (!Equals(previous, next))
        {
            _logger.LogInformation("MXC sandbox availability: {State}", next.State);
            StatusChanged?.Invoke(this, next);
        }

        return next;
    }

    private static (bool Supported, string? Reason) SdkPlatformSupport()
    {
        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            return (support.IsSupported, support.Reason);
        }
        catch (Exception ex)
        {
            return (false, $"The sandbox runtime could not be checked: {ex.Message}");
        }
    }
}
