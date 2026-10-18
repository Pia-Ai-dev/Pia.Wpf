using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Tests.TestInfrastructure;

/// <summary>The real machine, except that detection looks only in the given folders, so a test never grants the
/// developer's own toolchains; works in Release too, where the real override is compiled out.</summary>
public sealed class OverridingRuntimeHost(IRuntimeHostService inner, IReadOnlyList<string> folders) : IRuntimeHostService
{
    public OverridingRuntimeHost(params string[] folders)
        : this(Real(), folders)
    {
    }

    public static RuntimeHostService Real() => new(new ElevationService(NullLogger<ElevationService>.Instance));

    public IReadOnlyList<string>? CandidateOverride => folders;

    public IReadOnlyList<string> RegistryPathEntries() => inner.RegistryPathEntries();

    public string RegistryPath() => inner.RegistryPath();

    public IReadOnlyList<string> UserEnvironmentNames() => inner.UserEnvironmentNames();

    public string? RegistryEnvironmentValue(string name) => inner.RegistryEnvironmentValue(name);

    public IReadOnlyDictionary<string, string>? DefaultUserEnvironment() => inner.DefaultUserEnvironment();

    public string LocalAppData => inner.LocalAppData;

    public string RoamingAppData => inner.RoamingAppData;

    public string UserProfile => inner.UserProfile;

    public string WindowsDirectory => inner.WindowsDirectory;

    public IReadOnlyList<string> ProgramFilesDirectories => inner.ProgramFilesDirectories;

    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path, string searchPattern) =>
        inner.EnumerateDirectories(path, searchPattern);

    public string? ReadAllText(string path) => inner.ReadAllText(path);

    public string? ResolveFinalPath(string path) => inner.ResolveFinalPath(path);

    public string? FileVersion(string path) => inner.FileVersion(path);

    public bool IsContainerReadable(string path) => inner.IsContainerReadable(path);

    public bool CanUserWriteDacl(string path) => inner.CanUserWriteDacl(path);
}
