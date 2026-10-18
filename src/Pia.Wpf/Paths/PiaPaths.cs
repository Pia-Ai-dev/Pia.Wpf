using System.IO;

namespace Pia.Paths;

/// <summary>
/// The single resolver for Pia's on-disk roots. The two data roots honour an environment override so a UI-test
/// replay can run against a throwaway profile instead of the developer's; downloaded artifacts deliberately do
/// not, because re-fetching gigabytes of models per run costs more than sharing them.
/// </summary>
public static class PiaPaths
{
    public const string RoamingDataDirectoryEnvVar = "PIA_DATA_DIR";
    public const string LocalDataDirectoryEnvVar = "PIA_LOCAL_DATA_DIR";

    private const string ProductFolder = "Pia";

    private static string? _roamingDataDirectory;
    private static string? _localDataDirectory;

    /// <summary>Settings, providers, templates, sync delete-tracking. Defaults to <c>%APPDATA%\Pia</c>.</summary>
    public static string RoamingDataDirectory => _roamingDataDirectory ??=
        Resolve(RoamingDataDirectoryEnvVar, Environment.SpecialFolder.ApplicationData);

    /// <summary>History DB, logs, vault, run workspaces, transcripts. Defaults to <c>%LOCALAPPDATA%\Pia</c>.</summary>
    public static string LocalDataDirectory => _localDataDirectory ??=
        Resolve(LocalDataDirectoryEnvVar, Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Where the file sink writes <c>pia-*.log</c>.</summary>
    public static string LogsDirectory => Path.Combine(LocalDataDirectory, "Logs");

    /// <summary>
    /// Diagnostics exports. A SIBLING of <see cref="LogsDirectory"/> on purpose — an export written inside
    /// Logs would be picked up and shipped by the next one.
    /// </summary>
    public static string DiagnosticsDirectory => Path.Combine(LocalDataDirectory, "Diagnostics");

    /// <summary>Where a virtual-file drop (a mail dragged out of Outlook) writes the file it had to materialise.</summary>
    public static string DropCacheDirectory => Path.Combine(LocalDataDirectory, "DropCache");

    /// <summary>Dictation WAVs between recording and transcription; startup sweeps whatever a crash left behind.</summary>
    public static string RecordingsDirectory => Path.Combine(LocalDataDirectory, "Recordings");

    /// <summary>One JSONL line per screen capture. Routed, so a walkthrough against a throwaway profile does not
    /// leave rows in the user's real trail.</summary>
    public static string ScreenCaptureAuditDirectory => Path.Combine(LocalDataDirectory, "ScreenCaptureAudit");

    /// <summary>Per-server read-write folders for sandboxed MCP servers. Routed: the files are user data.</summary>
    public static string McpSandboxRoot => Path.Combine(LocalDataDirectory, "McpSandbox");

    /// <summary>A sibling of the server's own folder, never inside it, so its file tools never see the runtime.</summary>
    public static string McpRuntimeStateDirectory(string prefix) => Path.Combine(McpSandboxRoot, prefix + ".runtime");

    /// <summary>Outside every state folder, because a server can write its own.</summary>
    public static string McpSandboxGrantsDirectory => Path.Combine(LocalDataDirectory, "McpSandboxGrants");

    /// <summary>Granted read-only to every Node start; no container can write it.</summary>
    public static string McpSandboxSupportDirectory => Path.Combine(LocalDataDirectory, "McpSandboxSupport");

    /// <summary>True when either data root came from the environment rather than the real user profile.</summary>
    public static bool IsOverridden =>
        HasOverride(RoamingDataDirectoryEnvVar) || HasOverride(LocalDataDirectoryEnvVar);

    // Redaction keys for the log sink, not data paths: the REAL profile roots, which an override still sits under.
    public static string RoamingProfileRoot => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string LocalProfileRoot => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string UserProfileRoot => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Downloaded artifacts and audit trails, always on the real profile — see the class summary. Exposed as
    // individual leaves rather than one shared root so a future *data* path cannot reach for "the real root"
    // and silently lose its override.
    public static string ModelsDirectory => Path.Combine(RealLocalRoot, "Models");

    public static string TtsDirectory => Path.Combine(RealLocalRoot, "Tts");

    /// <summary>The pre-sherpa tree: piper.exe plus rhasspy voices the current engine cannot load.</summary>
    public static string LegacyPiperDirectory => Path.Combine(RealLocalRoot, "Piper");

    public static string BrowsersDirectory => Path.Combine(RealLocalRoot, "Browsers");

    public static string PluginsDirectory => Path.Combine(RealLocalRoot, "plugins");

    public static string ConsentAuditDirectory => Path.Combine(RealLocalRoot, "ConsentAudit");

    public static string ConsentEvidenceDirectory => Path.Combine(RealLocalRoot, "ConsentEvidence");

    /// <summary>Version of the downloaded MXC native runtime; equals the referenced Microsoft.Mxc.Sdk package.</summary>
    public const string MxcRuntimeVersion = "1.0.1";

    public static string MxcRuntimeDirectory => Path.Combine(RealLocalRoot, "Runtimes", "Mxc", MxcRuntimeVersion);

    /// <summary>Where a pre-fetched runtime package is looked for before anything is downloaded.</summary>
    public static string MxcRuntimeArchivePath =>
        Path.Combine(RealLocalRoot, "Runtimes", "Mxc", $"microsoft.mxc.sdk.{MxcRuntimeVersion}.nupkg");

    private static string RealLocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    private static bool HasOverride(string envVar) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar));

    internal static string Resolve(string envVar, Environment.SpecialFolder profileFolder)
    {
        var configured = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(Environment.GetFolderPath(profileFolder), ProductFolder);

        // Deliberately not guarded: a malformed override must surface as a directory-creation failure rather
        // than fall back to the real profile, which is the write the override exists to prevent.
        return Path.GetFullPath(configured.Trim());
    }

    /// <summary>Test seam: applies both overrides and drops the resolved cache, restoring both on dispose.</summary>
    internal static IDisposable OverrideForTests(string? roamingDirectory, string? localDirectory) =>
        new TestOverride(roamingDirectory, localDirectory);

    private sealed class TestOverride : IDisposable
    {
        private readonly string? _previousRoaming;
        private readonly string? _previousLocal;

        internal TestOverride(string? roamingDirectory, string? localDirectory)
        {
            _previousRoaming = Environment.GetEnvironmentVariable(RoamingDataDirectoryEnvVar);
            _previousLocal = Environment.GetEnvironmentVariable(LocalDataDirectoryEnvVar);
            Apply(roamingDirectory, localDirectory);
        }

        public void Dispose() => Apply(_previousRoaming, _previousLocal);

        private static void Apply(string? roamingDirectory, string? localDirectory)
        {
            Environment.SetEnvironmentVariable(RoamingDataDirectoryEnvVar, roamingDirectory);
            Environment.SetEnvironmentVariable(LocalDataDirectoryEnvVar, localDirectory);
            _roamingDataDirectory = null;
            _localDataDirectory = null;
        }
    }
}
