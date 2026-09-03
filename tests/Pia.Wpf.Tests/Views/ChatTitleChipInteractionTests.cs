using System.Windows;
using System.Windows.Data;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Two defects a walkthrough hit and no test could see: a dismissal the view-model flag missed, so the
/// next press toggled a stale value and opened nothing, and the folder list's selection move being POSTED,
/// so the next key of a burst arrived before it had happened and went nowhere. Both need a real input
/// source, hence the hidden HwndSource.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ChatTitleChipInteractionTests
{
    private PiaChatTitleChip _chip = null!;
    private ChatTitleChipViewModel _vm = null!;
    private HwndSource _source = null!;

    // The binding is cleared in both tests below so the popup's state and the flag can be pulled apart
    // on purpose - that split is the bug, and with the binding in place the test would pass either way.
    [Theory]
    [InlineData("FlyoutPopup", "ChatChip_Toggle")]
    [InlineData("WorkingDirPopup", "ChatChip_WorkingDir")]
    public void AClosedPopup_OpensOnOnePress_EvenWhenTheFlagStillSaysOpen(string popup, string button)
    {
        WpfStaHost.Run(() => Host());
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            _vm.IsFlyoutOpen = true;
            Unbind(popup);
            Set(popup, true);
            Invoke(button);
            return 0;
        });
        // InvokePattern dispatches the click, so the press has not happened until this drains.
        WpfStaHost.Pump();

        Assert.True(WpfStaHost.Run(() => Get(popup)),
            "the press read the stale flag instead of the popup, so it closed nothing and opened nothing");

        WpfStaHost.Run(Dispose);
    }

    [Theory]
    [InlineData("FlyoutPopup")]
    [InlineData("WorkingDirPopup")]
    public void APopupThatDismissesItself_PullsTheFlagDownWithIt(string popup)
    {
        WpfStaHost.Run(() => Host());
        WpfStaHost.Pump();

        WpfStaHost.Run(() => { _vm.IsFlyoutOpen = true; _vm.IsPickerOpen = true; return 0; });
        WpfStaHost.Pump();
        Assert.True(WpfStaHost.Run(() => Popup(popup).IsOpen), $"{popup} never opened");

        WpfStaHost.Run(() =>
        {
            // Without the fade, Closed still arrives on the queue rather than inline - hence the pump.
            Popup(popup).PopupAnimation = PopupAnimation.None;
            Unbind(popup);
            Popup(popup).IsOpen = false;
            return 0;
        });
        WpfStaHost.Pump();

        Assert.False(WpfStaHost.Run(() => Get(popup)));

        WpfStaHost.Run(Dispose);
    }

    private void Unbind(string popup) =>
        BindingOperations.ClearBinding(Popup(popup), System.Windows.Controls.Primitives.Popup.IsOpenProperty);

    private bool Get(string popup) =>
        popup == "FlyoutPopup" ? _vm.IsFlyoutOpen : _vm.IsPickerOpen;

    private void Set(string popup, bool value)
    {
        if (popup == "FlyoutPopup") _vm.IsFlyoutOpen = value; else _vm.IsPickerOpen = value;
    }

    [Fact]
    public void BackspaceMovesTheSelection_BeforeTheNextKeyCouldArrive()
    {
        WpfStaHost.Run(() => Host());
        WpfStaHost.Pump();

        WpfStaHost.Run(() =>
        {
            _vm.IsFlyoutOpen = true;
            _vm.WorkingDirectoryPicker.InitializeFrom("Alpha/Beta");
            return 0;
        });
        WpfStaHost.Pump();

        // Read back inside the SAME dispatcher turn as the key: a posted focus/selection move has not
        // run yet at this point, which is exactly why the arrows of a one-call burst went nowhere.
        var landed = WpfStaHost.Run(() =>
        {
            var list = (ListBox)_chip.FindName("WorkingDirEntries")!;
            Send(list, Key.Back);
            return $"{string.Join(',', list.Items.Cast<string>())}|{list.SelectedItem}";
        });

        Assert.Equal("Beta,Gamma|Beta", landed);

        WpfStaHost.Run(Dispose);
    }

    private int Host()
    {
        var chats = Substitute.For<IAssistantChatService>();
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(c => (string)c[0]);

        var folders = Substitute.For<IWorkingDirectoryService>();
        folders.ListSubfolders(Arg.Any<string>()).Returns(c => (string)c[0] switch
        {
            "" => new[] { "Alpha", "Delta" },
            "Alpha" => ["Beta", "Gamma"],
            _ => [],
        });

        _vm = new ChatTitleChipViewModel(
            chats,
            loc,
            NullLogger<ChatTitleChipViewModel>.Instance,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(true),
            _ => { },
            () => { },
            _ => ChatState.Idle,
            folders,
            _ => { },
            () => null);

        _chip = new PiaChatTitleChip { DataContext = _vm };

        // Not shown (no WS_VISIBLE): the chip only needs a PresentationSource so keyboard focus and
        // KeyEventArgs are real, and a popup can create its own window.
        _source = new HwndSource(new HwndSourceParameters("PiaChatTitleChipTests")
        {
            Width = 600,
            Height = 600,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        })
        {
            RootVisual = _chip,
        };

        _chip.UpdateLayout();
        return 0;
    }

    private int Dispose()
    {
        _source.Dispose();
        _vm.Dispose();
        return 0;
    }

    private Popup Popup(string name) => (Popup)_chip.FindName(name)!;

    private void Invoke(string automationId)
    {
        var button = Descendants(_chip)
            .OfType<ButtonBase>()
            .First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == automationId);

        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button)!
            .GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private void Send(IInputElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, _source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
