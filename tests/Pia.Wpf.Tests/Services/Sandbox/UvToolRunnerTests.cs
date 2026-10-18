using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class UvToolRunnerTests
{
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["UV_TOOL_DIR"] = @"C:\state\tools",
        ["UV_NO_BUILD"] = "1",
        ["TEMP"] = @"C:\state\tmp",
    };

    private static UvPrepareRequest Request(UvxSpec spec) =>
        new("pia-mcp-time", @"C:\Users\Jane\.local\bin\uv.exe", spec, Environment, Offline: false, @"C:\state", ["--local-timezone", "UTC"],
            [@"C:\state\uv-cache", @"C:\state\python-cache"]);

    private sealed class Seam(int exitCode, params string[] lines)
    {
        public ProcessStartInfo? Info { get; private set; }

        public Task<int> Run(ProcessStartInfo info, Action<string> outputLine, CancellationToken cancellationToken)
        {
            Info = info;
            foreach (var line in lines)
                outputLine(line);
            return Task.FromResult(exitCode);
        }
    }

    [Fact]
    public async Task TheArgumentList_IsToolInstall_ThePackage_EachWith_AndThePython()
    {
        var seam = new Seam(0);
        var runner = new UvToolRunner(NullLogger<UvToolRunner>.Instance, seam.Run);

        await runner.InstallAsync(Request(new UvxSpec("mcp-server-time==2026.10.10", "mcp-server-time", ["tzdata", "httpx>=0.28"], "3.13")),
            _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\Users\Jane\.local\bin\uv.exe", seam.Info!.FileName);
        Assert.Equal(
            ["tool", "install", "mcp-server-time==2026.10.10", "--with", "tzdata", "--with", "httpx>=0.28", "--python", "3.13"],
            seam.Info.ArgumentList);
        Assert.Equal("", seam.Info.Arguments);
        Assert.False(seam.Info.UseShellExecute);
        Assert.True(seam.Info.CreateNoWindow);
        Assert.True(seam.Info.RedirectStandardOutput);
        Assert.True(seam.Info.RedirectStandardError);
        Assert.Equal(@"C:\state", seam.Info.WorkingDirectory);
    }

    [Fact]
    public async Task WithoutWithOrPython_OnlyThePackageFollows()
    {
        var seam = new Seam(0);

        await new UvToolRunner(NullLogger<UvToolRunner>.Instance, seam.Run)
            .InstallAsync(Request(new UvxSpec("mcp-server-time", "mcp-server-time", [], null)), _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(["tool", "install", "mcp-server-time"], seam.Info!.ArgumentList);
    }

    [Fact]
    public async Task TheEnvironment_IsExactlyTheRequests()
    {
        var seam = new Seam(0);

        await new UvToolRunner(NullLogger<UvToolRunner>.Instance, seam.Run)
            .InstallAsync(Request(new UvxSpec("x", "x", [], null)), _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(
            Environment.OrderBy(e => e.Key, StringComparer.Ordinal),
            seam.Info!.Environment.Select(e => KeyValuePair.Create(e.Key, e.Value!)).OrderBy(e => e.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OutputLines_AreForwarded_AndTheExitCodeReturned()
    {
        var seam = new Seam(2, "Resolved 33 packages in 621ms", "error: no wheel for sdist-only");
        var lines = new List<string>();

        var code = await new UvToolRunner(NullLogger<UvToolRunner>.Instance, seam.Run)
            .InstallAsync(Request(new UvxSpec("x", "x", [], null)), lines.Add, TestContext.Current.CancellationToken);

        Assert.Equal(2, code);
        Assert.Equal(["Resolved 33 packages in 621ms", "error: no wheel for sdist-only"], lines);
    }

    [Fact]
    public async Task TheRealProcess_GetsOnlyTheGivenEnvironment_ForwardsBothStreams_AndReturnsTheExitCode()
    {
        var info = new ProcessStartInfo(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/d", "/c", "echo out %PIA_UV_PROBE%-%USERNAME%& echo err 1>&2& exit 3" })
            info.ArgumentList.Add(arg);
        info.Environment.Clear();
        info.Environment["PIA_UV_PROBE"] = "given";
        var lines = new List<string>();

        var code = await UvToolRunner.RunProcessAsync(info, line => { lock (lines) lines.Add(line); }, TestContext.Current.CancellationToken);

        Assert.Equal(3, code);
        Assert.Contains("out given-%USERNAME%", lines);
        Assert.Contains(lines, line => line.TrimEnd() == "err");
    }

    [Fact]
    public async Task AMissingExecutable_ThrowsWin32Exception()
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "uv.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        await Assert.ThrowsAsync<Win32Exception>(() => UvToolRunner.RunProcessAsync(info, _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_KillsTheProcess()
    {
        var info = new ProcessStartInfo(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/d", "/c", "ping -n 30 127.0.0.1 >nul" })
            info.ArgumentList.Add(arg);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UvToolRunner.RunProcessAsync(info, _ => { }, cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }
}
