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
    private AutomationPeer _revisitRoot = null!;

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

    /// <summary>A UIA client attached before Settings was revisited: the ItemsControl's peer is walked once
    /// while empty, caching an empty subtree, then the seven rows arrive in one go on the revisit.</summary>
    [Fact]
    public void RowsAddedAfterAnEarlierWalk_AreStillReadable()
    {
        WpfStaHost.Run(BuildEmptyWalkThenAddRows);
        WpfStaHost.Pump();

        var ids = WpfStaHost.Run(SurveyRevisitWithoutReset);

        foreach (var key in new[] { "weekly", "pool" })
        {
            Assert.Contains($"Settings_Account_Credits_Label_{key}", ids);
            Assert.Contains($"Settings_Account_Credits_Caption_{key}", ids);
            Assert.Contains($"Settings_Account_Credits_Bar_{key}", ids);
        }
    }

    private bool BuildEmptyWalkThenAddRows()
    {
        var vm = new TestAccountViewModel { IsSyncLoggedIn = true, HasCredits = true };
        _view = new AccountView { DataContext = vm };
        LayoutRevisitView();

        _revisitRoot = UIElementAutomationPeer.CreatePeerForElement(_view)
            ?? throw new InvalidOperationException("AccountView no longer creates an automation peer");
        Collect(_revisitRoot, []); // the attached client's first walk, while the card is empty

        vm.CreditMeters.Add(new CreditMeter("weekly", "This week", 3, 10, "3 of 10 used"));
        vm.CreditMeters.Add(new CreditMeter("pool", "Free team pool this week", 4, 5, "1 of 5 left"));

        // The premise: nothing has been through a layout pass yet, so the walk below finds no rows
        // regardless of the fix — matches this file's other test only after Layout runs again.
        Assert.DoesNotContain(SurveyRevisitWithoutReset(),
            id => id.StartsWith("Settings_Account_Credits_Label_", StringComparison.Ordinal));

        LayoutRevisitView();
        return true;
    }

    private string[] SurveyRevisitWithoutReset()
    {
        var ids = new List<string>();
        Collect(_revisitRoot, ids);
        return [.. ids];
    }

    private void LayoutRevisitView()
    {
        _view.Measure(new Size(1000, 2000));
        _view.Arrange(new Rect(0, 0, 1000, 2000));
        _view.UpdateLayout();
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
