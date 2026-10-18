using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

public sealed class ToolchainAclService(ILogger<ToolchainAclService> logger) : IToolchainAclService
{
    private static readonly SecurityIdentifier AllAppPackages = new("S-1-15-2-1");

    public bool HasAllAppPackagesAce(string root) =>
        new DirectoryInfo(root).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => AllAppPackages.Equals(rule.IdentityReference));

    // Windows propagates the inheritable entry to the whole tree inside SetAccessControl.
    public void GrantAllAppPackagesRead(string root)
    {
        var stopwatch = Stopwatch.StartNew();
        var directory = new DirectoryInfo(root);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            AllAppPackages,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        logger.LogInformation("Granted all-app-packages read on a runtime folder in {Ms} ms", stopwatch.ElapsedMilliseconds);
        logger.SensitiveDebug("All-app-packages read granted on {Folder}", root);
    }
}
