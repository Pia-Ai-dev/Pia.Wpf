using System;
using System.Diagnostics;
using System.IO;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public sealed class SafeFolderPathTests : IDisposable
{
    private readonly string _temp;
    private readonly string _base;

    public SafeFolderPathTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "pia-sfp-" + Guid.NewGuid().ToString("N"));
        _base = Path.Combine(_temp, "sandbox");
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        TempPath.Remove(_temp);
    }

    [Fact]
    public void InBaseAbsolute_Resolves()
    {
        var target = Path.Combine(_base, "notes", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "hi");

        var ok = SafeFolderPath.TryResolveInsideAllowingAbsolute(_base, target, out var resolved);

        Assert.True(ok);
        Assert.StartsWith(_base, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutOfBaseAbsolute_Rejected()
    {
        var outside = Path.Combine(_temp, "outside", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "secret");

        var ok = SafeFolderPath.TryResolveInsideAllowingAbsolute(_base, outside, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void DotDot_EscapingBase_Rejected()
    {
        // base\..\outside\secret.txt -> escapes the sandbox lexically.
        var escaping = Path.Combine(_base, "..", "outside", "secret.txt");

        var ok = SafeFolderPath.TryResolveInsideAllowingAbsolute(_base, escaping, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void NonExistentLeaf_UnderRealDir_Resolves()
    {
        // Parent exists, leaf does not (the write_file create-new case).
        var dir = Path.Combine(_base, "logs");
        Directory.CreateDirectory(dir);
        var newFile = Path.Combine(dir, "does-not-exist-yet.txt");
        Assert.False(File.Exists(newFile));

        var ok = SafeFolderPath.TryResolveInsideAllowingAbsolute(_base, newFile, out var resolved);

        Assert.True(ok);
        Assert.EndsWith("does-not-exist-yet.txt", resolved);
        Assert.StartsWith(_base, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InBaseJunction_PointingOutside_RejectedAfterCanonicalization()
    {
        // Layout (siblings, so the target is genuinely outside the sandbox):
        //   <temp>\sandbox          (base)
        //   <temp>\escape-target    (outside, contains secret.txt)
        //   <temp>\sandbox\link  -> junction to <temp>\escape-target
        var outsideTarget = Path.Combine(_temp, "escape-target");
        Directory.CreateDirectory(outsideTarget);
        File.WriteAllText(Path.Combine(outsideTarget, "secret.txt"), "secret");

        var junction = Path.Combine(_base, "link");
        var rc = RunMklinkJunction(junction, outsideTarget);
        // The junction case is the security-critical one: a silent skip would be a false pass.
        Assert.True(rc == 0 && Directory.Exists(junction),
            $"mklink /J failed (exit {rc}); cannot validate junction canonicalization.");

        // Lexically this path is "inside" base, but it canonicalizes to outside the sandbox.
        var throughJunction = Path.Combine(junction, "secret.txt");
        Assert.True(File.Exists(throughJunction), "junction did not expose the outside file");

        var ok = SafeFolderPath.TryResolveInsideAllowingAbsolute(_base, throughJunction, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    private static int RunMklinkJunction(string link, string target)
    {
        // Junctions (/J) do not require elevation, unlike symbolic links.
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
