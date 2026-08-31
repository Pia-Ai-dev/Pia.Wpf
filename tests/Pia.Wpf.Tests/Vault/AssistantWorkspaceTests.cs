using System;
using System.IO;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Vault;

public class AssistantWorkspaceTests
{
    [Fact]
    public void DefaultRoot_is_PiaAssistant_under_user_profile_Documents()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(profile, "Documents", "Pia Assistant");
        Assert.Equal(expected, AssistantWorkspace.DefaultRoot);
        Assert.StartsWith(profile, AssistantWorkspace.DefaultRoot);
    }

    [Fact]
    public void LegacyWorkdir_is_workdir_under_local_app_data_Pia()
    {
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(lad, "Pia", "workdir"), AssistantWorkspace.LegacyWorkdir);
    }

    [Fact]
    public void VaultRootFor_appends_Vault_subfolder()
    {
        Assert.Equal(Path.Combine(@"C:\x\y", "Vault"), AssistantWorkspace.VaultRootFor(@"C:\x\y"));
    }

    [Theory]
    [InlineData(@"C:\x\Vault", true)]
    [InlineData(@"C:\x\Vault\sources\a.md", true)]
    [InlineData(@"C:\x\vault\a.md", true)]
    [InlineData(@"C:\x\Vault Backups\a.md", false)]
    [InlineData(@"C:\x\docs\Vault\a.md", false)]
    [InlineData(@"C:\x\VaultNotes.md", false)]
    [InlineData(@"C:\x\a.md", false)]
    [InlineData(@"C:\x", false)]
    public void IsAtOrInsideVaultOf_Theory(string candidate, bool expected)
    {
        Assert.Equal(expected, AssistantWorkspace.IsAtOrInsideVaultOf(@"C:\x", candidate));
    }
}
