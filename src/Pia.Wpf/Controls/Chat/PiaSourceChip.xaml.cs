using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Chat;

public partial class PiaSourceChip : UserControl
{
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

    public PiaSourceChip() => InitializeComponent();

    private void OnClick(object sender, RoutedEventArgs e)
    {
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
