using System.Security.Cryptography;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class OptimizingContentDialog : ContentDialog
{
    private readonly string[] _messages;
    private readonly DispatcherTimer _messageTimer;

    public OptimizingContentDialog(
        ContentDialogHost dialogHost,
        string[] messages)
        : base(dialogHost)
    {
        _messages = messages;
        InitializeComponent();

        UpdateMessage();

        _messageTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _messageTimer.Tick += (_, _) => UpdateMessage();
        _messageTimer.Start();

        Closing += OnClosing;
    }

    private void UpdateMessage()
    {
        MessageTextBlock.Text = _messages[RandomNumberGenerator.GetInt32(_messages.Length)];
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _messageTimer.Stop();
    }
}
