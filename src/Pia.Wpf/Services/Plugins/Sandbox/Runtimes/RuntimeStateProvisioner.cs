using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Exceptions;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>Everything on disk a runner needs before its container starts; the caller holds the state folder's lock and the container's lease.</summary>
public sealed class RuntimeStateProvisioner(IRuntimeStateStore store, UvToolRunner uv, ILogger<RuntimeStateProvisioner> logger)
    : IRuntimeStateProvisioner
{
    internal const string SpecRecordName = "pia-uv-spec.json";

    internal const string InstallFailedMessage =
        "uv could not install the package. Only packages published as wheels are supported; the server's error output has the details.";

    internal const string OfflineMessage =
        "Network access is off and this package is not installed yet. Allow network access for the first start.";

    internal const string NoCommandMessage = "The installed package does not provide the command this server names.";

    internal const string InUseMessage =
        "This server's package is in use by the running server. Stop the server, then try again.";

    internal const string CouldNotRunUvMessage = "Pia could not run uv.";

    internal const string CouldNotPrepareMessage = "Pia could not prepare the server's runtime folder.";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SandboxLaunchSpec> PrepareAsync(SandboxLaunchSpec spec, Action<string> stderrLine, CancellationToken cancellationToken)
    {
        try
        {
            if (spec.RunnerKind != RunnerKind.None && spec.Preparation is { } preparation)
                return await PrepareCoreAsync(spec, preparation, stderrLine, cancellationToken).ConfigureAwait(false);

            WriteGrantRecord(spec);
            return spec;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailure(spec.ContainerName, ex);
            throw new SandboxStartException(CouldNotPrepareMessage);
        }
    }

    private async Task<SandboxLaunchSpec> PrepareCoreAsync(
        SandboxLaunchSpec spec, RuntimePreparation preparation, Action<string> stderrLine, CancellationToken cancellationToken)
    {
        var state = spec.RuntimeStateDirectory is { } s ? SandboxLaunchSpecBuilder.Normalize(s) : null;
        var support = spec.SupportDirectory is { } p ? SandboxLaunchSpecBuilder.Normalize(p) : null;
        var files = preparation.Files.Select(file => (File: file, Root: RootOf(file.Path, state, support))).ToList();
        var directories = preparation.Directories.Select(directory => (Path: directory, Root: RootOf(directory, state, support))).ToList();
        if (preparation.Uv is { } uvRequest)
        {
            CheckUvRequest(uvRequest, state);
            ClearStateAContainerCouldHaveWritten(spec.ContainerName, state!);
        }

        if (support is not null)
            Directory.CreateDirectory(support);

        foreach (var (file, _) in files.Where(f => f.Root == support))
            WriteSupportFile(file);

        foreach (var (directory, root) in directories)
        {
            if (root == state)
                StateFolderSafety.EnsureNoReparsePoints(root, directory);
            Directory.CreateDirectory(directory);
        }

        foreach (var (file, root) in files.Where(f => f.Root != support))
            WriteStateFile(root, file);

        WriteGrantRecord(spec);

        if (preparation.Uv is not { } request)
            return spec;

        cancellationToken.ThrowIfCancellationRequested();
        return await PrepareUvAsync(spec, request, stderrLine, cancellationToken).ConfigureAwait(false);
    }

    private static void CheckUvRequest(UvPrepareRequest request, string? state)
    {
        var uvState = SandboxLaunchSpecBuilder.Normalize(request.StateDirectory);
        if (state is null || !string.Equals(uvState, state, StringComparison.OrdinalIgnoreCase)
            || request.CacheDirectoriesToPrune.Any(cache => !IsStrictlyUnder(cache, state)))
            throw new InvalidOperationException("A uv preparation must stay inside the runtime state folder.");
    }

    // uv runs uncontained and executes what it finds, so it never reads a folder a container was allowed to write.
    // The grant records sit outside the state folder, where no container can forge them.
    private void ClearStateAContainerCouldHaveWritten(string containerName, string state)
    {
        var grants = SandboxLaunchSpecBuilder.ContainerNames(SandboxLaunchSpecBuilder.PrefixOf(containerName))
            .Select(name => (Name: name, ReadOnly: store.StateGrantedReadOnly(name)))
            .ToList();
        var onlyReadOnly = grants.Any(grant => grant.ReadOnly == true) && grants.All(grant => grant.ReadOnly != false);
        var clear = !onlyReadOnly && HasEntries(state);

        var running = grants.Where(grant => IsOtherRunning(grant.Name, containerName)).ToList();
        if (running.Count > 0 && (clear || running.Any(grant => grant.ReadOnly == false)))
        {
            logger.LogInformation("Runtime state for {Container} not prepared: a running container can write it", containerName);
            throw new SandboxStartException(InUseMessage);
        }

        if (!clear)
            return;

        StateFolderSafety.DeleteTree(state);
        logger.LogInformation("Runtime state for {Container} cleared before the uv install", containerName);

        // Nothing a sibling wrote is left, so its read-write grant must not force the next start to clear again.
        foreach (var grant in grants.Where(grant =>
                     grant.ReadOnly == false && !string.Equals(grant.Name, containerName, StringComparison.OrdinalIgnoreCase)))
            store.WriteGrantRecord(grant.Name, [], stateReadOnly: true);
    }

    // Counts a planted link as an entry; the reparse-safe walks would skip it.
    private static bool HasEntries(string state)
    {
        var attributes = new FileInfo(state).Attributes;
        if ((int)attributes == -1)
            return false;

        return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory
            || Directory.EnumerateFileSystemEntries(state).Any();
    }

    private static bool IsStrictlyUnder(string path, string root)
    {
        var full = SandboxLaunchSpecBuilder.Normalize(path);
        return SandboxLaunchSpecBuilder.IsSameOrUnder(full, root) && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SandboxLaunchSpec> PrepareUvAsync(
        SandboxLaunchSpec spec, UvPrepareRequest request, Action<string> stderrLine, CancellationToken cancellationToken)
    {
        var state = SandboxLaunchSpecBuilder.Normalize(request.StateDirectory);
        StateFolderSafety.EnsureNoReparsePoints(state, state);
        Directory.CreateDirectory(state);

        var recordPath = Path.Combine(state, SpecRecordName);
        var wanted = new UvSpecRecord(request.Spec.Package, [.. request.Spec.With], request.Spec.Python);
        var recorded = ReadSpecRecord(recordPath);
        // uv runs a tool venv's python on the host, where a .pth file in it would execute; only the container may.
        if (recorded is not null && recorded.SameAs(wanted)
            && UvEntryPointResolver.Find(state, request.Spec) is { } installed
            && File.Exists(UvEntryPointResolver.PythonFor(installed.ToolFolder)))
            return Launch(spec, request, installed);

        if (request.Offline)
            throw new SandboxStartException(OfflineMessage);

        RefuseWhileAContainerRuns(spec.ContainerName);
        RemoveInstalledTool(spec.ContainerName, state);

        int exitCode;
        try
        {
            exitCode = await uv.InstallAsync(request, stderrLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            LogFailure(spec.ContainerName, ex);
            throw new SandboxStartException(CouldNotRunUvMessage);
        }

        if (exitCode != 0)
            throw new SandboxStartException(InstallFailedMessage);

        StateFolderSafety.EnsureNoReparsePoints(state, recordPath);
        File.WriteAllText(recordPath, JsonSerializer.Serialize(wanted, Json), Utf8NoBom);
        PruneCaches(spec.ContainerName, request.CacheDirectoriesToPrune);

        if (UvEntryPointResolver.Find(state, request.Spec) is not { } entry)
            throw new SandboxStartException(NoCommandMessage);

        return Launch(spec, request, entry);
    }

    private static SandboxLaunchSpec Launch(
        SandboxLaunchSpec spec, UvPrepareRequest request, (string ToolFolder, string Module, string Attr) entry) =>
        spec with
        {
            Command = UvEntryPointResolver.PythonFor(entry.ToolFolder),
            Args = ["-c", UvEntryPointResolver.Code(request.Spec.CommandName, entry.Module, entry.Attr), .. request.ServerArgs],
        };

    // A failed recursive delete has already removed every file it could reach, so a running server is checked first;
    // one that was not given the state folder cannot be using the tool.
    private void RefuseWhileAContainerRuns(string containerName)
    {
        if (!SandboxLaunchSpecBuilder.ContainerNames(SandboxLaunchSpecBuilder.PrefixOf(containerName))
                .Any(name => IsOtherRunning(name, containerName) && store.StateGranted(name)))
            return;

        logger.LogInformation("uv tool for {Container} not replaced: a running container of this server was given the state folder", containerName);
        throw new SandboxStartException(InUseMessage);
    }

    // The preparing start holds its own name's lease, so only the server's other container can be running.
    private static bool IsOtherRunning(string name, string containerName) =>
        !string.Equals(name, containerName, StringComparison.OrdinalIgnoreCase) && SandboxContainerLease.IsActive(name);

    private void RemoveInstalledTool(string containerName, string state)
    {
        try
        {
            StateFolderSafety.DeleteTree(Path.Combine(state, "tools"));
            StateFolderSafety.DeleteTree(Path.Combine(state, "tool-bin"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFailure(containerName, ex);
            throw new SandboxStartException(InUseMessage);
        }
    }

    private void PruneCaches(string containerName, IReadOnlyList<string> cacheDirectories)
    {
        foreach (var cache in cacheDirectories)
        {
            try
            {
                StateFolderSafety.DeleteTree(cache);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("uv cache for {Container} not pruned: {Error}", containerName, ex.GetType().Name);
                logger.SensitiveDebug("uv cache {Path} not pruned: {Message}", cache, ex.Message);
            }
        }
    }

    private static string RootOf(string path, string? state, string? support)
    {
        var full = SandboxLaunchSpecBuilder.Normalize(path);
        if (state is not null && SandboxLaunchSpecBuilder.IsSameOrUnder(full, state))
            return state;
        if (support is not null && SandboxLaunchSpecBuilder.IsSameOrUnder(full, support))
            return support;

        throw new InvalidOperationException("A prepared path lies outside the runtime state and support folders.");
    }

    private static void WriteStateFile(string state, PreparedFile file)
    {
        StateFolderSafety.EnsureNoReparsePoints(state, file.Path);
        var bytes = Utf8NoBom.GetBytes(file.Content);
        if (IsCurrent(file.Path, bytes))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        File.WriteAllBytes(file.Path, bytes);
    }

    private static void WriteSupportFile(PreparedFile file)
    {
        var bytes = Utf8NoBom.GetBytes(file.Content);
        if (IsCurrent(file.Path, bytes))
            return;

        var directory = Path.GetDirectoryName(file.Path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(file.Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file.Path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static bool IsCurrent(string path, byte[] bytes) =>
        File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    // A start without a state folder grants none, so it keeps an earlier read-write grant on record until the next clear.
    private void WriteGrantRecord(SandboxLaunchSpec spec)
    {
        try
        {
            store.WriteGrantRecord(
                spec.ContainerName,
                RuntimeRoots(spec),
                spec.RuntimeStateDirectory is null ? store.StateGrantedReadOnly(spec.ContainerName) != false : spec.RuntimeStateReadOnly,
                [.. spec.ReadwritePaths.Concat(spec.ReadonlyPaths).Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
        // Without a state folder the record only widens later sweeps, so losing it must not stop the start.
        catch (Exception ex) when (spec.RuntimeStateDirectory is null && ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Grant record for {Container} not written: {Error}", spec.ContainerName, ex.GetType().Name);
            logger.SensitiveDebug("Grant record for {Container} not written: {Message}", spec.ContainerName, ex.Message);
        }
    }

    private static List<string> RuntimeRoots(SandboxLaunchSpec spec) =>
        [.. spec.Toolchain
            .Append(spec.GrantedCommandDirectory)
            .Append(spec.RuntimeStateDirectory)
            .Append(spec.SupportDirectory)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static UvSpecRecord? ReadSpecRecord(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UvSpecRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LogFailure(string containerName, Exception ex)
    {
        logger.LogWarning("Runtime preparation for {Container} failed: {Error}", containerName, ex.GetType().Name);
        logger.SensitiveDebug("Runtime preparation for {Container} failed: {Message}", containerName, ex.Message);
    }

    private sealed record UvSpecRecord(string? Package, IReadOnlyList<string>? With, string? Python)
    {
        public bool SameAs(UvSpecRecord other) =>
            Package == other.Package
            && Python == other.Python
            && (With ?? []).SequenceEqual(other.With ?? [], StringComparer.Ordinal);
    }
}
