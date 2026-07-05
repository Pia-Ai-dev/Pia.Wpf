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

    public PiaAssistantChatRowContent() => InitializeComponent();
}
