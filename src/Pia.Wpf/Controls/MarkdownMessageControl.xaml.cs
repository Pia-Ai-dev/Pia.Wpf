using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Pia.Controls.Markdown;
using Pia.Helpers;
using Pia.Models;

namespace Pia.Controls;

public partial class MarkdownMessageControl : UserControl
{
    public event EventHandler<PiiKeywordRequest>? AddToPiiRequested;

    /// <summary>
    /// Raised when the user clicks an in-app wikilink (a <see cref="WikiLinkScheme"/> URI). The argument is
    /// the vault-relative link target (e.g. <c>topics/foo</c>). Only surfaces where the content was rewritten
    /// by <c>WikiLinkConverter</c>; other markdown never produces this scheme, so this stays silent elsewhere.
    /// </summary>
    public event EventHandler<string>? WikiLinkNavigate;

    private readonly DispatcherTimer _debounceTimer;
    private string? _pendingMarkdown;

    public static readonly DependencyProperty MarkdownTextProperty =
        DependencyProperty.Register(
            nameof(MarkdownText),
            typeof(string),
            typeof(MarkdownMessageControl),
            new PropertyMetadata(string.Empty, OnMarkdownTextChanged));

    public static readonly DependencyProperty IsStreamingProperty =
        DependencyProperty.Register(
            nameof(IsStreaming),
            typeof(bool),
            typeof(MarkdownMessageControl),
            new PropertyMetadata(false, OnIsStreamingChanged));

    /// <summary>Host-supplied discriminator appended to the inner viewer's AutomationId so two
    /// instances on screen at once don't report the same id.</summary>
    public static readonly DependencyProperty AutomationIdSuffixProperty =
        DependencyProperty.Register(
            nameof(AutomationIdSuffix),
            typeof(object),
            typeof(MarkdownMessageControl),
            new PropertyMetadata(null));

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public object? AutomationIdSuffix
    {
        get => GetValue(AutomationIdSuffixProperty);
        set => SetValue(AutomationIdSuffixProperty, value);
    }

    public MarkdownMessageControl()
    {
        InitializeComponent();

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _debounceTimer.Tick += OnDebounceTimerTick;

        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));

        PreviewMouseWheel += OnPreviewMouseWheel;

        RenderMarkdown(string.Empty);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var parent = this.FindAncestor<ScrollViewer>();
        if (parent is null) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this,
        });
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageControl control)
        {
            control.HandleMarkdownChanged((string)e.NewValue);
        }
    }

    private static void OnIsStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownMessageControl control && (bool)e.NewValue == false)
        {
            control._debounceTimer.Stop();
            control.RenderMarkdown(control.MarkdownText);
        }
    }

    private void HandleMarkdownChanged(string newText)
    {
        if (IsStreaming)
        {
            _pendingMarkdown = newText;
            if (!_debounceTimer.IsEnabled)
            {
                _debounceTimer.Start();
            }
        }
        else
        {
            RenderMarkdown(newText);
        }
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        if (_pendingMarkdown is not null)
        {
            RenderMarkdown(_pendingMarkdown);
            _pendingMarkdown = null;
        }
    }

    private void RenderMarkdown(string? markdown)
    {
        MarkdownViewer.Document = PiaMarkdownRenderer.Render(markdown ?? string.Empty);
    }

    public string GetSelectedText() =>
        MarkdownViewer.Selection?.Text?.Trim() ?? string.Empty;

    private void MarkdownViewer_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selectedText = GetSelectedText();
        var hasSelection = !string.IsNullOrWhiteSpace(selectedText);

        MarkdownViewer.ContextMenu!.Tag = selectedText;
        AddToPiiMenu.IsEnabled = hasSelection;
    }

    private void AddToPii_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;

        var category = menuItem.Tag as string ?? "Custom";
        var selectedText = MarkdownViewer.ContextMenu?.Tag as string ?? string.Empty;

        if (string.IsNullOrWhiteSpace(selectedText)) return;

        AddToPiiRequested?.Invoke(this, new PiiKeywordRequest(selectedText, category));
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        // In-app wikilinks navigate within the app (the Vault view), NOT the browser.
        if (WikiLinkScheme.TryGetTarget(e.Uri, out var target))
        {
            WikiLinkNavigate?.Invoke(this, target);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore failures to open links
        }
    }
}
