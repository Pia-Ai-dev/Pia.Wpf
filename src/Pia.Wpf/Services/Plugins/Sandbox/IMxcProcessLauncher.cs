using System.IO;

namespace Pia.Services.Plugins.Sandbox;

/// <param name="ShutdownAsync">Closes stdin, waits briefly for the server to exit, kills it if it does not, then frees the container. Idempotent.</param>
public sealed record LaunchedMxcProcess(
    Stream StandardInput,
    Stream StandardOutput,
    Stream StandardError,
    int? ProcessId,
    Func<Task> ShutdownAsync);

/// <summary>Starts a server inside a container; the seam that keeps the native runtime out of everything above it.</summary>
public interface IMxcProcessLauncher
{
    Task<LaunchedMxcProcess> LaunchAsync(SandboxLaunchSpec spec, CancellationToken cancellationToken);
}
