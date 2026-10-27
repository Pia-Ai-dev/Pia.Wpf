using System.Windows;
using System.Windows.Controls;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;

namespace Pia.Views.Dialogs;

/// <summary>Wpf.Ui renders the close button even when its text is empty, so this collapses it and its column.</summary>
internal static class ContentDialogCloseButton
{
    public static void HideOnLoad(ContentDialog dialog) => dialog.Loaded += (_, _) => Collapse(dialog);

    public static void Collapse(ContentDialog dialog)
    {
        if (dialog.Template?.FindName("CloseButton", dialog) is UIElement button)
        {
            button.Visibility = Visibility.Collapsed;
        }

        foreach (var name in new[] { "CloseColumn", "SecondSpacer" })
        {
            if (dialog.Template?.FindName(name, dialog) is ColumnDefinition column)
            {
                column.Width = new GridLength(0);
            }
        }
    }
}
