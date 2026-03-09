using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pia.Views.Dialogs.Overlay;

public partial class OptimizingOverlayPanel : UserControl
{
    private readonly string[] _messages;
    private readonly DispatcherTimer _messageTimer;

    public OptimizingOverlayPanel(string[] messages)
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
    }

    public void StopTimer() => _messageTimer.Stop();

    private void UpdateMessage()
    {
        MessageTextBlock.Text = _messages[RandomNumberGenerator.GetInt32(_messages.Length)];
    }
}
