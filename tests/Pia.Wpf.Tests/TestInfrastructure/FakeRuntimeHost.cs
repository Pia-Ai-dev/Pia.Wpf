using System.IO;
using System.Text.RegularExpressions;
using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Tests.TestInfrastructure;

/// <summary>An in-memory machine for runtime detection; frozen, so add extra helpers in your own test file.</summary>
public sealed class FakeRuntimeHost : IRuntimeHostService
{
    public List<string> PathEntries { get; } = [];

    public Dictionary<string, string> RegistryEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> UserNames { get; } = [];

    /// <summary>Thrown by <see cref="UserEnvironmentNames"/> instead of returning the names.</summary>
    public Exception? UserNamesFailure { get; set; }

    public List<string>? Override { get; set; }

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Readable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DaclWritable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A path prefix and the link target it stands for.</summary>
    public Dictionary<string, string> FinalPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string UserProfile { get; set; } = Path.Combine(@"C:\", "Users", "u");

    public string LocalAppData { get; set; } = Path.Combine(@"C:\", "Users", "u", "AppData", "Local");

    public string RoamingAppData { get; set; } = Path.Combine(@"C:\", "Users", "u", "AppData", "Roaming");

    public string WindowsDirectory { get; set; } = Path.Combine(@"C:\", "Windows");

    public IReadOnlyList<string> ProgramFilesDirectories { get; set; } =
        [Path.Combine(@"C:\", "Program Files"), Path.Combine(@"C:\", "Program Files (x86)")];

    public IReadOnlyList<string>? CandidateOverride => Override;

    public IReadOnlyList<string> RegistryPathEntries() => [.. PathEntries];

    public string RegistryPath() => string.Join(';', PathEntries);

    public IReadOnlyList<string> UserEnvironmentNames() => UserNamesFailure is { } failure ? throw failure : [.. UserNames];

    public string? RegistryEnvironmentValue(string name) => RegistryEnvironment.GetValueOrDefault(name);

    public bool FileExists(string path) => Files.Contains(path);

    public bool DirectoryExists(string path) => Directories.Contains(path);

    public IReadOnlyList<string> EnumerateDirectories(string path, string searchPattern)
    {
        var pattern = new Regex(
            "^" + Regex.Escape(searchPattern).Replace(@"\*", ".*") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var parent = path.TrimEnd('\\', '/');

        return [.. Directories
            .Where(d => string.Equals(Path.GetDirectoryName(d)?.TrimEnd('\\', '/'), parent, StringComparison.OrdinalIgnoreCase))
            .Where(d => pattern.IsMatch(Path.GetFileName(d)))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public string? ReadAllText(string path) => Texts.GetValueOrDefault(path);

    public string? ResolveFinalPath(string path)
    {
        var link = FinalPaths.Keys
            .Where(prefix => IsSameOrUnder(path, prefix))
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault();
        var suffix = link is null ? "" : path[link.TrimEnd('\\').Length..];
        var resolved = link is null ? path
            : suffix.Length == 0 ? FinalPaths[link]
            : FinalPaths[link].TrimEnd('\\') + suffix;

        return Files.Contains(resolved) || Directories.Contains(resolved) ? resolved : null;
    }

    public string? FileVersion(string path) => Versions.GetValueOrDefault(path);

    public bool IsContainerReadable(string path) => Readable.Contains(path);

    public bool CanUserWriteDacl(string path) => DaclWritable.Contains(path);

    public FakeRuntimeHost WithNode(
        string root,
        string version = "22.23.2",
        string npmVersion = "10.9.8",
        bool readable = false,
        bool grantable = true,
        bool withNpm = true)
    {
        var node = Path.Combine(root, "node.exe");
        WithFile(node);
        Versions[node] = version;

        var scripts = new List<string>();
        if (withNpm)
        {
            var npm = Path.Combine(root, "node_modules", "npm");
            WithFile(Path.Combine(npm, "package.json"), $$"""{"name": "npm", "version": "{{npmVersion}}"}""");
            scripts.Add(Path.Combine(npm, "bin", "npx-cli.js"));
            scripts.Add(Path.Combine(npm, "bin", "npm-cli.js"));
            scripts.ForEach(script => WithFile(script));
        }

        if (readable)
            Readable.UnionWith([root, node, .. scripts]);
        if (grantable)
            DaclWritable.Add(root);
        return this;
    }

    public FakeRuntimeHost WithDotnet(string root, string[] sdks, string[] runtimes, bool readable = false, bool grantable = true)
    {
        var dotnet = Path.Combine(root, "dotnet.exe");
        WithFile(dotnet);

        var entryScripts = sdks.Select(sdk => Path.Combine(root, "sdk", sdk, "dotnet.dll")).ToList();
        entryScripts.ForEach(script => WithFile(script));
        foreach (var runtime in runtimes)
            WithDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", runtime));

        if (readable)
            Readable.UnionWith([root, dotnet, .. entryScripts]);
        if (grantable)
            DaclWritable.Add(root);
        return this;
    }

    public FakeRuntimeHost WithUv(string folder, string version = "0.13.1")
    {
        var uv = Path.Combine(folder, "uv.exe");
        WithFile(uv);
        Versions[uv] = version;
        return this;
    }

    public FakeRuntimeHost WithFile(string path, string? text = null)
    {
        Files.Add(path);
        if (text is not null)
            Texts[path] = text;
        AddAncestors(path);
        return this;
    }

    public FakeRuntimeHost WithDirectory(string path)
    {
        Directories.Add(path);
        AddAncestors(path);
        return this;
    }

    private void AddAncestors(string path)
    {
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            Directories.Add(parent);
    }

    private static bool IsSameOrUnder(string path, string prefix) =>
        string.Equals(path, prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
