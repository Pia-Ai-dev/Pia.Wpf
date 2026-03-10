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
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (AssistantMessage message in e.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
            ScrollToBottom();
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // All items removed — unsubscribe handled implicitly since objects are gone
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
        MessageScrollViewer.ScrollToEnd();
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
