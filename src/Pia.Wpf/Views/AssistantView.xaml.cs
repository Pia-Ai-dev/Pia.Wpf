using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pia.Behaviors;
using Pia.Helpers;
using Pia.Localization;
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

    public static readonly DependencyProperty IsComposerOverflowingProperty =
        DependencyProperty.Register(
            nameof(IsComposerOverflowing),
            typeof(bool),
            typeof(AssistantView),
            new PropertyMetadata(false));

    /// <summary>True while the draft is taller than the collapsed composer — i.e. the expand toggle is worth offering.</summary>
    public bool IsComposerOverflowing
    {
        get => (bool)GetValue(IsComposerOverflowingProperty);
        set => SetValue(IsComposerOverflowingProperty, value);
    }

    /// <summary>Roughly five lines at the composer's font size; the box scrolls past this until expanded.</summary>
    private const double CollapsedComposerHeight = 120;
    private const double ExpandedComposerHeight = 360;

    /// <summary>Padding and border sit inside MaxHeight but outside the text's own extent, so the
    /// overflow threshold has to subtract them. Read from the control, not guessed, so restyling it
    /// cannot silently move the threshold.</summary>
    private double ComposerChrome =>
        InputTextBox.Padding.Top + InputTextBox.Padding.Bottom
        + InputTextBox.BorderThickness.Top + InputTextBox.BorderThickness.Bottom;

    private AssistantViewModel? ViewModel => DataContext as AssistantViewModel;
    private ObservableCollection<AssistantMessage>? _subscribedMessages;
    private readonly HashSet<AssistantMessage> _hookedMessages = [];
    private double? _prependExtent;
    private bool _composerExpanded;

    private readonly Stopwatch _activation = Stopwatch.StartNew();
    private bool _activationReported;

    // The host clears DataContext before Unloaded, so resolving the VM again there finds nothing and the
    // subscription would outlive the view — a production dump held 18 of them that way.
    private AssistantViewModel? _subscribedViewModel;

    public AssistantView()
    {
        InitializeComponent();
        ApplyComposerHeight();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded repeats without an Unloaded on re-parenting, so drop the previous hook before taking a new one.
        DetachViewModel();

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            SubscribeMessages(_subscribedViewModel.VisibleMessages);
            ReportActivation(_subscribedViewModel);
        }

        // Deliberately NOT re-arming auto-scroll here: Loaded repeats on a re-parent, and a reader who
        // scrolled up mid-answer would be yanked back to the newest turn.
        InputTextBox.Focus();
    }

    private void ReportActivation(AssistantViewModel viewModel)
    {
        if (_activationReported) return;
        _activationReported = true;

        // ContextIdle, so the elapsed covers the layout pass that builds the transcript.
        Dispatcher.BeginInvoke(
            new Action(() => viewModel.ReportActivated(_activation.Elapsed)),
            DispatcherPriority.ContextIdle);
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // ExtentHeight is only right once the new text has been measured.
        Dispatcher.BeginInvoke(RefreshComposerOverflow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RefreshComposerOverflow()
    {
        IsComposerOverflowing = InputTextBox.ExtentHeight > CollapsedComposerHeight - ComposerChrome;

        // A send clears the draft, and an expanded box with two lines in it is just a hole in the view.
        if (!IsComposerOverflowing && _composerExpanded)
        {
            _composerExpanded = false;
            ApplyComposerHeight();
        }
    }

    private void ComposerExpandButton_Click(object sender, RoutedEventArgs e)
    {
        _composerExpanded = !_composerExpanded;
        ApplyComposerHeight();
        InputTextBox.Focus();
    }

    private void ApplyComposerHeight()
    {
        InputTextBox.MaxHeight = _composerExpanded ? ExpandedComposerHeight : CollapsedComposerHeight;
        ComposerExpandIcon.Symbol = _composerExpanded
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24
            : Wpf.Ui.Controls.SymbolRegular.ChevronUp24;

        // The one button does both jobs, so the label has to turn round with the icon — a screen
        // reader hears only this, and the XAML's literal would keep saying "expand" while it shrinks.
        var label = LocalizationSource.Instance[_composerExpanded
            ? "Assistant_CollapseComposer_Tooltip"
            : "Assistant_ExpandComposer_Tooltip"];
        AutomationProperties.SetName(ComposerExpandButton, label);
        ComposerExpandButton.ToolTip = new ToolTip { Content = label };
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is null)
            return;

        _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = null;
        SubscribeMessages(null);
    }

    // The VM re-points Messages when the manager's async activation completes after this view loaded; the
    // window it rebuilds from that is what the list shows, so auto-scroll follows the window, not Messages.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantViewModel.Messages))
            return;

        SubscribeMessages(ViewModel?.VisibleMessages);
        PinToEnd();
    }

    /// <summary>Opening a chat shows its latest turn, whatever the reader had scrolled to before.</summary>
    private void PinToEnd()
    {
        IsAutoScrollEnabled = true;
        Dispatcher.BeginInvoke(ScrollToBottom, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SubscribeMessages(ObservableCollection<AssistantMessage>? messages)
    {
        if (ReferenceEquals(_subscribedMessages, messages))
            return;

        if (_subscribedMessages is not null)
            _subscribedMessages.CollectionChanged -= OnMessagesCollectionChanged;
        UnhookMessages();

        _subscribedMessages = messages;
        if (messages is not null)
        {
            messages.CollectionChanged += OnMessagesCollectionChanged;
            foreach (var message in messages)
                HookMessage(message);
        }
    }

    private void HookMessage(AssistantMessage message)
    {
        if (_hookedMessages.Add(message))
            message.PropertyChanged += OnMessagePropertyChanged;
    }

    private void UnhookMessages()
    {
        foreach (var message in _hookedMessages)
            message.PropertyChanged -= OnMessagePropertyChanged;
        _hookedMessages.Clear();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (AssistantMessage message in e.NewItems)
                    HookMessage(message);

                if (IsTailAdd(e))
                {
                    // A new turn means the user wants to see it: resume auto-scroll regardless of
                    // whether they had paused mid-stream of the previous answer. An anchor restore
                    // queued by a refill above would otherwise scroll straight back off the answer.
                    _prependExtent = null;
                    IsAutoScrollEnabled = true;
                    ScrollToBottom();
                }
                else
                {
                    AnchorBeforePrepend();
                }
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (AssistantMessage message in e.OldItems)
                    UnhookMessage(message);
                break;

            case NotifyCollectionChangedAction.Reset:
                // The window is rebuilt out of a transcript that outlives it, so these messages are still
                // alive and would keep this view reachable through their PropertyChanged.
                UnhookMessages();
                IsAutoScrollEnabled = true;
                break;
        }
    }

    private void UnhookMessage(AssistantMessage message)
    {
        if (_hookedMessages.Remove(message))
            message.PropertyChanged -= OnMessagePropertyChanged;
    }

    private bool IsTailAdd(NotifyCollectionChangedEventArgs e) =>
        _subscribedMessages is null
        || e.NewStartingIndex < 0
        || e.NewStartingIndex + e.NewItems!.Count == _subscribedMessages.Count;

    // Older messages arriving above the viewport push everything below them down by their own height, and
    // that height is unknown until they are measured.
    private void AnchorBeforePrepend()
    {
        IsAutoScrollEnabled = false;
        if (_prependExtent is not null)
            return;

        _prependExtent = MessageScrollViewer.ExtentHeight;
        Dispatcher.BeginInvoke(RestoreOffsetAfterPrepend, DispatcherPriority.Loaded);
    }

    private void RestoreOffsetAfterPrepend()
    {
        if (_prependExtent is not { } before)
            return;
        _prependExtent = null;

        MessageScrollViewer.UpdateLayout();
        MessageScrollViewer.ScrollToVerticalOffset(
            MessageScrollViewer.VerticalOffset + (MessageScrollViewer.ExtentHeight - before));
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
        // A chat opened from history arrives already populated, and its markdown bubbles keep growing
        // the extent for several passes — one ScrollToEnd would land short of the newest turn.
        if (e.ExtentHeightChange != 0)
        {
            if (IsAutoScrollEnabled) MessageScrollViewer.ScrollToEnd();
            return;
        }

        if (e.VerticalChange == 0) return;

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

    // Read the POPUP, not the flag: a dismissal the flag misses leaves the next press toggling a
    // stale value and opening nothing.
    private void EmptyWorkingDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.ChatTitleChip.IsInlinePickerOpen = !EmptyWorkingDirPopup.IsOpen;
    }

    // Posted: the popup's content is still being connected when Opened fires.
    private void EmptyWorkingDirPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(EmptyWorkingDirPicker.FocusEntries));

    private void EmptyWorkingDirPopup_Closed(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
            vm.ChatTitleChip.IsInlinePickerOpen = false;
    }

    private void EmptyWorkingDirPicker_CloseRequested(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.ChatTitleChip.IsInlinePickerOpen = false;
            EmptyWorkingDirButton.Focus();
        }
    }

    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        var files = DebugDroppedPaths() ?? FilePicker.PickFiles(FileDropBehavior.GetAcceptedExtensions(RootGrid));
        if (files.Count == 0) return;

        if (ViewModel?.HandleFilesDroppedCommand.CanExecute(files) == true)
            ViewModel.HandleFilesDroppedCommand.Execute(files);
    }

    /// <summary>Dev-only: a preset path list that stands in for the file picker, so a UI script can drive the
    /// real Attach-file button without automating a native dialog. Always null in release.</summary>
    private static IReadOnlyList<string>? DebugDroppedPaths()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(Bootstrapper.DebugDropFilesEnvVar) is not { Length: > 0 } value)
            return null;

        var paths = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Length == 0 ? null : paths;
#else
        return null;
#endif
    }
}