using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls;

/// <summary>
/// Measures its child with infinite width to determine natural content width,
/// then re-measures at the capped width so text wrapping still works.
/// This makes FlowDocument-based controls shrink-wrap to their content.
/// </summary>
public class ShrinkWrapDecorator : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
            return new Size();

        // First pass: measure with infinite width to get natural content width
        Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        var naturalWidth = Child.DesiredSize.Width;

        // Cap at available width
        var finalWidth = Math.Min(naturalWidth, constraint.Width);

        // Second pass: measure at final width for correct height (text wrapping)
        Child.Measure(new Size(finalWidth, constraint.Height));

        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
