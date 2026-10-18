namespace Pia.Services.Plugins.Sandbox.Runtimes;

public sealed class RuntimeRootVerifier : IRuntimeRootVerifier
{
    private readonly Func<SandboxGuardEnvironment> _environment;

    public RuntimeRootVerifier() : this(SandboxGuardEnvironment.Current)
    {
    }

    internal RuntimeRootVerifier(Func<SandboxGuardEnvironment> environment) => _environment = environment;

    public RuntimeIssue? Check(string root, bool isProgramFolder) =>
        SandboxPathGuard.JudgeRuntimeRoot(SandboxLaunchSpecBuilder.Normalize(root), isProgramFolder, _environment());
}
