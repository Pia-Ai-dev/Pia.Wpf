using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Native;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>What a real container does with an empty environment entry and with a link planted in its folder.</summary>
[Collection("MxcRuntimeEnvironment")]
public sealed class MxcSandboxGateTests : IAsyncLifetime
{
    private const string SkipReason = "MXC cannot create a container on this host (see MxcTestHost.Reason).";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "pia-gate-outside-" + Guid.NewGuid().ToString("N"));
    private readonly string _revoked = Path.Combine(Path.GetTempPath(), "pia-gate-revoked-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefix = "gate-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<McpClient> _clients = [];

    private string Workspace => Path.Combine(_root, "workspace");

    private string Junction => Path.Combine(Workspace, "j");

    private string Symlink => Path.Combine(Workspace, "s");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(_revoked);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
        File.WriteAllText(Path.Combine(_revoked, "kept.txt"), "revoked");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();

        // A recursive delete over a junction fails, so the links go first and alone.
        RemoveLink(Junction);
        RemoveLink(Symlink);

        new AppContainerProfileService(NullLogger<AppContainerProfileService>.Instance).DeleteServerProfiles(_prefix);
        TempPath.Remove(_root);
        TempPath.Remove(_outside);
        TempPath.Remove(_revoked);
    }

    private static McpTransportFactory Factory() =>
        new(new MxcMcpLauncher(NullLogger<MxcMcpLauncher>.Instance, MxcTestHost.Runtime), new SandboxGrantVerifier(), NullLoggerFactory.Instance);

    private Task<McpClient> StartAsync(
        IReadOnlyDictionary<string, string>? env = null,
        bool probe = false,
        IReadOnlyList<string>? readwrite = null) =>
        ConnectAsync(new McpTransportRequest("gate", TestMcpServerFixture.ExePath, [], env, Workspace,
            new McpSandboxPolicy(readwrite ?? [Workspace], [], AllowNetwork: false), _prefix, probe));

    private async Task<McpClient> ConnectAsync(McpTransportRequest request)
    {
        var client = await McpClient.CreateAsync(Factory().Create(request), cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    private async Task StopAsync(McpClient client)
    {
        await client.DisposeAsync();
        _clients.Remove(client);
    }

    private static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        return (result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
    }

    private static Task<(bool IsError, string Text)> CreateLinkAsync(McpClient client, string path, string target, string kind) =>
        CallAsync(client, "create_link", new() { ["path"] = path, ["target"] = target, ["kind"] = kind });

    private static async Task<Dictionary<string, string>> EnvironmentAsync(McpClient client, params string[] names)
    {
        var (isError, text) = await CallAsync(client, "get_environment", new() { ["names"] = string.Join(",", names) });
        Assert.False(isError, text);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(": ", 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> InAppContainerAsync(McpClient client)
    {
        var (_, text) = await CallAsync(client, "get_server_info", []);
        return JsonDocument.Parse(text).RootElement.GetProperty("inAppContainer").GetBoolean();
    }

    private static List<string> Sids(string path)
    {
        FileSystemSecurity acl = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return [.. acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value)];
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void RemoveLink(string link)
    {
        if (!IsLink(link))
            return;

        if (File.GetAttributes(link).HasFlag(FileAttributes.Directory))
            Directory.Delete(link, recursive: false);
        else
            File.Delete(link);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task WithoutEnvEntries_TheRegistryBlockArrives_AndASessionOnlyVariableDoesNot()
    {
        var sessionOnly = "PIA_TEST_SESSION_ONLY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sessionOnly, "session-only");
        try
        {
            var client = await StartAsync();

            var env = await EnvironmentAsync(client, "OS", "ComSpec", sessionOnly);

            Assert.StartsWith("set", env["OS"]);
            Assert.StartsWith("set", env["ComSpec"]);
            Assert.Equal("absent", env[sessionOnly]);
            Assert.True(await InAppContainerAsync(client));
        }
        finally
        {
            Environment.SetEnvironmentVariable(sessionOnly, null);
        }
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task AnEmptyOverride_BlanksARegistryVariable_AndTheRestOfTheBlockArrives()
    {
        var client = await StartAsync(new Dictionary<string, string> { ["OS"] = "" });

        var env = await EnvironmentAsync(client, "OS", "ComSpec");

        Assert.Equal("empty", env["OS"]);
        Assert.StartsWith("set", env["ComSpec"]);
        Assert.True(await InAppContainerAsync(client));
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task ALinkToAnUngrantedFolder_CannotBeCreatedFromInsideTheContainer()
    {
        var client = await StartAsync();

        var junction = await CreateLinkAsync(client, Junction, _outside, "junction");
        var symlink = await CreateLinkAsync(client, Symlink, _outside, "symlink");

        Assert.True(junction.IsError, junction.Text);
        Assert.Contains("FSCTL_SET_REPARSE_POINT failed with Win32 error 5", junction.Text);
        Assert.True(symlink.IsError, symlink.Text);
        Assert.Contains("Win32 error 1314", symlink.Text);
        Assert.False(Path.Exists(Junction));
        Assert.False(Path.Exists(Symlink));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(_outside, "secret.txt")));
    }

    /// <summary>Proves the refusal above is the container's: the same junction, uncontained, is created.</summary>
    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task TheSameJunctionUncontained_IsCreated()
    {
        var client = await ConnectAsync(new McpTransportRequest("gate", TestMcpServerFixture.ExePath, [], null, Workspace));

        var junction = await CreateLinkAsync(client, Junction, _outside, "junction");

        Assert.False(junction.IsError, junction.Text);
        Assert.True(IsLink(Junction));
        Assert.Equal(_outside, new DirectoryInfo(Junction).LinkTarget);
    }

    [Fact(SkipUnless = nameof(MxcTestHost.CanSpawn), SkipType = typeof(MxcTestHost), Skip = SkipReason)]
    public async Task AJunctionToARevokedGrant_IsCreated_ButTheNextSpawnNeitherFollowsNorOpensIt()
    {
        var kept = Path.Combine(_revoked, "kept.txt");
        var first = await StartAsync(readwrite: [Workspace, _revoked]);
        var created = await CreateLinkAsync(first, Junction, _revoked, "junction");
        await StopAsync(first);
        Assert.False(created.IsError, created.Text);
        Assert.True(IsLink(Junction));

        var second = await StartAsync(probe: true);
        var secondSid = AppContainerNative.DeriveSid(SandboxLaunchSpecBuilder.ContainerName(_prefix, isProbe: true))!;
        var onTarget = Sids(_revoked).Concat(Sids(kept)).ToList();
        var read = await CallAsync(second, "read_file", new() { ["path"] = Path.Combine(Junction, "kept.txt") });
        var write = await CallAsync(second, "write_file", new() { ["path"] = Path.Combine(Junction, "planted.txt"), ["content"] = "x" });

        Assert.DoesNotContain(secondSid, onTarget);
        Assert.True(read.IsError, read.Text);
        Assert.True(write.IsError, write.Text);
        Assert.False(File.Exists(Path.Combine(_revoked, "planted.txt")));
    }
}
