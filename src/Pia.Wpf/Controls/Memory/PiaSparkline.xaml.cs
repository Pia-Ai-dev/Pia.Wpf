using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pia.Controls.Memory;

public partial class PiaSparkline : UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(PiaSparkline),
            new PropertyMetadata(Array.Empty<double>(), OnValuesChanged));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public PiaSparkline()
    {
        InitializeComponent();
        Surface.SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PiaSparkline)d).Rebuild();

    private void Rebuild()
    {
        var values = Values;
        var w = Surface.ActualWidth;
        var h = Surface.ActualHeight;

        if (values is null || values.Count < 2 || w <= 0 || h <= 0)
        {
            LinePath.Data = null;
            AreaPath.Data = null;
            return;
        }

        double max = 0;
        foreach (var v in values) if (v > max) max = v;
        if (max <= 0) max = 1;

        var stepX = w / (values.Count - 1);
        var inv = CultureInfo.InvariantCulture;

        var line = new StringBuilder();
        var area = new StringBuilder();
        area.Append("M 0,").Append(h.ToString("0.##", inv));

        for (int i = 0; i < values.Count; i++)
        {
            var x = i * stepX;
            var y = h - (values[i] / max) * (h - 2) - 1;
            if (i == 0) line.Append('M').Append(' ');
            else line.Append('L').Append(' ');
            line.Append(x.ToString("0.##", inv)).Append(',').Append(y.ToString("0.##", inv)).Append(' ');
            area.Append(" L ").Append(x.ToString("0.##", inv)).Append(',').Append(y.ToString("0.##", inv));
        }
        area.Append(" L ").Append(w.ToString("0.##", inv)).Append(',').Append(h.ToString("0.##", inv)).Append(" Z");

        LinePath.Data = Geometry.Parse(line.ToString());
        AreaPath.Data = Geometry.Parse(area.ToString());
    }
}
