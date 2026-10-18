using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Paths;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Owns the runtime state folders and the grant records kept outside them.</summary>
public sealed class RuntimeStateStore(ILogger<RuntimeStateStore> logger) : IRuntimeStateStore
{
    private const string RecordExtension = ".json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly GrantRecord Damaged = new("", null, default);

    public void WriteGrantRecord(
        string containerName, IReadOnlyList<string> roots, bool stateReadOnly = false, IReadOnlyList<string>? policyRoots = null)
    {
        var target = RecordPath(containerName);
        var directory = PiaPaths.McpSandboxGrantsDirectory;
        Directory.CreateDirectory(directory);

        // From this start's roots alone: a root kept from an earlier start was not granted to this one.
        var stateGranted = roots.Any(root => IsStateFolderOf(containerName, root));
        var previous = ReadRecord(containerName);
        var record = new GrantRecord(
            containerName,
            WithRootsStillOnDisk(roots, previous?.Roots),
            DateTime.UtcNow,
            stateReadOnly,
            WithRootsStillOnDisk(policyRoots ?? [], previous?.PolicyRoots),
            stateGranted);
        var temp = Path.Combine(directory, $".{containerName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public bool? StateGrantedReadOnly(string containerName) =>
        ReadRecord(containerName) is { } record ? record.StateReadOnly == true : null;

    public bool StateGranted(string containerName) =>
        ReadRecord(containerName) is { } record && record.StateGranted != false;

    public IReadOnlyList<string> SweepRoots(string prefix) => RootsOnRecord(prefix, withPolicyRoots: true);

    public IReadOnlyList<string> RuntimeRoots(string prefix) => RootsOnRecord(prefix, withPolicyRoots: false);

    private static IReadOnlyList<string> RootsOnRecord(string prefix, bool withPolicyRoots)
    {
        if (!IsPlainPrefix(prefix))
            return [];

        var roots = new List<string>();
        var state = PiaPaths.McpRuntimeStateDirectory(prefix);
        if (Directory.Exists(state))
            roots.Add(state);

        foreach (var record in SandboxLaunchSpecBuilder.ContainerNames(prefix).Select(ReadRecord))
        {
            roots.AddRange(Valid(record?.Roots));
            if (withPolicyRoots)
                roots.AddRange(Valid(record?.PolicyRoots));
        }

        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public bool TryDelete(string prefix, Action? beforeDelete = null)
    {
        if (!IsPlainPrefix(prefix))
        {
            logger.LogWarning("Runtime state not deleted: the prefix is not a plain name");
            return false;
        }

        var state = PiaPaths.McpRuntimeStateDirectory(prefix);
        using var held = RuntimeStateLocks.TryAcquire(state);
        if (held is null || SandboxLaunchSpecBuilder.ContainerNames(prefix).Any(SandboxContainerLease.IsActive))
            return false;

        try
        {
            beforeDelete?.Invoke();
            var entries = StateFolderSafety.EnumerateNoLinks(state).Count();
            StateFolderSafety.DeleteTree(state);
            foreach (var containerName in SandboxLaunchSpecBuilder.ContainerNames(prefix))
                File.Delete(RecordPath(containerName));

            logger.LogInformation("Runtime state for prefix {Prefix} deleted: {Entries} entries", prefix, entries);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Runtime state for prefix {Prefix} not deleted: {Error}", prefix, ex.GetType().Name);
            logger.SensitiveDebug("Runtime state folder {Path} not deleted: {Message}", state, ex.Message);
        }

        return true;
    }

    public IReadOnlyList<string> OrphanPrefixes(IReadOnlySet<string> knownPrefixes)
    {
        var prefixes = new List<string>();

        if (Directory.Exists(PiaPaths.McpSandboxRoot))
        {
            prefixes.AddRange(Directory.EnumerateDirectories(PiaPaths.McpSandboxRoot)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.EndsWith(PiaPaths.McpRuntimeStateSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(name => name[..^PiaPaths.McpRuntimeStateSuffix.Length]));
        }

        if (Directory.Exists(PiaPaths.McpSandboxGrantsDirectory))
        {
            prefixes.AddRange(Directory.EnumerateFiles(PiaPaths.McpSandboxGrantsDirectory, "*" + RecordExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => SandboxLaunchSpecBuilder.ContainerNames(SandboxLaunchSpecBuilder.PrefixOf(name))
                    .Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(SandboxLaunchSpecBuilder.PrefixOf));
        }

        return [.. prefixes
            .Where(prefix => prefix.Length > 0 && !knownPrefixes.Contains(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string RecordPath(string containerName)
    {
        if (!IsPlainFileName(containerName))
            throw new ArgumentException("A container name must be a plain file name.", nameof(containerName));

        return Path.Combine(PiaPaths.McpSandboxGrantsDirectory, containerName + RecordExtension);
    }

    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // A prefix read from saved settings must not lead the delete out of the sandbox root.
    private static bool IsPlainPrefix(string prefix)
    {
        if (prefix.Length == 0 || !SandboxLaunchSpecBuilder.ContainerNames(prefix).All(IsPlainFileName))
            return false;

        var parent = Path.GetDirectoryName(Path.GetFullPath(PiaPaths.McpRuntimeStateDirectory(prefix)));
        return string.Equals(
            parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(PiaPaths.McpSandboxRoot)), StringComparison.OrdinalIgnoreCase);
    }

    private static GrantRecord? ReadRecord(string containerName)
    {
        var path = RecordPath(containerName);
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<GrantRecord>(File.ReadAllText(path), Json) ?? Damaged;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Damaged;
        }
    }

    private static IEnumerable<string> Valid(IReadOnlyList<string>? roots) =>
        (roots ?? []).Where(root => !string.IsNullOrWhiteSpace(root));

    // A root a failed sweep left behind stays on record until a later sweep can reach it.
    private static List<string> WithRootsStillOnDisk(IReadOnlyList<string> roots, IReadOnlyList<string>? earlier) =>
        [.. roots
            .Concat(Valid(earlier).Where(root => Directory.Exists(root) || File.Exists(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool IsStateFolderOf(string containerName, string root) =>
        !string.IsNullOrWhiteSpace(root)
        && string.Equals(
            SandboxLaunchSpecBuilder.Normalize(root),
            SandboxLaunchSpecBuilder.Normalize(PiaPaths.McpRuntimeStateDirectory(SandboxLaunchSpecBuilder.PrefixOf(containerName))),
            StringComparison.OrdinalIgnoreCase);

    // A missing StateReadOnly counts as a read-write grant, a missing StateGranted as a grant.
    private sealed record GrantRecord(
        string Container,
        IReadOnlyList<string>? Roots,
        DateTime WrittenUtc,
        bool? StateReadOnly = null,
        IReadOnlyList<string>? PolicyRoots = null,
        bool? StateGranted = null);
}
