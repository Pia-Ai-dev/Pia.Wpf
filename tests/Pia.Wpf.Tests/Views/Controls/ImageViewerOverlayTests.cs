using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Views.Controls;
using Xunit;

namespace Pia.Tests.Views.Controls;

[Collection("WpfApplicationStatic")]
public class ImageViewerOverlayTests
{
    [Fact]
    public void ASecondShowWhileOpen_ReturnsAtOnceAndLeavesTheOpenSessionAlone()
    {
        ImageViewerOverlay? overlay = null;
        ImageViewerViewModel? first = null, second = null;
        Task? firstShow = null, secondShow = null, third = null;
        WpfStaHost.Run(() =>
        {
            overlay = new ImageViewerOverlay();
            first = NewViewModel();
            second = NewViewModel();
            firstShow = overlay.ShowAsync(first);
            secondShow = overlay.ShowAsync(second);
            return 0;
        });

        Assert.True(secondShow!.IsCompleted);
        var context = WpfStaHost.Run(() =>
        {
            second!.CloseCommand.Execute(null);
            return overlay!.DataContext;
        });
        WpfStaHost.Pump();

        Assert.Same(first, context);
        Assert.False(firstShow!.IsCompleted);
        Assert.Equal(Visibility.Visible, WpfStaHost.Run(() => overlay!.Visibility));

        WpfStaHost.Run(() => { first!.CloseCommand.Execute(null); return 0; });
        WpfStaHost.Pump();

        Assert.True(firstShow.IsCompletedSuccessfully);
        var closed = WpfStaHost.Run(() => (overlay!.Visibility, overlay.DataContext));
        Assert.Equal(Visibility.Collapsed, closed.Visibility);
        Assert.Null(closed.DataContext);

        WpfStaHost.Run(() => { third = overlay!.ShowAsync(second!); return 0; });
        Assert.False(third!.IsCompleted);
        WpfStaHost.Run(() => { second!.CloseCommand.Execute(null); return 0; });
        WpfStaHost.Pump();
        Assert.True(third.IsCompletedSuccessfully);
    }

    [Fact]
    public void Traversal_CyclesInsideTheOverlay()
    {
        var modes = WpfStaHost.Run(() =>
        {
            var overlay = new ImageViewerOverlay();
            return (KeyboardNavigation.GetTabNavigation(overlay),
                KeyboardNavigation.GetControlTabNavigation(overlay),
                KeyboardNavigation.GetDirectionalNavigation(overlay));
        });

        Assert.Equal(KeyboardNavigationMode.Cycle, modes.Item1);
        Assert.Equal(KeyboardNavigationMode.Cycle, modes.Item2);
        Assert.Equal(KeyboardNavigationMode.Contained, modes.Item3);
    }

    private static ImageViewerViewModel NewViewModel()
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        var image = new ImageAttachment
        {
            JpegBytes = [1, 2, 3],
            MimeType = "image/jpeg",
            Width = 1,
            Height = 1,
            Thumbnail = bitmap,
        };
        return new ImageViewerViewModel([image], 0,
            Substitute.For<IClipboardService>(), Substitute.For<IFileDialogService>(),
            Substitute.For<ILocalizationService>(), Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<ImageViewerViewModel>.Instance);
    }
}
