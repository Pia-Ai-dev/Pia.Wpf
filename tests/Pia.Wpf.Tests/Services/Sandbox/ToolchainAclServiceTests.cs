using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class ToolchainAclServiceTests : IDisposable
{
    private static readonly SecurityIdentifier AllAppPackages = new("S-1-15-2-1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-toolchain-acl-" + Guid.NewGuid().ToString("N"));
    private readonly ToolchainAclService _service = new(NullLogger<ToolchainAclService>.Instance);

    public ToolchainAclServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            var directory = new DirectoryInfo(_root);
            var security = directory.GetAccessControl();
            security.PurgeAccessRules(AllAppPackages);
            directory.SetAccessControl(security);
        }

        TempPath.Remove(_root);
    }

    [Fact]
    public void Grant_AddsAnEntryTheFolderReportsAfterwards()
    {
        Assert.False(_service.HasAllAppPackagesAce(_root), "the temp folder must not carry an all-app-packages entry already");

        _service.GrantAllAppPackagesRead(_root);

        Assert.True(_service.HasAllAppPackagesAce(_root));
        var rule = Assert.Single(RulesFor(new DirectoryInfo(_root).GetAccessControl()));
        Assert.False(rule.IsInherited);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        Assert.Equal(FileSystemRights.ReadAndExecute, rule.FileSystemRights & FileSystemRights.ReadAndExecute);
    }

    [Fact]
    public void Grant_ReachesExistingFilesAndFilesCreatedAfterwards()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "node_modules", "npm")).FullName;
        var existing = Path.Combine(nested, "package.json");
        File.WriteAllText(existing, "{}");

        _service.GrantAllAppPackagesRead(_root);

        var created = Path.Combine(_root, "node.exe");
        File.WriteAllText(created, "x");
        AssertInheritsRead(new FileInfo(existing).GetAccessControl());
        AssertInheritsRead(new FileInfo(created).GetAccessControl());
        AssertInheritsRead(new DirectoryInfo(nested).GetAccessControl());
        Assert.True(_service.HasAllAppPackagesAce(nested));
    }

    [Fact]
    public void HasAllAppPackagesAce_SeesADenyEntryToo()
    {
        var directory = new DirectoryInfo(_root);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(AllAppPackages, FileSystemRights.Write, AccessControlType.Deny));
        directory.SetAccessControl(security);

        Assert.True(_service.HasAllAppPackagesAce(_root));
    }

    [Fact]
    public void Grant_OnAMissingFolder_Throws()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.ThrowsAny<IOException>(() => _service.GrantAllAppPackagesRead(missing));
    }

    private static void AssertInheritsRead(FileSystemSecurity security)
    {
        var rule = Assert.Single(RulesFor(security));
        Assert.True(rule.IsInherited);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.ReadAndExecute, rule.FileSystemRights & FileSystemRights.ReadAndExecute);
    }

    private static List<FileSystemAccessRule> RulesFor(FileSystemSecurity security) =>
        [.. security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => AllAppPackages.Equals(rule.IdentityReference))];
}
