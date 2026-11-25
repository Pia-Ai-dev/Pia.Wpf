using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed partial class ImageViewerViewModel : ObservableObject
{
    public const double MinZoom = 0.1, MaxZoom = 8.0, ZoomStep = 1.25;

    private readonly IReadOnlyList<ImageAttachment> _images;
    private readonly IClipboardService _clipboard;
    private readonly IFileDialogService _fileDialogs;
    private readonly ILocalizationService _localization;
    private readonly Wpf.Ui.ISnackbarService _snackbar;
    private readonly ILogger<ImageViewerViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageBytes), nameof(CurrentWidth), nameof(CurrentHeight), nameof(PositionText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    private int _currentIndex;

    [ObservableProperty]
    private bool _isFitToWindow = true;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private double _fitScale = 1.0;

    public ImageViewerViewModel(
        IReadOnlyList<ImageAttachment> images,
        int startIndex,
        IClipboardService clipboard,
        IFileDialogService fileDialogs,
        ILocalizationService localization,
        Wpf.Ui.ISnackbarService snackbar,
        ILogger<ImageViewerViewModel> logger)
    {
        _images = images;
        _clipboard = clipboard;
        _fileDialogs = fileDialogs;
        _localization = localization;
        _snackbar = snackbar;
        _logger = logger;
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, images.Count - 1));
    }

    public event EventHandler? CloseRequested;

    private ImageAttachment Current => _images[CurrentIndex];

    public byte[] CurrentImageBytes => Current.JpegBytes;
    public int CurrentWidth => Current.Width;
    public int CurrentHeight => Current.Height;
    public bool HasMany => _images.Count > 1;
    public string PositionText => $"{CurrentIndex + 1} / {_images.Count}";

    partial void OnCurrentIndexChanged(int value)
    {
        IsFitToWindow = true;
        Zoom = 1.0;
    }

    private bool CanGoPrevious() => CurrentIndex > 0;
    private bool CanGoNext() => CurrentIndex < _images.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => CurrentIndex--;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => CurrentIndex++;

    [RelayCommand]
    private void ZoomIn() => SetManualZoom(ZoomBase() * ZoomStep);

    [RelayCommand]
    private void ZoomOut() => SetManualZoom(ZoomBase() / ZoomStep);

    [RelayCommand]
    private void ActualSize() => SetManualZoom(1.0);

    [RelayCommand]
    private void FitToWindow() => IsFitToWindow = true;

    // Starting from the fit scale keeps the first click from jumping to 100 %.
    private double ZoomBase() => IsFitToWindow ? FitScale : Zoom;

    private void SetManualZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        IsFitToWindow = false;
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            _clipboard.SetImage(CurrentImageBytes);
        }
        catch (ExternalException ex)
        {
            _logger.LogWarning("Copying the image failed: {ExceptionType}", ex.GetType().Name);
            ShowCaution("Msg_ImageViewer_CopyFailed");
        }
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        var name = Current.SourcePath is { } source
            ? Path.GetFileNameWithoutExtension(source) + ".jpg"
            : "image.jpg";
        var path = _fileDialogs.PromptSaveFile(
            _localization["ImageViewer_Save_Tooltip"], "JPEG (*.jpg)|*.jpg", name, null);
        if (path is null)
            return;

        try
        {
            await File.WriteAllBytesAsync(path, CurrentImageBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Saving the image failed: {ExceptionType}", ex.GetType().Name);
            _logger.SensitiveDebug("Saving the image to {Path} failed", path);
            ShowCaution("Msg_ImageViewer_SaveFailed");
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ShowCaution(string messageKey) =>
        _snackbar.Show(
            _localization["Msg_Warning"],
            _localization[messageKey],
            Wpf.Ui.Controls.ControlAppearance.Caution,
            null,
            TimeSpan.FromSeconds(4));
}
