using System.Collections.Concurrent;
using Pia.Services.Plugins.Sandbox;

namespace Pia.Tests.TestInfrastructure;

/// <summary>Keeps every spec that reached the launcher, after the provisioner had its say.</summary>
public sealed class RecordingMxcLauncher(IMxcProcessLauncher inner) : IMxcProcessLauncher
{
    private readonly ConcurrentQueue<SandboxLaunchSpec> _specs = new();

    public IReadOnlyList<SandboxLaunchSpec> Specs => [.. _specs];

    public Task<LaunchedMxcProcess> LaunchAsync(SandboxLaunchSpec spec, CancellationToken cancellationToken)
    {
        _specs.Enqueue(spec);
        return inner.LaunchAsync(spec, cancellationToken);
    }
}
