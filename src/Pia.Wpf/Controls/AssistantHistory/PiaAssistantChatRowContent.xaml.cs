using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pia.Controls.AssistantHistory;

public partial class PiaAssistantChatRowContent : UserControl
{
    /// <summary>
    /// Optional per-row delete command (hover trash bin that replaces the date). Left unset by hosts
    /// that don't offer delete — the trash then stays hidden. The command parameter is the row's own
    /// DataContext (the chat/chip item VM), so each host binds a command matching its item type.
    /// </summary>
    public static readonly DependencyProperty DeleteCommandProperty =
        DependencyProperty.Register(nameof(DeleteCommand), typeof(ICommand), typeof(PiaAssistantChatRowContent));

    public ICommand? DeleteCommand
    {
        get => (ICommand?)GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    /// <summary>
    /// Optional per-row open command, on the same hover strip as the delete one. A host that offers delete
    /// without this leaves a row that a script can destroy by id but not open by one.
    /// </summary>
    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(PiaAssistantChatRowContent));

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    /// <summary>
    /// Optional per-row rename command, on the same hover strip. Takes a <c>ChatRowRenameRequest</c>
    /// rather than the row, because the name the user typed lives in this control, not in the item VM.
    /// </summary>
    public static readonly DependencyProperty RenameCommandProperty =
        DependencyProperty.Register(nameof(RenameCommand), typeof(ICommand), typeof(PiaAssistantChatRowContent));

    public ICommand? RenameCommand
    {
        get => (ICommand?)GetValue(RenameCommandProperty);
        set => SetValue(RenameCommandProperty, value);
    }

    /// <summary>The name the inline editor opens with. A host offering <see cref="RenameCommand"/> binds
    /// this to the same title the row shows; the row's own title is drawn through a behaviour that leaves
    /// no readable text behind.</summary>
    public static readonly DependencyProperty EditableTitleProperty =
        DependencyProperty.Register(nameof(EditableTitle), typeof(string), typeof(PiaAssistantChatRowContent));

    public string? EditableTitle
    {
        get => (string?)GetValue(EditableTitleProperty);
        set => SetValue(EditableTitleProperty, value);
    }

    public PiaAssistantChatRowContent() => InitializeComponent();

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        // The row is the content of the host's own button; without this the click resumes the chat.
        e.Handled = true;

        RenameBox.Text = EditableTitle ?? string.Empty;
        ShowEditor(true);
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void ConfirmRenameButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Commit();
    }

    private void CancelRenameButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ShowEditor(false);
    }

    private void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Escape:
                e.Handled = true;
                ShowEditor(false);
                break;
        }
    }

    /// <summary>Clicks on the editor's own padding would otherwise reach the host's row button and resume
    /// the chat mid-edit. The box itself is exempt, or it never sees the click that places the caret.</summary>
    private void RenameEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && (ReferenceEquals(source, RenameBox) || RenameBox.IsAncestorOf(source)))
            return;

        e.Handled = true;
    }

    private void Commit()
    {
        var title = RenameBox.Text?.Trim();
        ShowEditor(false);

        if (string.IsNullOrEmpty(title) || title == EditableTitle) return;

        var request = new ViewModels.Models.ChatRowRenameRequest(DataContext, title);
        if (RenameCommand?.CanExecute(request) == true)
            RenameCommand.Execute(request);
    }

    private void ShowEditor(bool editing)
    {
        RenameEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        RowBody.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
    }
}
