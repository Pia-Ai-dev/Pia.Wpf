using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls;

/// <summary>
/// Lays its children out in up to <see cref="MaxColumns"/> equal-width columns. The column count is
/// derived from the available width — each column is at least <see cref="MinColumnWidth"/> wide — so
/// the layout reflows responsively as the window resizes. Items flow into the currently-shortest
/// column (masonry) so cards of differing heights pack tightly without large gaps.
///
/// Reusable as an <c>ItemsPanel</c> for card lists (personas use 3 columns, optimize templates 2).
/// Requires a width-constrained parent: host it in a <see cref="ScrollViewer"/> with
/// <c>HorizontalScrollBarVisibility="Disabled"</c>, otherwise it is measured with infinite width and
/// falls back to <see cref="MaxColumns"/> columns at <see cref="MinColumnWidth"/>.
/// </summary>
public class ColumnsPanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Maximum number of columns to ever produce, however wide the panel gets.</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Minimum width a column may shrink to before the column count is reduced.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>Horizontal gap between columns.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Vertical gap between stacked items in a column.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private int ResolveColumnCount(double availableWidth)
    {
        var max = Math.Max(1, MaxColumns);
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0)
            return max;

        var spacing = Math.Max(0, ColumnSpacing);
        var minWidth = Math.Max(1, MinColumnWidth);
        var fit = (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing));
        return Math.Clamp(fit, 1, max);
    }

    private double ResolveColumnWidth(double availableWidth, int columns)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0)
            return Math.Max(1, MinColumnWidth);

        var spacing = Math.Max(0, ColumnSpacing);
        return Math.Max(1, (availableWidth - (columns - 1) * spacing) / columns);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ResolveColumnCount(availableSize.Width);
        var columnWidth = ResolveColumnWidth(availableSize.Width, columns);
        var rowSpacing = Math.Max(0, RowSpacing);

        var columnHeights = new double[columns];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            var target = ShortestColumn(columnHeights);
            if (columnHeights[target] > 0)
                columnHeights[target] += rowSpacing;
            columnHeights[target] += child.DesiredSize.Height;
        }

        var totalWidth = double.IsInfinity(availableSize.Width)
            ? columns * columnWidth + (columns - 1) * Math.Max(0, ColumnSpacing)
            : availableSize.Width;
        var totalHeight = columnHeights.Length == 0 ? 0 : columnHeights.Max();
        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ResolveColumnCount(finalSize.Width);
        var columnWidth = ResolveColumnWidth(finalSize.Width, columns);
        var spacing = Math.Max(0, ColumnSpacing);
        var rowSpacing = Math.Max(0, RowSpacing);

        var columnHeights = new double[columns];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var target = ShortestColumn(columnHeights);
            var top = columnHeights[target];
            if (top > 0)
                top += rowSpacing;
            var left = target * (columnWidth + spacing);
            child.Arrange(new Rect(left, top, columnWidth, child.DesiredSize.Height));
            columnHeights[target] = top + child.DesiredSize.Height;
        }

        return finalSize;
    }

    private static int ShortestColumn(double[] heights)
    {
        var index = 0;
        for (var i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[index])
                index = i;
        }
        return index;
    }
}
