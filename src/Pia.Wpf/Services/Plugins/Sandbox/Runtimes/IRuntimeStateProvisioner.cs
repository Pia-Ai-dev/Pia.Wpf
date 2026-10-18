namespace Pia.Services.Plugins.Sandbox.Runtimes;

public interface IRuntimeStateProvisioner
{
    /// <summary>Writes Pia's folders and files and runs the uv install; returns the spec to launch.</summary>
    Task<SandboxLaunchSpec> PrepareAsync(SandboxLaunchSpec spec, Action<string> stderrLine, CancellationToken cancellationToken);
}
