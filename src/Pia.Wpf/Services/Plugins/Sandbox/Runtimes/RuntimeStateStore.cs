using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Paths;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Owns the runtime state folders and the grant records kept outside them.</summary>
public sealed class RuntimeStateStore(ILogger<RuntimeStateStore> logger) : IRuntimeStateStore
{
    private const string ContainerPrefix = "pia-mcp-";
    private const string ProbeSuffix = "-probe";
    private const string RecordExtension = ".json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void WriteGrantRecord(string containerName, IReadOnlyList<string> roots, bool stateReadOnly = false)
    {
        CheckContainerName(containerName);

        var directory = PiaPaths.McpSandboxGrantsDirectory;
        Directory.CreateDirectory(directory);
        var record = new GrantRecord(containerName, [.. roots], DateTime.UtcNow, stateReadOnly);
        var target = RecordPath(containerName);
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

    public bool? StateGrantedReadOnly(string containerName)
    {
        CheckContainerName(containerName);
        try
        {
            var path = RecordPath(containerName);
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<GrantRecord>(File.ReadAllText(path), Json)?.StateReadOnly == true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> SweepRoots(string prefix)
    {
        var roots = new List<string>();
        var state = PiaPaths.McpRuntimeStateDirectory(prefix);
        if (Directory.Exists(state))
            roots.Add(state);

        foreach (var containerName in SandboxLaunchSpecBuilder.ContainerNames(prefix))
            roots.AddRange(ReadRoots(containerName));

        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public bool TryDelete(string prefix, Action? beforeDelete = null)
    {
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
                .Where(name => name.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(name => name[ContainerPrefix.Length..])
                .Select(name => name.EndsWith(ProbeSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^ProbeSuffix.Length] : name));
        }

        return [.. prefixes
            .Where(prefix => prefix.Length > 0 && !knownPrefixes.Contains(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string RecordPath(string containerName) =>
        Path.Combine(PiaPaths.McpSandboxGrantsDirectory, containerName + RecordExtension);

    private static void CheckContainerName(string containerName)
    {
        if (containerName.Length == 0 || containerName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A container name must be a plain file name.", nameof(containerName));
    }

    private static IReadOnlyList<string> ReadRoots(string containerName)
    {
        try
        {
            var path = RecordPath(containerName);
            if (!File.Exists(path))
                return [];

            var record = JsonSerializer.Deserialize<GrantRecord>(File.ReadAllText(path), Json);
            return record?.Roots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A record without StateReadOnly counts as a read-write grant.
    private sealed record GrantRecord(string Container, IReadOnlyList<string>? Roots, DateTime WrittenUtc, bool? StateReadOnly = null);
}
