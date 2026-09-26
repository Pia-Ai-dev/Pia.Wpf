using System.Windows;
using System.Windows.Automation.Peers;
using System.Collections.ObjectModel;
using Pia.ViewModels;
using Pia.Views.SettingsViews;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>A plain Border may create no automation peer, so this walks the real peer tree WinWright reads.</summary>
[Collection("WpfApplicationStatic")]
public class AccountViewCreditsAutomationTests
{
    private AccountView _view = null!;

    [Fact]
    public void CreditsCard_ExposesCardSuspendedAndPerRowIds()
    {
        WpfStaHost.Run(() => { Build(); return 0; });
        WpfStaHost.Pump();

        var ids = WpfStaHost.Run(Survey);

        Assert.Contains("Settings_Account_Credits", ids);
        Assert.Contains("Settings_Account_Credits_Suspended", ids);
        foreach (var key in new[] { "weekly", "pool" })
        {
            Assert.Contains($"Settings_Account_Credits_Label_{key}", ids);
            Assert.Contains($"Settings_Account_Credits_Caption_{key}", ids);
            Assert.Contains($"Settings_Account_Credits_Bar_{key}", ids);
        }
    }

    private void Build()
    {
        _view = new AccountView
        {
            DataContext = new TestAccountViewModel
            {
                IsSyncLoggedIn = true,
                HasCredits = true,
                IsCreditTierSuspended = true,
                CreditMeters =
                [
                    new CreditMeter("weekly", "This week", 3, 10, "3 of 10 used"),
                    new CreditMeter("pool", "Free team pool this week", 4, 5, "1 of 5 left"),
                ],
            },
        };
    }

    private string[] Survey()
    {
        _view.Measure(new Size(1000, 2000));
        _view.Arrange(new Rect(0, 0, 1000, 2000));
        _view.UpdateLayout();

        var root = UIElementAutomationPeer.CreatePeerForElement(_view)
            ?? throw new InvalidOperationException("AccountView no longer creates an automation peer");

        ResetSubtree(root);

        var ids = new List<string>();
        Collect(root, ids);
        return [.. ids];
    }

    private static void ResetSubtree(AutomationPeer peer)
    {
        peer.ResetChildrenCache();
        foreach (var child in peer.GetChildren() ?? [])
            ResetSubtree(child);
    }

    private static void Collect(AutomationPeer peer, List<string> ids)
    {
        if (peer.GetAutomationId() is { Length: > 0 } id)
            ids.Add(id);

        foreach (var child in peer.GetChildren() ?? [])
            Collect(child, ids);
    }

    private sealed class TestAccountViewModel
    {
        public bool IsSyncLoggedIn { get; init; }
        public bool HasCredits { get; init; }
        public bool IsCreditTierSuspended { get; init; }
        public ObservableCollection<CreditMeter> CreditMeters { get; init; } = [];
    }
}
