using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Pia.Controls.Markdown;

public partial class CodeBlockControl : UserControl
{
    public static readonly DependencyProperty LanguageLabelProperty =
        DependencyProperty.Register(
            nameof(LanguageLabel),
            typeof(string),
            typeof(CodeBlockControl),
            new PropertyMetadata(string.Empty));

    public string LanguageLabel
    {
        get => (string)GetValue(LanguageLabelProperty);
        set => SetValue(LanguageLabelProperty, value);
    }

    private string _rawCode = string.Empty;
    private DispatcherTimer? _copyResetTimer;

    public CodeBlockControl()
    {
        InitializeComponent();
    }

    public void SetContent(string rawCode, IEnumerable<Run> runs)
    {
        _rawCode = rawCode ?? string.Empty;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            LineHeight = double.NaN,
        };
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "CodeFontFamily");
        doc.SetResourceReference(FlowDocument.FontSizeProperty, "CodeFontSize");

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };

        foreach (var run in runs)
        {
            paragraph.Inlines.Add(run);
        }

        doc.Blocks.Add(paragraph);
        CodeViewer.Document = doc;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_rawCode);
        }
        catch
        {
            // Clipboard can transiently fail; tooltip text already indicates intent.
            return;
        }

        CopyIcon.Symbol = SymbolRegular.Checkmark24;

        _copyResetTimer?.Stop();
        _copyResetTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _copyResetTimer.Tick -= ResetIcon;
        _copyResetTimer.Tick += ResetIcon;
        _copyResetTimer.Start();
    }

    private void ResetIcon(object? sender, EventArgs e)
    {
        _copyResetTimer?.Stop();
        CopyIcon.Symbol = SymbolRegular.Copy16;
    }
}
