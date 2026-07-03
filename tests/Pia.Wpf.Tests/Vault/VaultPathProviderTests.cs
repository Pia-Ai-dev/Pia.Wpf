using System;
using System.IO;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultPathProviderTests
{
    [Fact]
    public void Default_root_is_Pia_Vault_under_local_app_data()
    {
        var provider = new VaultPathProvider();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = Path.Combine(localAppData, "Pia", "Vault");

        Assert.Equal(expected, provider.VaultRoot);
        Assert.StartsWith(localAppData, provider.VaultRoot);
        Assert.EndsWith(Path.Combine("Pia", "Vault"), provider.VaultRoot);
    }

    [Fact]
    public void Override_ctor_uses_supplied_root()
    {
        var provider = new VaultPathProvider("/some/custom/root");

        Assert.Equal("/some/custom/root", provider.VaultRoot);
    }

    [Fact]
    public void SetRoot_updates_VaultRoot()
    {
        var provider = new VaultPathProvider("/initial");
        provider.SetRoot("/changed");
        Assert.Equal("/changed", provider.VaultRoot);
    }

    [Fact]
    public void SetRoot_rejects_blank()
    {
        var provider = new VaultPathProvider("/initial");
        Assert.Throws<ArgumentException>(() => provider.SetRoot("  "));
    }
}
