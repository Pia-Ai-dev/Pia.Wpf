namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>The machine facts detection and planning read; a seam so they test without a real profile.</summary>
public interface IRuntimeHostService
{
    IReadOnlyList<string> RegistryPathEntries();     // HKLM then HKCU Path, expanded, in order
    string RegistryPath();                           // the same, joined with ';'
    IReadOnlyList<string> UserEnvironmentNames();    // value names under HKCU\Environment
    string? RegistryEnvironmentValue(string name);   // HKCU over HKLM, expanded
    IReadOnlyDictionary<string, string>? DefaultUserEnvironment(); // the registry-built block, never Pia's own process variables
    IReadOnlyList<string>? CandidateOverride { get; } // PIA_DEBUG_SANDBOX_RUNTIME_ROOTS, DEBUG builds only
    string LocalAppData { get; }
    string RoamingAppData { get; }
    string UserProfile { get; }
    string WindowsDirectory { get; }
    IReadOnlyList<string> ProgramFilesDirectories { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> EnumerateDirectories(string path, string searchPattern);
    string? ReadAllText(string path);
    string? ResolveFinalPath(string path);           // symlinks and junctions anywhere in the path; null when missing
    string? FileVersion(string path);                // ProductVersion
    bool IsContainerReadable(string path);           // allow RX for S-1-15-2-1 or S-1-15-2-2, no deny
    bool CanUserWriteDacl(string path);              // as the medium-IL user, also when Pia runs elevated
}
