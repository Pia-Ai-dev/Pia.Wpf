using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

public partial class AssistantView : UserControl
{
    private AssistantViewModel? ViewModel => DataContext as AssistantViewModel;
    private bool _autoScroll = true;

    public AssistantView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        }

        MessageScrollViewer.ScrollChanged += OnMessageScrollChanged;
        InputTextBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            // Unsubscribe from all message PropertyChanged events
            foreach (var message in ViewModel.Messages)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
            ViewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
        }
        MessageScrollViewer.ScrollChanged -= OnMessageScrollChanged;
    }

    private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Re-evaluate auto-scroll only when the viewport moved without content growing —
        // that's a user-driven scroll. Content growth during streaming leaves VerticalOffset
        // unchanged when the user has scrolled away from the bottom, so we never confuse the two.
        if (e.ExtentHeightChange == 0 && e.VerticalChange != 0)
        {
            _autoScroll = MessageScrollViewer.VerticalOffset >= MessageScrollViewer.ScrollableHeight - 1.0;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (AssistantMessage message in e.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
            // A new message is a new logical event — resume following the conversation.
            _autoScroll = true;
            ScrollToBottom();
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // All items removed — unsubscribe handled implicitly since objects are gone
            _autoScroll = true;
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantMessage.Content) or nameof(AssistantMessage.HasActionCards))
        {
            Dispatcher.BeginInvoke(ScrollToBottom, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ScrollToBottom()
    {
        if (_autoScroll)
        {
            MessageScrollViewer.ScrollToEnd();
        }
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let the autocomplete popup handle Enter/Escape when it's open
        if (AtCommandPopup.IsOpen)
            return;

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (ViewModel?.SendMessageCommand.CanExecute(null) == true)
            {
                ViewModel.SendMessageCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel?.CancelStreamingCommand.CanExecute(null) == true)
            {
                ViewModel.CancelStreamingCommand.Execute(null);
                e.Handled = true;
            }
        }
        // Shift+Enter: default behavior (newline) — no handling needed
    }

    private void OnAddToPiiRequested(object? sender, PiiKeywordRequest request)
    {
        ViewModel?.AddPiiKeywordCommand.Execute(request);
    }
}
