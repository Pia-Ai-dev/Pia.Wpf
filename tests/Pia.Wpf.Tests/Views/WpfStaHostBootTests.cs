using Pia;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Application's constructor posts its startup callback and this host pumps a dispatcher, so App.OnStartup used
/// to run inside the gate: it migrated the developer's real history.db and reconciled their vault.
/// </summary>
[Collection("WpfApplicationStatic")]
public class WpfStaHostBootTests
{
    [Fact]
    public void StartingTheHost_DoesNotBootTheApplication()
    {
        // Through the host, so this cannot pass by never having created the Application at all.
        Assert.True(WpfStaHost.Run(() => System.Windows.Application.Current is not null));

        Assert.Throws<InvalidOperationException>(() => Bootstrapper.ServiceProvider);
    }
}
