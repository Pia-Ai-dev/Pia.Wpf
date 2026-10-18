using System.Collections.Concurrent;
using System.IO;
using Pia.Native;

namespace Pia.Services.Plugins.Sandbox;

/// <param name="Reason">Written for the user; carries counts, never paths.</param>
public sealed record SandboxGrantResult(bool Ok, int BlockedCount, string? Reason);

/// <summary>Whether the sandbox can be given access to what a server needs.</summary>
public interface ISandboxGrantVerifier
{
    SandboxGrantResult Verify(SandboxLaunchSpec spec);
}

/// <summary>The sandbox grants access by rewriting ACLs and silently skips what it cannot rewrite, so the server
/// would start and then fail on load. Probing WRITE_DAC up front turns that into a clear refusal.</summary>
public sealed class SandboxGrantVerifier : ISandboxGrantVerifier
{
    /// <summary>Past this many entries the rest of a tree is trusted; a missed file only means less access.</summary>
    public const int MaxEntries = 5000;

    /// <summary>A runtime's tree is walked in full, up to 2 s cold, so a clean result is trusted this long.</summary>
    internal static readonly TimeSpan CleanToolchainLifetime = TimeSpan.FromMinutes(10);

    private readonly Func<string, bool> _canWriteDacl;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, DateTime> _cleanToolchainRoots = new(StringComparer.OrdinalIgnoreCase);

    public SandboxGrantVerifier() : this(FileSecurityProbe.CanWriteDacl)
    {
    }

    internal SandboxGrantVerifier(Func<string, bool> canWriteDacl, Func<DateTime>? clock = null)
    {
        _canWriteDacl = canWriteDacl;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public SandboxGrantResult Verify(SandboxLaunchSpec spec)
    {
        var commandBlocked = spec.GrantedCommandDirectory is { } directory ? CountBlockedInTree(directory, MaxEntries) : 0;
        var toolchainBlocked = spec.Toolchain.Sum(CountBlockedInToolchain);
        var policyBlocked = spec.ReadwritePaths.Concat(spec.ReadonlyPaths).Sum(CountBlockedShallow);
        var ownBlocked = new[] { spec.RuntimeStateDirectory, spec.SupportDirectory }.OfType<string>().Sum(CountBlockedShallow);
        if (commandBlocked == 0 && toolchainBlocked == 0 && policyBlocked == 0 && ownBlocked == 0)
            return new SandboxGrantResult(true, 0, null);

        var clauses = new List<string>();
        var advice = new List<string>();
        if (commandBlocked > 0)
        {
            clauses.Add($"{commandBlocked} file(s) in its folder belong to an administrator or another account");
            advice.Add("Install or copy the server into a folder you own.");
        }
        if (toolchainBlocked > 0)
        {
            clauses.Add($"{toolchainBlocked} file(s) in the runtime folder belong to an administrator or another account");
            advice.Add("Install the runtime for your user only; the server's settings show how.");
        }
        if (policyBlocked > 0)
        {
            clauses.Add($"{policyBlocked} item(s) in the folders you chose belong to an administrator or another account");
            advice.Add("Choose folders you own.");
        }
        if (ownBlocked > 0)
        {
            clauses.Add($"{ownBlocked} file(s) in Pia's own folders for this server can't be shared");
            advice.Add("Remove the server and add it again; Pia then starts with fresh folders.");
        }

        return new SandboxGrantResult(false, commandBlocked + toolchainBlocked + policyBlocked + ownBlocked,
            $"Pia can't sandbox this server: {string.Join("; ", clauses)}. {string.Join(" ", advice)}");
    }

    private int CountBlockedInToolchain(string root)
    {
        var key = SandboxLaunchSpecBuilder.Normalize(root);
        var now = _clock();
        if (_cleanToolchainRoots.TryGetValue(key, out var checkedAt) && now - checkedAt < CleanToolchainLifetime)
            return 0;

        // Not remembered when missing: it could appear holding another account's files before the lifetime ends.
        if (!Directory.Exists(key))
            return 0;

        var blocked = CountBlockedInTree(key, int.MaxValue);
        if (blocked == 0)
            _cleanToolchainRoots[key] = now;
        return blocked;
    }

    private int CountBlockedInTree(string root, int limit)
    {
        if (!Directory.Exists(root))
            return 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return CountBlocked(Directory.EnumerateFileSystemEntries(root, "*", options).Prepend(root), limit);
    }

    private int CountBlockedShallow(string root)
    {
        if (File.Exists(root))
            return CountBlocked([root], MaxEntries);
        if (!Directory.Exists(root))
            return 0;

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return CountBlocked(Directory.EnumerateFiles(root, "*", options).Prepend(root), MaxEntries);
    }

    private int CountBlocked(IEnumerable<string> paths, int limit) =>
        paths.Take(limit).Count(path => !_canWriteDacl(path));
}
