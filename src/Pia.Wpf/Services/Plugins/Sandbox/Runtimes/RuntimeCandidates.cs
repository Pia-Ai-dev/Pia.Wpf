using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>The user-scope install locations probed after the registry PATH; Pia's own PATH is stale after a guide install.</summary>
public static partial class RuntimeCandidates
{
    [GeneratedRegex(@"^node-v(?<version>[0-9]+\.[0-9]+\.[0-9]+)(-|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WinGetNodeFolder();

    public static IEnumerable<string> For(RuntimeFamily family, IRuntimeHostService host) => family switch
    {
        RuntimeFamily.Node => Node(host),
        RuntimeFamily.Dotnet => Dotnet(host),
        RuntimeFamily.Uv => Uv(host),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
    };

    public static IEnumerable<string> Node(IRuntimeHostService host)
    {
        yield return Path.Combine(host.LocalAppData, "Programs", "nodejs");
        if (NewestWinGetNode(host) is { } winGet)
            yield return winGet;
        if (Variable(host, "NVM_SYMLINK") is { } nvm)
            yield return nvm;
        yield return Path.Combine(Variable(host, "FNM_DIR") ?? Path.Combine(host.RoamingAppData, "fnm"), "aliases", "default");
        if (VoltaNode(host) is { } volta)
            yield return volta;

        var scoop = ScoopRoot(host);
        yield return Path.Combine(scoop, "apps", "nodejs-lts", "current");
        yield return Path.Combine(scoop, "apps", "nodejs", "current");
    }

    public static IEnumerable<string> Dotnet(IRuntimeHostService host)
    {
        if (Variable(host, "DOTNET_ROOT") is { } dotnetRoot)
            yield return dotnetRoot;
        yield return Path.Combine(host.LocalAppData, "Microsoft", "dotnet");
        if (host.ProgramFilesDirectories.FirstOrDefault() is { } programFiles)
            yield return Path.Combine(programFiles, "dotnet");
    }

    public static IEnumerable<string> Uv(IRuntimeHostService host)
    {
        yield return Path.Combine(host.UserProfile, ".local", "bin");
        yield return Path.Combine(host.UserProfile, ".cargo", "bin");
        yield return Path.Combine(host.LocalAppData, "Microsoft", "WinGet", "Links");
        if (ScoopShimFolder(host, "uv.shim") is { } scoopUv)
            yield return scoopUv;
    }

    /// <summary>Every fixed uv folder and registry PATH folder, normalized, whether or not it holds a program yet, and
    /// where a uv.exe link in one leads; <paramref name="mayProbe"/> keeps the file system out of slow folders.</summary>
    public static IReadOnlyList<string> HostProgramFolders(IRuntimeHostService host, Func<string, bool> mayProbe)
    {
        var folders = new List<string>();
        foreach (var entry in Uv(host).Concat(host.RegistryPathEntries()))
        {
            if (NormalizedFolder(entry.Trim().Trim('"')) is not { } folder)
                continue;

            folders.Add(folder);
            var uv = Path.Combine(folder, "uv.exe");
            if (mayProbe(folder)
                && host.FileExists(uv)
                && host.ResolveFinalPath(uv) is { } target
                && NormalizedFolder(Path.GetDirectoryName(target) ?? string.Empty) is { } targetFolder)
                folders.Add(targetFolder);
        }

        return [.. folders.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // PATH may hold unexpanded or malformed entries; one of them must not fail the whole guard.
    private static string? NormalizedFolder(string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            return null;

        try
        {
            return SandboxLaunchSpecBuilder.Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NewestWinGetNode(IRuntimeHostService host)
    {
        var packages = Path.Combine(host.LocalAppData, "Microsoft", "WinGet", "Packages");
        if (!host.DirectoryExists(packages))
            return null;

        return host.EnumerateDirectories(packages, "OpenJS.NodeJS*")
            .SelectMany(package => host.EnumerateDirectories(package, "node-v*"))
            .Select(folder => (Folder: folder, Version: WinGetNodeFolder().Match(Path.GetFileName(folder)) is { Success: true } match
                ? match.Groups["version"].Value
                : null))
            .OrderByDescending(entry => entry.Version, SdkVersion.Comparer)
            .Select(entry => entry.Folder)
            .FirstOrDefault();
    }

    private static string? VoltaNode(IRuntimeHostService host)
    {
        var volta = Variable(host, "VOLTA_HOME") ?? Path.Combine(host.LocalAppData, "Volta");
        var text = host.ReadAllText(Path.Combine(volta, "tools", "user", "platform.json"));
        if (text is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("node", out var node)
                && node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("runtime", out var runtime)
                && runtime.ValueKind == JsonValueKind.String
                && runtime.GetString() is { } version
                && IsPlainName(version)
                ? Path.Combine(volta, "tools", "image", "node", version)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ScoopShimFolder(IRuntimeHostService host, string shimName)
    {
        var text = host.ReadAllText(Path.Combine(ScoopRoot(host), "shims", shimName));
        if (text is null)
            return null;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=');
            if (equals < 0 || !line[..equals].Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = line[(equals + 1)..].Trim().Trim('"');
            return Path.IsPathFullyQualified(target) ? Path.GetDirectoryName(target) : null;
        }

        return null;
    }

    private static string ScoopRoot(IRuntimeHostService host) =>
        Variable(host, "SCOOP") ?? Path.Combine(host.UserProfile, "scoop");

    private static string? Variable(IRuntimeHostService host, string name) =>
        host.RegistryEnvironmentValue(name)?.Trim().Trim('"') is { Length: > 0 } value && Path.IsPathFullyQualified(value)
            ? value
            : null;

    private static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
