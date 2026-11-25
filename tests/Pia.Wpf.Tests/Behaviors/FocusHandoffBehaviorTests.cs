using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Pia.Behaviors;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Behaviors;

/// <summary>WPF re-homes focus from a disabled element asynchronously, so each test pumps before it observes.</summary>
[Collection("WpfApplicationStatic")]
public class FocusHandoffBehaviorTests
{
    [Fact]
    public void AFocusedButtonWhoseCommandStopsExecuting_PassesFocusToTheNextControl()
    {
        Rig? rig = null;
        var hadFocus = WpfStaHost.Run(() =>
        {
            var canExecute = true;
            var command = new RelayCommand(() => { }, () => canExecute);
            rig = Host(withBehavior: true, command);
            Keyboard.Focus(rig.Button);
            var had = rig.Button.IsKeyboardFocused;
            canExecute = false;
            command.NotifyCanExecuteChanged();
            return had;
        });
        WpfStaHost.Pump();

        var focused = Observe(rig!);

        Assert.True(hadFocus, "the button never took keyboard focus, so the test proves nothing");
        Assert.Equal(nameof(CheckBox), focused);
    }

    [Fact]
    public void WithoutTheBehavior_FocusDoesNotReachTheNextControl()
    {
        Rig? rig = null;
        var hadFocus = WpfStaHost.Run(() =>
        {
            rig = Host(withBehavior: false, command: null);
            Keyboard.Focus(rig.Button);
            var had = rig.Button.IsKeyboardFocused;
            rig.Button.IsEnabled = false;
            return had;
        });
        WpfStaHost.Pump();

        var focused = Observe(rig!);

        Assert.True(hadFocus, "the button never took keyboard focus, so the test proves nothing");
        Assert.NotEqual(nameof(CheckBox), focused);
    }

    [Fact]
    public void AnUnfocusedButtonThatDisables_LeavesFocusWhereItIs()
    {
        Rig? rig = null;
        WpfStaHost.Run(() =>
        {
            rig = Host(withBehavior: true, command: null);
            Keyboard.Focus(rig.Elsewhere);
            rig.Button.IsEnabled = false;
            return 0;
        });
        WpfStaHost.Pump();

        Assert.Equal(nameof(TextBox), Observe(rig!));
    }

    private static string Observe(Rig rig) => WpfStaHost.Run(() =>
    {
        var name = Keyboard.FocusedElement?.GetType().Name ?? "(none)";
        rig.Window.Close();
        return name;
    });

    private sealed record Rig(Window Window, TextBox Elsewhere, Button Button);

    private static Rig Host(bool withBehavior, ICommand? command)
    {
        var elsewhere = new TextBox();
        var button = new Button { Content = "Export", Command = command };
        FocusHandoffBehavior.SetMoveFocusWhenDisabled(button, withBehavior);
        var panel = new StackPanel();
        panel.Children.Add(elsewhere);
        panel.Children.Add(button);
        panel.Children.Add(new CheckBox { Content = "Understood" });

        var window = new Window
        {
            Content = panel,
            Width = 300,
            Height = 200,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        return new Rig(window, elsewhere, button);
    }
}
