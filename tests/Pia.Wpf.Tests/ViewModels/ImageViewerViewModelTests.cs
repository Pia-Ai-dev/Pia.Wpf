using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class ImageViewerViewModelTests
{
    private readonly IClipboardService _clipboard = Substitute.For<IClipboardService>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly Wpf.Ui.ISnackbarService _snackbar = Substitute.For<Wpf.Ui.ISnackbarService>();
    private readonly List<ImageAttachment> _images = [Attachment(1), Attachment(2), Attachment(3)];

    public ImageViewerViewModelTests()
    {
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
    }

    [Fact]
    public void StartsOnTheRequestedImage_AndClampsAnOutOfRangeStart()
    {
        var vm = NewViewModel(_images, 1);
        Assert.Equal(_images[1].JpegBytes, vm.CurrentImageBytes);
        Assert.Equal("2 / 3", vm.PositionText);

        Assert.Equal(2, NewViewModel(_images, 9).CurrentIndex);
    }

    [Fact]
    public void PreviousAndNext_AreDisabledAtTheEnds()
    {
        var first = NewViewModel(_images, 0);
        Assert.False(first.PreviousCommand.CanExecute(null));
        Assert.True(first.NextCommand.CanExecute(null));

        var last = NewViewModel(_images, 2);
        Assert.True(last.PreviousCommand.CanExecute(null));
        Assert.False(last.NextCommand.CanExecute(null));
    }

    [Fact]
    public void ASingleImage_HasNoNavigation()
    {
        var vm = NewViewModel([_images[0]], 0);

        Assert.False(vm.HasMany);
        Assert.False(vm.PreviousCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Next_ResetsToFit_AndRaisesTheCurrentImage()
    {
        var vm = NewViewModel(_images, 0);
        vm.ZoomInCommand.Execute(null);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.NextCommand.Execute(null);

        Assert.True(vm.IsFitToWindow);
        Assert.Equal(1.0, vm.Zoom);
        Assert.Contains(nameof(ImageViewerViewModel.CurrentImageBytes), raised);
        Assert.Equal(_images[1].JpegBytes, vm.CurrentImageBytes);
    }

    [Fact]
    public void Zoom_IsClampedBetweenMinAndMax()
    {
        var vm = NewViewModel(_images, 0);

        for (var i = 0; i < 30; i++)
            vm.ZoomInCommand.Execute(null);
        Assert.Equal(ImageViewerViewModel.MaxZoom, vm.Zoom);

        for (var i = 0; i < 30; i++)
            vm.ZoomOutCommand.Execute(null);
        Assert.Equal(ImageViewerViewModel.MinZoom, vm.Zoom);
    }

    [Fact]
    public void ZoomIn_FromFit_StartsAtTheFitScale()
    {
        var vm = NewViewModel(_images, 0);
        vm.FitScale = 0.5;

        vm.ZoomInCommand.Execute(null);

        Assert.Equal(0.625, vm.Zoom, 10);
        Assert.False(vm.IsFitToWindow);
    }

    [Fact]
    public void Copy_PutsTheCurrentBytesOnTheClipboard()
    {
        var vm = NewViewModel(_images, 1);

        vm.CopyCommand.Execute(null);

        _clipboard.Received(1).SetImage(_images[1].JpegBytes);
    }

    [Fact]
    public void Copy_WhenTheClipboardIsLocked_WarnsAndStaysOpen()
    {
        _clipboard.When(c => c.SetImage(Arg.Any<byte[]>())).Do(_ => throw new ExternalException("locked"));
        var vm = NewViewModel(_images, 0);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CopyCommand.Execute(null);

        _snackbar.Received(1).Show(
            Arg.Any<string>(), "Msg_ImageViewer_CopyFailed",
            Arg.Any<Wpf.Ui.Controls.ControlAppearance>(), Arg.Any<Wpf.Ui.Controls.IconElement?>(), Arg.Any<TimeSpan>());
        Assert.False(closed);
    }

    [Fact]
    public async Task SaveAs_WritesTheStoredBytes_ToThePromptedPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pia-viewer-{Guid.NewGuid():N}.jpg");
        _fileDialogs.PromptSaveFile(default!, default!, default!, default).ReturnsForAnyArgs(path);
        var vm = NewViewModel(_images, 2);
        try
        {
            await vm.SaveAsCommand.ExecuteAsync(null);

            Assert.Equal(_images[2].JpegBytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAs_Cancelled_WritesNothing()
    {
        _fileDialogs.PromptSaveFile(default!, default!, default!, default).ReturnsForAnyArgs((string?)null);
        var vm = NewViewModel(_images, 0);

        await vm.SaveAsCommand.ExecuteAsync(null);

        _snackbar.DidNotReceiveWithAnyArgs().Show(default!, default!, default, default, default);
    }

    [Fact]
    public void SaveAs_ProposesTheSourceFileName_WithAJpgExtension()
    {
        var withSource = Attachment(4, "C:\\x\\Holiday.png");
        _fileDialogs.PromptSaveFile(default!, default!, default!, default).ReturnsForAnyArgs((string?)null);
        var vm = NewViewModel([withSource], 0);

        vm.SaveAsCommand.Execute(null);

        _fileDialogs.Received(1).PromptSaveFile(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Is<string>("Holiday.jpg"),Arg.Is<string?>(d => d == null));
    }

    [Fact]
    public void Close_RaisesCloseRequested()
    {
        var vm = NewViewModel(_images, 0);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }

    private ImageViewerViewModel NewViewModel(IReadOnlyList<ImageAttachment> images, int start) =>
        new(images, start, _clipboard, _fileDialogs, _localization, _snackbar,
            NullLogger<ImageViewerViewModel>.Instance);

    private static ImageAttachment Attachment(byte marker, string? sourcePath = null)
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        return new ImageAttachment
        {
            JpegBytes = [marker, marker, marker],
            MimeType = "image/jpeg",
            Width = 1,
            Height = 1,
            Thumbnail = bitmap,
            SourcePath = sourcePath,
        };
    }
}
