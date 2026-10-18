using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pia.Native;
using Pia.Paths;
using Pia.Services.Interfaces;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Reads the registry rather than Pia's own environment, which is stale after a toolchain install.</summary>
public sealed partial class RuntimeHostService(IElevationService elevation) : IRuntimeHostService
{
    public const string CandidateOverrideVariable = "PIA_DEBUG_SANDBOX_RUNTIME_ROOTS";

    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserEnvironmentKey = "Environment";
    private const string PathName = "Path";

    private const uint GenericAll = 0x10000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericRead = 0x80000000;

    private const FileSystemRights FileGenericRead = FileSystemRights.ReadData | FileSystemRights.ReadAttributes
        | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;

    private const FileSystemRights FileGenericExecute = FileSystemRights.ExecuteFile | FileSystemRights.ReadAttributes
        | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;

    private static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");
    private static readonly SecurityIdentifier AllRestrictedApplicationPackages = new("S-1-15-2-2");

    [GeneratedRegex("%([^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();

    public IReadOnlyList<string>? CandidateOverride
    {
        get
        {
            // A Release override would change which folders get granted.
#if DEBUG
            var raw = Environment.GetEnvironmentVariable(CandidateOverrideVariable);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var roots = raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return roots.Length == 0 ? null : roots;
#else
            return null;
#endif
        }
    }

    public string LocalAppData => PiaPaths.LocalProfileRoot;

    public string RoamingAppData => PiaPaths.RoamingProfileRoot;

    public string UserProfile => PiaPaths.UserProfileRoot;

    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<string> ProgramFilesDirectories =>
    [
        .. new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    public IReadOnlyList<string> RegistryPathEntries()
    {
        var registry = RegistryEnvironment.Read();
        return
        [
            .. new[] { registry.Machine, registry.User }
                .SelectMany(scope => (scope.GetValueOrDefault(PathName) ?? "")
                    .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Select(entry => registry.Expand(entry).Trim())
                .Where(entry => entry.Length > 0),
        ];
    }

    public string RegistryPath() => string.Join(';', RegistryPathEntries());

    public IReadOnlyList<string> UserEnvironmentNames() => ReadUserEnvironmentNames();

    /// <summary>The value names under <c>HKCU\Environment</c>, empty on any error; needs no DI, so blanking never depends on it.</summary>
    public static IReadOnlyList<string> ReadUserEnvironmentNames() =>
        ReadUserEnvironmentNames(Registry.CurrentUser, UserEnvironmentKey);

    internal static IReadOnlyList<string> ReadUserEnvironmentNames(RegistryKey parent, string subKey)
    {
        try
        {
            using var key = parent.OpenSubKey(subKey);
            return key is null ? [] : [.. key.GetValueNames().Where(name => name.Length > 0)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public string? RegistryEnvironmentValue(string name)
    {
        var registry = RegistryEnvironment.Read();
        return registry.Raw(name) is { } raw ? registry.Expand(raw) : null;
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path, string searchPattern)
    {
        try
        {
            return [.. Directory.GetDirectories(path, searchPattern).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (IsFileSystemError(e))
        {
            return [];
        }
    }

    public string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (IsFileSystemError(e))
        {
            return null;
        }
    }

    public string? ResolveFinalPath(string path) => FinalPathProbe.Resolve(path);

    public string? FileVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (string.IsNullOrEmpty(version))
                return null;

            var end = version.IndexOfAny(['+', ' ']);
            var trimmed = (end < 0 ? version : version[..end]).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        catch (Exception e) when (IsFileSystemError(e))
        {
            return null;
        }
    }

    public bool IsContainerReadable(string path)
    {
        try
        {
            FileSystemSecurity security;
            if (File.Exists(path))
                security = new FileInfo(path).GetAccessControl();
            else if (Directory.Exists(path))
                security = new DirectoryInfo(path).GetAccessControl();
            else
                return false;

            return GrantsContainerRead(security);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool GrantsContainerRead(FileSystemSecurity security)
    {
        var allowed = 0u;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (!rule.IdentityReference.Equals(AllApplicationPackages) && !rule.IdentityReference.Equals(AllRestrictedApplicationPackages))
                continue;

            if (rule.AccessControlType == AccessControlType.Deny)
                return false;

            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)
                allowed |= MapGenericBits(unchecked((uint)rule.FileSystemRights));
        }

        const uint readAndExecute = (uint)FileSystemRights.ReadAndExecute;
        return (allowed & readAndExecute) == readAndExecute;
    }

    public bool CanUserWriteDacl(string path) =>
        elevation.IsElevated
            ? TokenAccessProbe.CanLinkedTokenWriteDacl(path) ?? FileSecurityProbe.CanWriteDacl(path)
            : FileSecurityProbe.CanWriteDacl(path);

    private static uint MapGenericBits(uint mask)
    {
        if ((mask & GenericAll) != 0)
            mask |= (uint)FileSystemRights.FullControl;
        if ((mask & GenericRead) != 0)
            mask |= (uint)FileGenericRead;
        if ((mask & GenericExecute) != 0)
            mask |= (uint)FileGenericExecute;
        return mask;
    }

    private static bool IsFileSystemError(Exception e) =>
        e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private sealed class RegistryEnvironment(Dictionary<string, string> machine, Dictionary<string, string> user)
    {
        public Dictionary<string, string> Machine { get; } = machine;

        public Dictionary<string, string> User { get; } = user;

        public static RegistryEnvironment Read() =>
            new(ReadValues(Registry.LocalMachine, MachineEnvironmentKey), ReadValues(Registry.CurrentUser, UserEnvironmentKey));

        public string? Raw(string name) => User.TryGetValue(name, out var value) ? value : Machine.GetValueOrDefault(name);

        // One pass: a Path that names %PATH% would otherwise expand into itself.
        public string Expand(string value) =>
            VariableReference().Replace(value, match =>
                Raw(match.Groups[1].Value) ?? Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);

        private static Dictionary<string, string> ReadValues(RegistryKey hive, string subKey)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key is null)
                    return values;

                foreach (var name in key.GetValueNames())
                {
                    if (name.Length > 0 && key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                        values[name] = value;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                // An unreadable scope contributes nothing.
            }

            return values;
        }
    }
}
