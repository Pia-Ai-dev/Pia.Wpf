using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Pia.Behaviors;
using Pia.Helpers;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

public partial class AssistantView : UserControl
{
    public static readonly DependencyProperty IsAutoScrollEnabledProperty =
        DependencyProperty.Register(
            nameof(IsAutoScrollEnabled),
            typeof(bool),
            typeof(AssistantView),
            new PropertyMetadata(true));

    public bool IsAutoScrollEnabled
    {
        get => (bool)GetValue(IsAutoScrollEnabledProperty);
        set => SetValue(IsAutoScrollEnabledProperty, value);
    }

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
            // A new turn means the user wants to see it: resume auto-scroll regardless of
            // whether they had paused mid-stream of the previous answer.
            IsAutoScrollEnabled = true;
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
        if (!IsAutoScrollEnabled) return;
        MessageScrollViewer.ScrollToEnd();
    }

    private void MessageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Distinguish a user-driven vertical scroll from a scroll caused by content growth.
        // ExtentHeightChange != 0 means new content arrived; ignore those.
        if (e.VerticalChange == 0) return;
        if (e.ExtentHeightChange != 0) return;

        var atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
        IsAutoScrollEnabled = atBottom;
    }

    private void MessageItemsControl_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Streaming markdown bubbles up RequestBringIntoView during layout; the ScrollViewer's
        // class handler would honor it regardless of our pause flag. Swallow it here so the
        // user's manual scroll position is preserved while paused.
        if (!IsAutoScrollEnabled)
        {
            e.Handled = true;
        }
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (ViewModel?.ChatTitleChip.OpenQuickSwitcherCommand.CanExecute(null) == true)
            {
                ViewModel.ChatTitleChip.OpenQuickSwitcherCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V with an image on the clipboard: attach it instead of pasting nothing useful
        // into the text box. Checked before the popup early-return so paste works regardless.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryGetClipboardImage(out var image) && ViewModel is not null)
            {
                e.Handled = true; // suppress the default (text) paste
                if (ViewModel.HandleImagePastedCommand.CanExecute(image))
                    ViewModel.HandleImagePastedCommand.Execute(image);
                return;
            }
        }

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

    private static bool TryGetClipboardImage(out BitmapSource? image)
    {
        image = null;
        try
        {
            if (!Clipboard.ContainsImage()) return false;
            image = Clipboard.GetImage();
            return image is not null;
        }
        catch
        {
            // Clipboard access is best-effort: it can be locked by another process or hold a
            // malformed image. Fall back to the default text paste rather than throwing.
            return false;
        }
    }

    private void OnAddToPiiRequested(object? sender, PiiKeywordRequest request)
    {
        ViewModel?.AddPiiKeywordCommand.Execute(request);
    }

    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        var accepted = FileDropBehavior.GetAcceptedExtensions(RootGrid);
        var files = FilePicker.PickFiles(accepted);
        if (files.Count == 0) return;

        if (files.Count == 1 && DroppedFileReader.Classify(files[0]) == FileKind.Image)
        {
            if (ViewModel?.HandleImageAttachedCommand.CanExecute(files[0]) == true)
                ViewModel.HandleImageAttachedCommand.Execute(files[0]);
            return;
        }

        if (ViewModel?.HandleFilesDroppedCommand.CanExecute(files) == true)
            ViewModel.HandleFilesDroppedCommand.Execute(files);
    }
}