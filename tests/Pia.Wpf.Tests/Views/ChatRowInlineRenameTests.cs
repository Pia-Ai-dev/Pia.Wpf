using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Pia.Controls.AssistantHistory;
using Pia.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The flyout renames a chat in place rather than through a dialog: the popup closes the moment focus
/// leaves it, so a modal would take the list you were reading with it.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ChatRowInlineRenameTests : IDisposable
{
    private static readonly ChatChipItemViewModel Row =
        new(Guid.NewGuid(), "the old name", DateTime.UtcNow, ChatState.Idle);

    private readonly HwndSource _source;

    public ChatRowInlineRenameTests() =>
        // Not shown (WS_POPUP, no WS_VISIBLE): the row only needs a PresentationSource so a KeyEventArgs
        // is real, the same trick ChatTitleChipInteractionTests uses.
        _source = WpfStaHost.Run(() => new HwndSource(new HwndSourceParameters("PiaChatRowRenameTests")
        {
            Width = 400,
            Height = 60,
            WindowStyle = unchecked((int)0x80000000),
        }));

    public void Dispose() => WpfStaHost.Run(() => { _source.Dispose(); return 0; });

    [Fact]
    public void AHostThatOffersNoRenameCommandGetsNoPencil()
    {
        PiaAssistantChatRowContent? content = null;

        WpfStaHost.Run(() =>
        {
            content = new PiaAssistantChatRowContent { DataContext = Row };
            _source.RootVisual = content;
            content.UpdateLayout();
            return 0;
        });
        // The visibility comes from a converter on RenameCommand; unpumped it still reads its default.
        WpfStaHost.Pump();

        var visibility = WpfStaHost.Run(() =>
            ((FrameworkElement)content!.FindName("RenameHost")).Visibility);

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    [Fact]
    public void ThePencilSwapsTheRowForAnEditorSeededWithTheCurrentName()
    {
        var (editor, body, text) = WpfStaHost.Run(() =>
        {
            var content = Build(out _);
            StartRenaming(content);
            return (Editor(content).Visibility, Body(content).Visibility, Box(content).Text);
        });

        Assert.Equal(Visibility.Visible, editor);
        Assert.Equal(Visibility.Collapsed, body);
        Assert.Equal("the old name", text);
    }

    [Fact]
    public void EnterHandsTheTrimmedNameToTheHostAndCloses()
    {
        var (title, row, editor) = WpfStaHost.Run(() =>
        {
            var content = Build(out var command);
            StartRenaming(content);
            Box(content).Text = "  a name of my own  ";
            Press(content, Key.Enter);
            return (command.Received?.Title, command.Received?.Row, Editor(content).Visibility);
        });

        Assert.Equal("a name of my own", title);
        Assert.Same(Row, row);
        Assert.Equal(Visibility.Collapsed, editor);
    }

    [Fact]
    public void EscapeClosesTheEditorAndRenamesNothing()
    {
        var (renamed, editor) = WpfStaHost.Run(() =>
        {
            var content = Build(out var command);
            StartRenaming(content);
            Box(content).Text = "typed then abandoned";
            Press(content, Key.Escape);
            return (command.Received is not null, Editor(content).Visibility);
        });

        Assert.False(renamed, "Escape wrote the abandoned name");
        Assert.Equal(Visibility.Collapsed, editor);
    }

    /// <summary>Confirming the name it already has would cost a write and a list rebuild for nothing.</summary>
    [Fact]
    public void ConfirmingTheUnchangedNameRenamesNothing()
    {
        var renamed = WpfStaHost.Run(() =>
        {
            var content = Build(out var command);
            StartRenaming(content);
            Click(content, "RenameConfirm");
            return command.Received is not null;
        });

        Assert.False(renamed);
    }

    [Fact]
    public void TheCancelButtonClosesTheEditor()
    {
        var (renamed, editor) = WpfStaHost.Run(() =>
        {
            var content = Build(out var command);
            StartRenaming(content);
            Box(content).Text = "abandoned";
            Click(content, "RenameCancel");
            return (command.Received is not null, Editor(content).Visibility);
        });

        Assert.False(renamed);
        Assert.Equal(Visibility.Collapsed, editor);
    }

    private PiaAssistantChatRowContent Build(out RecordingCommand command)
    {
        command = new RecordingCommand();
        var content = new PiaAssistantChatRowContent
        {
            DataContext = Row,
            EditableTitle = Row.Title,
            RenameCommand = command,
        };

        _source.RootVisual = content;
        content.UpdateLayout();
        return content;
    }

    private static void StartRenaming(PiaAssistantChatRowContent content) => Click(content, "RenamePencil");

    private static void Click(PiaAssistantChatRowContent content, string name) =>
        ((ButtonBase)content.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private void Press(PiaAssistantChatRowContent content, Key key) =>
        Box(content).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, _source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });

    private static TextBox Box(PiaAssistantChatRowContent content) =>
        (TextBox)content.FindName("RenameBox");

    private static FrameworkElement Editor(PiaAssistantChatRowContent content) =>
        (FrameworkElement)content.FindName("RenameEditor");

    private static FrameworkElement Body(PiaAssistantChatRowContent content) =>
        (FrameworkElement)content.FindName("RowBody");

    private sealed class RecordingCommand : ICommand
    {
        public ChatRowRenameRequest? Received { get; private set; }

        // Never raised: the row asks CanExecute once, at commit.
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Received = parameter as ChatRowRenameRequest;
    }
}
