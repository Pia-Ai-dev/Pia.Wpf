using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pia.Models;

namespace Pia.Controls.Memory;

public partial class PiaJsonView : UserControl
{
    private MemoryObject? _bound;

    public PiaJsonView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Render();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_bound is not null)
            _bound.PropertyChanged -= OnMemoryPropertyChanged;
        _bound = DataContext as MemoryObject;
        if (_bound is not null)
            _bound.PropertyChanged += OnMemoryPropertyChanged;
        Render();
    }

    private void OnMemoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemoryObject.Data) or nameof(MemoryObject.Label))
            Render();
    }

    private void Render()
    {
        JsonHost.Inlines.Clear();
        if (_bound is null) return;

        RenderJson(_bound.Data ?? string.Empty);
    }

    private void RenderJson(string raw)
    {
        string pretty;
        try
        {
            var node = JsonNode.Parse(raw);
            pretty = node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? raw;
        }
        catch
        {
            JsonHost.Inlines.Add(new Run(raw));
            return;
        }

        var stringBrush = TryBrush("PiaSuccessBrush") ?? Brushes.SeaGreen;
        var numberBrush = TryBrush("WarnBrush") ?? Brushes.DarkOrange;
        var keywordBrush = TryBrush("TypeProfileFgBrush") ?? Brushes.MediumPurple;
        var propBrush = TryBrush("PiaAccentBrush") ?? Brushes.SteelBlue;
        var puncBrush = TryBrush("TextMutedBrush") ?? Brushes.Gray;

        var i = 0;
        while (i < pretty.Length)
        {
            var c = pretty[i];
            if (c == '"')
            {
                var start = i;
                i++;
                while (i < pretty.Length && pretty[i] != '"')
                {
                    if (pretty[i] == '\\' && i + 1 < pretty.Length) i += 2;
                    else i++;
                }
                if (i < pretty.Length) i++;
                var token = pretty.Substring(start, i - start);
                var isKey = false;
                var k = i;
                while (k < pretty.Length && (pretty[k] == ' ' || pretty[k] == '\t')) k++;
                if (k < pretty.Length && pretty[k] == ':') isKey = true;
                JsonHost.Inlines.Add(new Run(token) { Foreground = isKey ? propBrush : stringBrush });
            }
            else if (c == 't' || c == 'f' || c == 'n')
            {
                var start = i;
                while (i < pretty.Length && char.IsLetter(pretty[i])) i++;
                var token = pretty.Substring(start, i - start);
                if (token is "true" or "false" or "null")
                    JsonHost.Inlines.Add(new Run(token) { Foreground = keywordBrush });
                else
                    JsonHost.Inlines.Add(new Run(token));
            }
            else if (c == '-' || char.IsDigit(c))
            {
                var start = i;
                if (c == '-') i++;
                while (i < pretty.Length && (char.IsDigit(pretty[i]) || pretty[i] == '.' || pretty[i] == 'e' || pretty[i] == 'E' || pretty[i] == '+' || pretty[i] == '-')) i++;
                var token = pretty.Substring(start, i - start);
                JsonHost.Inlines.Add(new Run(token) { Foreground = numberBrush });
            }
            else if (c == '{' || c == '}' || c == '[' || c == ']' || c == ',' || c == ':')
            {
                JsonHost.Inlines.Add(new Run(c.ToString()) { Foreground = puncBrush });
                i++;
            }
            else
            {
                JsonHost.Inlines.Add(new Run(c.ToString()));
                i++;
            }
        }
    }

    private static Brush? TryBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush;
}
