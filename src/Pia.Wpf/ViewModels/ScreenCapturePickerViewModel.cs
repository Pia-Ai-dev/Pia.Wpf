using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Screen;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>Lists what can be captured and previews it one frame at a time; the frame that is attached is a
/// fresh capture taken on confirm, never the preview.</summary>
public sealed partial class ScreenCapturePickerViewModel : ObservableObject
{
    private readonly IScreenCaptureService _screenCapture;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ScreenCapturePickerViewModel> _logger;

    private CancellationTokenSource? _previewCts;
    private bool _syncingSelection;

    public ScreenCapturePickerViewModel(
        IScreenCaptureService screenCapture,
        ILocalizationService localization,
        ILogger<ScreenCapturePickerViewModel> logger)
    {
        _screenCapture = screenCapture;
        _localization = localization;
        _logger = logger;
    }

    public ObservableCollection<ScreenCaptureTargetViewModel> MonitorTargets { get; } = [];

    public ObservableCollection<ScreenCaptureTargetViewModel> WindowTargets { get; } = [];

    /// <summary>Names a window for the allowlist instead of grabbing a frame: displays are left out, because
    /// a run nobody is watching may never be handed one.</summary>
    public bool NamesAWindow { get; init; }

    public string DialogTitle =>
        _localization[NamesAWindow ? "ScreenCapturePicker_PickTitle" : "ScreenCapturePicker_Title"];

    public string ConfirmText =>
        _localization[NamesAWindow ? "ScreenCapturePicker_Use" : "ScreenCapturePicker_Capture"];

    public string Hint =>
        _localization[NamesAWindow ? "ScreenCapturePicker_PickHint" : "ScreenCapturePicker_Hint"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(CanCapture))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCapture))]
    private bool _isCapturing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCapture))]
    private ScreenCaptureTargetViewModel? _selectedTarget;

    public bool HasMonitors => MonitorTargets.Count > 0;

    public bool HasWindows => WindowTargets.Count > 0;

    public bool IsEmpty => !IsLoading && MonitorTargets.Count == 0 && WindowTargets.Count == 0;

    public bool CanCapture => SelectedTarget is { IsMinimized: false } && !IsCapturing && !IsLoading;

    /// <summary>Completes when the lists are populated; the preview loop keeps running after it.</summary>
    internal Task PendingInitialization { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the preview loop has stopped, whether it finished or was cancelled.</summary>
    internal Task PendingPreviews { get; private set; } = Task.CompletedTask;

    public Task InitializeAsync()
    {
        var pending = LoadTargetsAsync();
        PendingInitialization = pending;
        return pending;
    }

    [RelayCommand]
    private Task RefreshAsync() => InitializeAsync();

    /// <summary>Null when nothing capturable is selected; a refusal comes back as a failed result, not an
    /// exception.</summary>
    public async Task<CaptureResult?> CaptureSelectedAsync(CancellationToken cancellationToken = default)
    {
        var row = SelectedTarget;
        if (row is null || row.IsMinimized) return null;

        // Two captures must never overlap: a nested display-affinity lease restores the wrong value and
        // leaves every Pia window hidden from every capture on the machine.
        Cancel();
        await PendingPreviews;

        IsCapturing = true;
        try
        {
            return await _screenCapture.CaptureAsync(row.Target, cancellationToken);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    /// <summary>Stops the preview loop; idempotent, and the caller invokes it when the dialog closes.</summary>
    public void Cancel()
    {
        try
        {
            _previewCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LoadTargetsAsync()
    {
        // A refresh cancels the running loop but cannot pull back the capture it is already inside, so the
        // next loop is chained behind it rather than started alongside it.
        var settling = PendingPreviews;
        Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        IsLoading = true;
        ClearRows();

        IReadOnlyList<CaptureTarget> targets;
        try
        {
            targets = await _screenCapture.EnumerateTargetsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsLoading = false;
            cts.Dispose();
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Screen target enumeration failed ({Type})", ex.GetType().Name);
            targets = [];
        }

        if (cts.IsCancellationRequested)
        {
            IsLoading = false;
            cts.Dispose();
            return;
        }

        var display = 0;
        foreach (var target in targets)
        {
            if (target.Kind == CaptureTargetKind.Monitor)
            {
                if (NamesAWindow) continue;
                display++;
                AddRow(MonitorTargets, new ScreenCaptureTargetViewModel(
                    target,
                    _localization.Format("ScreenCapturePicker_MonitorLabel", display),
                    MonitorDetail(target),
                    MonitorKey(target.MonitorDeviceId, display)));
            }
            else
            {
                AddRow(WindowTargets, new ScreenCaptureTargetViewModel(
                    target,
                    target.Title,
                    $"{target.ProcessName} · {target.Bounds.Width}×{target.Bounds.Height}",
                    target.Hwnd.ToString(CultureInfo.InvariantCulture)));
            }
        }

        IsLoading = false;
        SelectFirstCapturableWindow();
        RaiseListProperties();
        _logger.LogDebug("Screen picker listed {Monitors} monitors and {Windows} windows",
            MonitorTargets.Count, WindowTargets.Count);

        PendingPreviews = LoadPreviewsAsync(settling, [.. MonitorTargets, .. WindowTargets], cts);
    }

    // Owns the source it was handed: every refresh mints one, and only the loop knows when nothing can
    // still register on the token.
    private async Task LoadPreviewsAsync(
        Task settling, IReadOnlyList<ScreenCaptureTargetViewModel> rows, CancellationTokenSource cts)
    {
        try
        {
            await settling;
            await LoadPreviewsAsync(rows, cts.Token);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task LoadPreviewsAsync(
        IReadOnlyList<ScreenCaptureTargetViewModel> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (row.IsMinimized)
            {
                row.MarkUnavailable(_localization["Msg_Screen_Minimized"]);
                continue;
            }

            CaptureResult result;
            try
            {
                result = await _screenCapture.CaptureAsync(row.Target, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Preview capture of a {Kind} target failed ({Type})",
                    row.Target.Kind, ex.GetType().Name);
                row.MarkUnavailable(_localization["Msg_Screen_NativeError"]);
                continue;
            }

            if (cancellationToken.IsCancellationRequested) return;

            if (result.IsSuccess)
            {
                row.ApplyPreview(result);
            }
            else
            {
                _logger.SensitiveDebug("Preview refused for window title: {Title}", row.Target.Title);
                row.MarkUnavailable(_localization[ScreenCaptureFailureText.KeyFor(result.Reason)!]);
            }
        }
    }

    private string MonitorDetail(CaptureTarget target)
    {
        var size = $"{target.Bounds.Width}×{target.Bounds.Height}";
        return target.IsPrimary
            ? $"{size} · {_localization["ScreenCapturePicker_PrimaryDisplay"]}"
            : size;
    }

    private static string MonitorKey(string deviceId, int ordinal)
    {
        var trimmed = ScreenTargetResolver.DeviceTail(deviceId);
        return string.IsNullOrWhiteSpace(trimmed)
            ? "MONITOR" + ordinal.ToString(CultureInfo.InvariantCulture)
            : trimmed;
    }

    // Arrow keys move a selection but cannot start one, so the list opens with one already on.
    private void SelectFirstCapturableWindow()
    {
        foreach (var row in WindowTargets)
        {
            if (row.IsMinimized) continue;
            row.IsSelected = true;
            return;
        }
    }

    private void AddRow(ObservableCollection<ScreenCaptureTargetViewModel> rows, ScreenCaptureTargetViewModel row)
    {
        row.PropertyChanged += OnRowPropertyChanged;
        rows.Add(row);
    }

    private void ClearRows()
    {
        foreach (var row in MonitorTargets) row.PropertyChanged -= OnRowPropertyChanged;
        foreach (var row in WindowTargets) row.PropertyChanged -= OnRowPropertyChanged;
        MonitorTargets.Clear();
        WindowTargets.Clear();
        SelectedTarget = null;
        RaiseListProperties();
    }

    private void RaiseListProperties()
    {
        OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanCapture));
    }

    // The RadioButton group already gives the view exclusivity; doing it here too is what makes the test
    // path and the hotkey path agree with it.
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScreenCaptureTargetViewModel.IsSelected)) return;
        if (sender is not ScreenCaptureTargetViewModel row) return;
        if (_syncingSelection) return;

        if (row.IsSelected)
        {
            _syncingSelection = true;
            try
            {
                foreach (var other in MonitorTargets)
                    if (!ReferenceEquals(other, row)) other.IsSelected = false;
                foreach (var other in WindowTargets)
                    if (!ReferenceEquals(other, row)) other.IsSelected = false;
            }
            finally
            {
                _syncingSelection = false;
            }

            SelectedTarget = row;
        }
        else if (ReferenceEquals(SelectedTarget, row))
        {
            SelectedTarget = null;
        }
    }
}
