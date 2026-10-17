using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Mxc.Sdk.V1;
using Pia.Logging;
using Pia.Services.Exceptions;

namespace Pia.Services.Plugins.Sandbox;

public sealed class MxcMcpLauncher(ILogger<MxcMcpLauncher> logger, IMxcRuntime runtime) : IMxcProcessLauncher
{
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(3);

    internal static ContainerRequest BuildRequest(SandboxLaunchSpec spec)
    {
        var request = new ContainerRequest(CommandLine.Build(spec.Command, spec.Args))
        {
            Filesystem = new FilesystemPolicy(),
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy
                {
                    Default = spec.AllowNetwork ? NetworkAction.Allow : NetworkAction.Deny
                }
            },
            // .NET children fail to start (0xC0000142) with the UI policy disabled.
            Ui = new UiPolicy { Disable = false },
            ContainerName = spec.ContainerName,
            WorkingDirectory = spec.WorkingDirectory,
            TimeoutMs = null,
            // An Environment without InheritDefaultEnvironment replaces the whole block and breaks .NET startup.
            Environment = new Dictionary<string, string>(spec.Environment),
            InheritDefaultEnvironment = true
        };

        request.Filesystem.ReadwritePaths.AddRange(spec.ReadwritePaths);
        request.Filesystem.ReadonlyPaths.AddRange(spec.ReadonlyPaths);
        if (spec.GrantedCommandDirectory is not null)
            request.Filesystem.ReadonlyPaths.Add(spec.GrantedCommandDirectory);
        return request;
    }

    public async Task<LaunchedMxcProcess> LaunchAsync(SandboxLaunchSpec spec, CancellationToken cancellationToken)
    {
        // The only caller of the SDK: it must not run until the runtime is verified in this process.
        if (!runtime.TryLoad(out var unavailable))
            throw new SandboxStartException(unavailable ?? "The sandbox runtime is not available.");

        var lease = SandboxContainerLease.Acquire(spec.ContainerName);
        try
        {
            var request = BuildRequest(spec);
            logger.LogInformation(
                "Starting {Container} under MXC: rw={Rw} ro={Ro} net={Net}",
                spec.ContainerName, request.Filesystem!.ReadwritePaths.Count, request.Filesystem.ReadonlyPaths.Count,
                spec.AllowNetwork ? "allow" : "deny");
            logger.SensitiveDebug("{Container}: {CommandLine} in {Cwd}, rw [{Rw}], ro [{Ro}]",
                spec.ContainerName, request.Command, spec.WorkingDirectory,
                string.Join("; ", request.Filesystem.ReadwritePaths), string.Join("; ", request.Filesystem.ReadonlyPaths));

            var process = await SpawnAsync(request, cancellationToken).ConfigureAwait(false);
            LogWarnings(spec.ContainerName, process, "spawn");

            // The streams must be taken before the first WaitAsync, which drains whatever is still untaken.
            var stdin = process.StandardInput;
            var stdout = process.StandardOutput;
            var stderr = process.StandardError;
            if (stdin is null || stdout is null || stderr is null)
            {
                process.Dispose();
                throw new SandboxStartException("The sandbox gave the server no standard streams.");
            }

            var shutdown = new Lazy<Task>(() => ShutdownAsync(spec.ContainerName, process, stdin, lease));
            return new LaunchedMxcProcess(stdin, stdout, stderr, TryGetId(process), () => shutdown.Value);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static async Task<MxcProcess> SpawnAsync(ContainerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await MxcContainer.SpawnAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MxcException ex)
        {
            throw new SandboxStartException(
                $"The sandbox could not start the server: {ex.Message}"
                + (string.IsNullOrEmpty(ex.Remediation) ? "" : $" {ex.Remediation}"), ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new SandboxStartException($"The sandbox runtime could not be loaded: {ex.Message}", ex);
        }
    }

    private async Task ShutdownAsync(string container, MxcProcess process, Stream stdin, SandboxContainerLease lease)
    {
        try
        {
            try { await stdin.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* already closed */ }

            using var grace = new CancellationTokenSource(GracePeriod);
            try
            {
                var result = await process.WaitAsync(grace.Token).ConfigureAwait(false);
                logger.LogInformation("{Container} exited with code {ExitCode}", container, result.ExitCode);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("{Container} did not exit {Seconds}s after stdin closed; killing it", container, GracePeriod.TotalSeconds);
                await KillAsync(container, process).ConfigureAwait(false);
            }

            LogWarnings(container, process, "exit");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while shutting {Container} down", container);
        }
        finally
        {
            // Disposing frees the sandbox, ends the process tree if it is still alive, and removes the container's ACEs.
            process.Dispose();
            lease.Dispose();
        }
    }

    private async Task KillAsync(string container, MxcProcess process)
    {
        try
        {
            process.Kill();
            using var killGrace = new CancellationTokenSource(GracePeriod);
            await process.WaitAsync(killGrace.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Killing {Container} failed", container);
        }
    }

    private void LogWarnings(string container, MxcProcess process, string phase)
    {
        try
        {
            var warnings = process.Warnings;
            if (warnings.Count == 0)
                return;

            logger.LogWarning("{Container}: {Count} MXC warning(s) at {Phase}", container, warnings.Count, phase);
            foreach (var warning in warnings)
                logger.SensitiveWarning("{Container} MXC warning ({Phase}): {Warning}", container, phase, warning);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static int? TryGetId(MxcProcess process)
    {
        try { return (int)process.Id; }
        catch (Exception) { return null; }
    }
}
