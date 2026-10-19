using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using NSubstitute;
using Pia.Native;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RuntimeHostServiceTests : IDisposable
{
    private static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");
    private static readonly SecurityIdentifier AllRestrictedApplicationPackages = new("S-1-15-2-2");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _granted = [];

    public RuntimeHostServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var folder in _granted.Where(Directory.Exists))
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            acl.PurgeAccessRules(AllApplicationPackages);
            acl.PurgeAccessRules(AllRestrictedApplicationPackages);
            info.SetAccessControl(acl);
        }

        TempPath.Remove(_root);
    }

    private static RuntimeHostService Host(bool elevated = false)
    {
        var elevation = Substitute.For<IElevationService>();
        elevation.IsElevated.Returns(elevated);
        return new RuntimeHostService(elevation);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string File(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    private void Grant(
        string folder,
        SecurityIdentifier sid,
        FileSystemRights rights,
        AccessControlType type = AccessControlType.Allow,
        PropagationFlags propagation = PropagationFlags.None)
    {
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagation, type));
        info.SetAccessControl(acl);
        _granted.Add(folder);
    }

    private static int Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    [Fact]
    public void AnInheritableAllowForAllApplicationPackages_MakesTheFolderAndAFileInsideItReadable()
    {
        var folder = Dir("toolchain");
        Grant(folder, AllApplicationPackages, FileSystemRights.ReadAndExecute);
        var file = File(folder, "node.exe");

        Assert.True(Host().IsContainerReadable(folder));
        Assert.True(Host().IsContainerReadable(file));
    }

    [Fact]
    public void AFolderWithoutAnAppContainerAce_IsNotReadable()
    {
        var folder = Dir("toolchain");
        var file = File(folder, "node.exe");

        Assert.False(Host().IsContainerReadable(folder));
        Assert.False(Host().IsContainerReadable(file));
    }

    [Fact]
    public void ADenyForAllApplicationPackages_OutweighsTheAllow()
    {
        var folder = Dir("toolchain");
        Grant(folder, AllApplicationPackages, FileSystemRights.ReadAndExecute);
        Grant(folder, AllApplicationPackages, FileSystemRights.ReadData, AccessControlType.Deny);

        Assert.False(Host().IsContainerReadable(folder));
    }

    [Fact]
    public void AnAllowForAllRestrictedApplicationPackagesAlone_IsReadable()
    {
        var folder = Dir("toolchain");
        Grant(folder, AllRestrictedApplicationPackages, FileSystemRights.ReadAndExecute);

        Assert.True(Host().IsContainerReadable(folder));
    }

    [Fact]
    public void AnInheritOnlyAllow_CoversTheChildrenButNotTheFolderItself()
    {
        var folder = Dir("toolchain");
        Grant(folder, AllApplicationPackages, FileSystemRights.ReadAndExecute, propagation: PropagationFlags.InheritOnly);
        var file = File(folder, "node.exe");

        Assert.False(Host().IsContainerReadable(folder));
        Assert.True(Host().IsContainerReadable(file));
    }

    [Fact]
    public void AnAllowThatOnlyReads_IsNotEnough()
    {
        var folder = Dir("toolchain");
        Grant(folder, AllApplicationPackages, FileSystemRights.Read);

        Assert.False(Host().IsContainerReadable(folder));
    }

    // Windows maps generic bits before it stores an ACE on disk, so these shapes are checked in memory.
    [Theory]
    [InlineData("D:(A;;GRGX;;;AC)", true)]
    [InlineData("D:(A;;GA;;;AC)", true)]
    [InlineData("D:(A;;GR;;;AC)", false)]
    [InlineData("D:(A;OICIIO;GRGX;;;AC)", false)]
    [InlineData("D:(A;;GRGX;;;S-1-15-2-2)", true)]
    [InlineData("D:(A;;GRGX;;;AC)(D;;GW;;;AC)", false)]
    [InlineData("D:(A;;GRGX;;;BU)", false)]
    public void GenericBits_AreMappedBeforeTheReadCheck(string sddl, bool readable)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);

        Assert.Equal(readable, RuntimeHostService.GrantsContainerRead(security));
    }

    [Fact]
    public void AMissingPath_IsNeitherReadableNorDaclWritable()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.False(Host().IsContainerReadable(missing));
        Assert.False(Host().CanUserWriteDacl(missing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFolderTheUserOwns_IsDaclWritable(bool elevated)
    {
        var folder = Dir("owned");

        Assert.True(Host(elevated).CanUserWriteDacl(folder));
        Assert.True(Host(elevated).CanUserWriteDacl(File(folder, "a.txt")));
    }

    [Fact]
    public void AnAdminOwnedFolder_IsNotDaclWritableForTheLinkedUser_ThoughTheElevatedProcessCouldWriteIt()
    {
        if (!IsElevated())
            Assert.Skip("needs an elevated test process to check the linked medium-IL token");

        var folder = Dir("admin-owned");
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!.Value;
        try
        {
            Assert.Equal(0, Run("icacls.exe", folder, "/setowner", "*S-1-5-18"));

            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);

            Assert.False(Host(elevated: true).CanUserWriteDacl(folder));
            Assert.True(FileSecurityProbe.CanWriteDacl(folder));
            // Positive control: the linked token exists and AccessCheck grants where the user really may.
            Assert.True(TokenAccessProbe.CanLinkedTokenWriteDacl(Dir("owned")));
        }
        finally
        {
            Run("icacls.exe", folder, "/setowner", "*" + user);
            Run("icacls.exe", folder, "/reset");
        }
    }

    [Fact]
    public void AJunction_ResolvesToItsTarget()
    {
        var target = Dir("target");
        var file = File(target, "node.exe");
        var link = Path.Combine(_root, "link");
        var exit = Run("cmd.exe", "/c", "mklink", "/J", link, target);
        Assert.True(exit == 0 && Directory.Exists(link), $"mklink /J failed (exit {exit})");

        var host = Host();
        var resolvedTarget = host.ResolveFinalPath(target);

        Assert.NotNull(resolvedTarget);
        Assert.Equal(resolvedTarget, host.ResolveFinalPath(link), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(host.ResolveFinalPath(file), host.ResolveFinalPath(Path.Combine(link, "node.exe")), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\?\", resolvedTarget);
        Assert.EndsWith(Path.Combine(Path.GetFileName(_root), "target"), resolvedTarget, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingPath_ResolvesToNull() =>
        Assert.Null(Host().ResolveFinalPath(Path.Combine(_root, "missing", "node.exe")));

    [Theory]
    [InlineData(@"\\?\C:\Program Files\nodejs", @"C:\Program Files\nodejs")]
    [InlineData(@"\\?\UNC\server\share\tools", @"\\server\share\tools")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void TheExtendedLengthPrefix_IsStripped(string final, string expected) =>
        Assert.Equal(expected, FinalPathProbe.StripExtendedPrefix(final));

    [Fact]
    public void TheRegistryPath_HoldsTheSystemDirectory()
    {
        var host = Host();
        var entries = host.RegistryPathEntries();

        Assert.Contains(entries, entry =>
            string.Equals(entry.TrimEnd('\\'), Environment.SystemDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.Length == 0 || entry.Contains(';'));
        Assert.Equal(string.Join(';', entries), host.RegistryPath());
    }

    [Fact]
    public void ARegistryVariable_IsExpandedAndAnUnknownOneIsNull()
    {
        var host = Host();

        Assert.Equal(Environment.GetEnvironmentVariable("windir"), host.RegistryEnvironmentValue("windir"), StringComparer.OrdinalIgnoreCase);
        Assert.Null(host.RegistryEnvironmentValue("PIA_TEST_UNSET_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void AMachineVariable_IsReadFromHklmOnly_AndExpanded()
    {
        var host = Host();
        using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        Assert.NotNull(machine);

        Assert.Equal((string?)machine.GetValue("TEMP"), host.MachineEnvironmentValue("TEMP"), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", host.MachineEnvironmentValue("TEMP"));
        Assert.Equal("Windows_NT", host.MachineEnvironmentValue("OS"));
        Assert.Null(host.MachineEnvironmentValue("PIA_TEST_UNSET_" + Guid.NewGuid().ToString("N")));

        using var user = Registry.CurrentUser.OpenSubKey("Environment");
        Assert.SkipWhen(user?.GetValue("TEMP") is null, "no user TEMP on this host");
        Assert.NotEqual(host.RegistryEnvironmentValue("TEMP"), host.MachineEnvironmentValue("TEMP"), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Pia's own process carries every user variable, so only the scopes decide what a machine value may use.</summary>
    [Fact]
    public void AMachineValue_KeepsANameOnlyTheUserDefinesLiteral_AndStillExpandsANameOfNeitherScope()
    {
        var userOnly = "PIA_TEST_USER_ONLY_" + Guid.NewGuid().ToString("N");
        var neither = "PIA_TEST_PROCESS_ONLY_" + Guid.NewGuid().ToString("N");
        var nodeOptions = $@"--require %{userOnly}%\init.js --title=%{neither}%";
        var registry = new RuntimeHostService.RegistryEnvironment(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NODE_OPTIONS"] = nodeOptions },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [userOnly] = @"C:\Users\me\hooks" });
        Environment.SetEnvironmentVariable(userOnly, @"C:\Users\me\hooks");
        Environment.SetEnvironmentVariable(neither, "from-the-process");
        try
        {
            Assert.Equal($@"--require %{userOnly}%\init.js --title=from-the-process", registry.Expand(nodeOptions, machineOnly: true));
            Assert.Equal(@"--require C:\Users\me\hooks\init.js --title=from-the-process", registry.Expand(nodeOptions));
        }
        finally
        {
            Environment.SetEnvironmentVariable(userOnly, null);
            Environment.SetEnvironmentVariable(neither, null);
        }
    }

    [Fact]
    public void TheUserEnvironmentNames_AreTheValueNamesUnderHkcuEnvironment()
    {
        using var environment = Registry.CurrentUser.OpenSubKey("Environment");
        Assert.NotNull(environment);
        var expected = environment.GetValueNames().Where(name => name.Length > 0).ToList();

        var names = RuntimeHostService.ReadUserEnvironmentNames();

        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), names.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(names, name => name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) || name.Equals("TMP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheUserEnvironmentNames_AreReadFromTheGivenKey_AndAMissingKeyGivesNone()
    {
        var subKey = @"Software\Pia.Tests\" + Guid.NewGuid().ToString("N");
        var unique = "PIA_TEST_NAME_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                key.SetValue(unique, "1");
                key.SetValue("", "default");
            }

            Assert.Equal([unique], RuntimeHostService.ReadUserEnvironmentNames(Registry.CurrentUser, subKey));
            Assert.Empty(RuntimeHostService.ReadUserEnvironmentNames(Registry.CurrentUser, subKey + "-missing"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
    }

    /// <summary>No names would mean nothing gets blanked, so a read that fails must not look like an empty key.</summary>
    [Fact]
    public void AUserEnvironmentReadThatFails_Throws_InsteadOfGivingNoNames()
    {
        var subKey = @"Software\Pia.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var parent = Registry.CurrentUser.CreateSubKey(subKey);
            using (var environment = parent.CreateSubKey("Environment"))
                environment.SetValue("PIA_TEST_SECRET", "1");
            parent.Dispose();

            Assert.Throws<ObjectDisposedException>(() => RuntimeHostService.ReadUserEnvironmentNames(parent, "Environment"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void TheCandidateOverride_IsReadOnlyInDebugBuilds()
    {
        var before = Environment.GetEnvironmentVariable(RuntimeHostService.CandidateOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeHostService.CandidateOverrideVariable, @"C:\a; ;C:\b;");
#if DEBUG
            Assert.Equal(new[] { @"C:\a", @"C:\b" }, Host().CandidateOverride);
#else
            Assert.Null(Host().CandidateOverride);
#endif

            Environment.SetEnvironmentVariable(RuntimeHostService.CandidateOverrideVariable, " ");
            Assert.Null(Host().CandidateOverride);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeHostService.CandidateOverrideVariable, before);
        }
    }

    [Fact]
    public void TheProfileRoots_AreTheRealOnes()
    {
        var host = Host();

        Assert.Equal(PiaPaths.LocalProfileRoot, host.LocalAppData);
        Assert.Equal(PiaPaths.RoamingProfileRoot, host.RoamingAppData);
        Assert.Equal(PiaPaths.UserProfileRoot, host.UserProfile);
    }

    [Fact]
    public void TheWindowsAndProgramFilesFolders_AreKnown()
    {
        var host = Host();

        Assert.Equal(Path.GetDirectoryName(Environment.SystemDirectory), host.WindowsDirectory, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(host.ProgramFilesDirectories);
        Assert.All(host.ProgramFilesDirectories, folder => Assert.True(Directory.Exists(folder)));
        Assert.Equal(host.ProgramFilesDirectories.Count, host.ProgramFilesDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AFileVersion_IsCutBeforeItsBuildMetadata()
    {
        var host = Host();
        var version = host.FileVersion(typeof(RuntimeHostService).Assembly.Location);

        Assert.False(string.IsNullOrEmpty(version));
        Assert.DoesNotContain("+", version);
        Assert.DoesNotContain(" ", version);
        Assert.Null(host.FileVersion(Path.Combine(_root, "missing.exe")));
    }

    [Fact]
    public void TheFileWrappers_AnswerForRealAndMissingPaths()
    {
        var host = Host();
        var folder = Dir("tools");
        Dir("tools", "v2");
        Dir("tools", "v1");
        Dir("tools", "other");
        var file = File(folder, "package.json");

        Assert.True(host.FileExists(file));
        Assert.True(host.DirectoryExists(folder));
        Assert.Equal(new[] { Path.Combine(folder, "v1"), Path.Combine(folder, "v2") }, host.EnumerateDirectories(folder, "v*"));
        Assert.Equal("x", host.ReadAllText(file));
        Assert.Empty(host.EnumerateDirectories(Path.Combine(_root, "missing"), "*"));
        Assert.Null(host.ReadAllText(Path.Combine(_root, "missing.json")));
    }
}
