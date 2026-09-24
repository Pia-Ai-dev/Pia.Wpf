using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Services.Screen;

namespace Pia.ViewModels.Models;

/// <summary>Lives here rather than in <c>Pia.ViewModels</c> because it is the only part of the picker that
/// touches a bitmap.</summary>
public sealed partial class ScreenCaptureTargetViewModel : ObservableObject
{
    public ScreenCaptureTargetViewModel(CaptureTarget target, string label, string detail, string automationKey)
    {
        Target = target;
        Label = label;
        Detail = detail;
        AutomationKey = automationKey;
    }

    public CaptureTarget Target { get; }

    public bool IsMonitor => Target.Kind == CaptureTargetKind.Monitor;

    public bool IsMinimized => Target.IsMinimized;

    /// <summary>Shown and read out by UIA; a window title is user content, so it never reaches a log.</summary>
    public string Label { get; }

    public string Detail { get; }

    /// <summary>Unique per row: the handle for a window, the device name for a display, which every
    /// monitor shares as handle zero.</summary>
    public string AutomationKey { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isPreviewPending = true;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private string? _previewHint;

    internal void ApplyPreview(CaptureResult success)
    {
        if (success.Bitmap is null) return;

        Thumbnail = ScreenCaptureThumbnails.Create(success.Bitmap, ScreenCaptureThumbnails.PickerMaxEdge);
        PreviewHint = null;
        IsPreviewPending = false;
    }

    internal void MarkUnavailable(string hint)
    {
        Thumbnail = null;
        PreviewHint = hint;
        IsPreviewPending = false;
    }
}
