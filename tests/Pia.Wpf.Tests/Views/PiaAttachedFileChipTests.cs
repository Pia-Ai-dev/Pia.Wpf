using System.Windows;
using System.Windows.Controls;
using Pia.Controls.Chat;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The chip's whole job is the saved/unsaved split: only a file that was copied into the assistant-files
/// sandbox can be reopened, so an unsaved one must not ship buttons that cannot work.
/// </summary>
[Collection("WpfApplicationStatic")]
public class PiaAttachedFileChipTests
{
    private static (Visibility Open, Visibility Reveal, Visibility Inert) Regions(string? savedRelativePath) =>
        WpfStaHost.Run(() =>
        {
            var chip = new PiaAttachedFileChip
            {
                FileName = "report.docx",
                SavedRelativePath = savedRelativePath,
            };
            chip.Measure(new Size(500, 100));

            var buttons = Descendants(chip).OfType<Button>().ToList();
            var inert = Descendants(chip).OfType<StackPanel>()
                .First(sp => sp.ToolTip is not null);
            return (buttons[0].Visibility, buttons[1].Visibility, inert.Visibility);
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public void SavedFile_OffersOpenAndReveal()
    {
        var (open, reveal, inert) = Regions("Playground/report.docx");

        Assert.Equal(Visibility.Visible, open);
        Assert.Equal(Visibility.Visible, reveal);
        Assert.NotEqual(Visibility.Visible, inert);
    }

    [Fact]
    public void UnsavedFile_IsAnInertNamePill()
    {
        var (open, reveal, inert) = Regions(null);

        Assert.NotEqual(Visibility.Visible, open);
        Assert.NotEqual(Visibility.Visible, reveal);
        Assert.Equal(Visibility.Visible, inert);
    }
}
