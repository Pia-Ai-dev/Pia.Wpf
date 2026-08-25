using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Models;

namespace Pia.Controls.Chat;

public partial class PiaSourceChip : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(SourceRefKind), typeof(PiaSourceChip),
            new PropertyMetadata(SourceRefKind.Web));

    /// <summary>Invoked with <see cref="Reference"/> for the in-app kinds; a web chip opens its own URL.</summary>
    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(PiaSourceChip));

    public static readonly DependencyProperty ReferenceProperty =
        DependencyProperty.Register(nameof(Reference), typeof(SourceRef), typeof(PiaSourceChip));

    public static readonly DependencyProperty NumberProperty =
        DependencyProperty.Register(nameof(Number), typeof(int), typeof(PiaSourceChip),
            new PropertyMetadata(0));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(string), typeof(PiaSourceChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(string), typeof(PiaSourceChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.Register(nameof(Url), typeof(string), typeof(PiaSourceChip),
            new PropertyMetadata(null));

    public int Number
    {
        get => (int)GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Meta
    {
        get => (string?)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public SourceRefKind Kind
    {
        get => (SourceRefKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public SourceRef? Reference
    {
        get => (SourceRef?)GetValue(ReferenceProperty);
        set => SetValue(ReferenceProperty, value);
    }

    public PiaSourceChip() => InitializeComponent();

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (Kind != SourceRefKind.Web)
        {
            // Unbound outside the live chat — the history inspector hosts the same message control.
            if (Reference is { } reference && OpenCommand?.CanExecute(reference) == true)
                OpenCommand.Execute(reference);
            return;
        }

        var url = Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // ShellExecute can fail if no default handler is registered for
            // http/https — silently swallow; the chip stays visible so the
            // user can copy the URL via tooltip.
        }
    }
}
