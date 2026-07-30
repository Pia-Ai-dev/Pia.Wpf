using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Pia.Controls;

/// <summary>
/// ImageIcon wrapped in a rounded tile, like an iOS app icon. Used for the
/// title-bar icon — ui:TitleBar.Icon requires an IconElement, so a templated
/// Style won't bind (IconElement is a FrameworkElement, not a Control).
/// </summary>
public class PiaTileImageIcon : ImageIcon
{
    protected override UIElement InitializeChildren()
    {
        var image = base.InitializeChildren();
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = image,
        };
        border.SetResourceReference(Border.BackgroundProperty, "BgCanvasBrush");
        if (image is FrameworkElement fe)
        {
            // The mark is tight-cropped to its ink, so the tile supplies the
            // inset that an app-icon lockup needs — roughly two thirds glyph.
            fe.Margin = new Thickness(3);
        }
        return border;
    }
}
