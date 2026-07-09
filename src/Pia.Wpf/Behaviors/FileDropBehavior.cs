using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that turns a UIElement into a file drop target.
/// Fires <c>FilesDroppedCommand</c> with the dropped file paths and exposes a
/// read-only <c>IsDragOver</c> attached property the view can bind a visual overlay to.
/// </summary>
public static class FileDropBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(FileDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static readonly DependencyProperty FilesDroppedCommandProperty =
        DependencyProperty.RegisterAttached("FilesDroppedCommand", typeof(ICommand), typeof(FileDropBehavior));

    public static ICommand? GetFilesDroppedCommand(DependencyObject obj) => (ICommand?)obj.GetValue(FilesDroppedCommandProperty);
    public static void SetFilesDroppedCommand(DependencyObject obj, ICommand? value) => obj.SetValue(FilesDroppedCommandProperty, value);

    /// <summary>Comma-separated list of accepted extensions (with leading dot). Empty = accept any file.</summary>
    public static readonly DependencyProperty AcceptedExtensionsProperty =
        DependencyProperty.RegisterAttached("AcceptedExtensions", typeof(string), typeof(FileDropBehavior),
            new PropertyMetadata(string.Empty));

    public static string GetAcceptedExtensions(DependencyObject obj) => (string)obj.GetValue(AcceptedExtensionsProperty);
    public static void SetAcceptedExtensions(DependencyObject obj, string value) => obj.SetValue(AcceptedExtensionsProperty, value);

    private static readonly DependencyPropertyKey IsDragOverPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly("IsDragOver", typeof(bool), typeof(FileDropBehavior),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsDragOverProperty = IsDragOverPropertyKey.DependencyProperty;

    public static bool GetIsDragOver(DependencyObject obj) => (bool)obj.GetValue(IsDragOverProperty);
    private static void SetIsDragOver(DependencyObject obj, bool value) => obj.SetValue(IsDragOverPropertyKey, value);

    // DragEnter/DragLeave fire for every child element too. Balancing them with a counter
    // is more reliable than a bounds check, which misses edge-of-window exits (the cursor
    // reports a position still inside ActualWidth/Height, leaving the overlay stuck).
    private static readonly DependencyProperty DragCounterProperty =
        DependencyProperty.RegisterAttached("DragCounter", typeof(int), typeof(FileDropBehavior),
            new PropertyMetadata(0));

    private static int GetDragCounter(DependencyObject obj) => (int)obj.GetValue(DragCounterProperty);
    private static void SetDragCounter(DependencyObject obj, int value) => obj.SetValue(DragCounterProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        if ((bool)e.NewValue)
        {
            element.AllowDrop = true;
            element.PreviewDragEnter += OnDragEnter;
            element.PreviewDragOver += OnDragOver;
            element.PreviewDragLeave += OnDragLeave;
            element.PreviewDrop += OnDrop;
        }
        else
        {
            element.PreviewDragEnter -= OnDragEnter;
            element.PreviewDragOver -= OnDragOver;
            element.PreviewDragLeave -= OnDragLeave;
            element.PreviewDrop -= OnDrop;
            SetDragCounter(element, 0);
            SetIsDragOver(element, false);
        }
    }

    private static void OnDragEnter(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;

        SetDragCounter(target, GetDragCounter(target) + 1);

        if (TryAcceptDrag(target, e))
            SetIsDragOver(target, true);
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;
        TryAcceptDrag(target, e);
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;

        // DragEnter/DragLeave fire for child elements too; only clear once every enter
        // has been matched by a leave (i.e. the drag has truly left the target).
        var count = Math.Max(0, GetDragCounter(target) - 1);
        SetDragCounter(target, count);
        if (count == 0)
            SetIsDragOver(target, false);
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;

        SetDragCounter(target, 0);
        SetIsDragOver(target, false);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return;

        var accepted = FilterAccepted(paths, GetAcceptedExtensions(target));
        if (accepted.Count == 0) return;

        e.Handled = true;

        var command = GetFilesDroppedCommand(target);
        if (command is not null && command.CanExecute(accepted))
            command.Execute(accepted);
    }

    private static bool TryAcceptDrag(DependencyObject target, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return false;
        }

        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        var accepted = paths is null ? new List<string>() : FilterAccepted(paths, GetAcceptedExtensions(target));

        if (accepted.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return false;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        return true;
    }

    private static IReadOnlyList<string> FilterAccepted(IEnumerable<string> paths, string acceptedExtensions)
    {
        var filter = ParseExtensions(acceptedExtensions);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (filter.Count == 0)
            {
                result.Add(path);
                continue;
            }
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && filter.Contains(ext))
                result.Add(path);
        }
        return result;
    }

    private static HashSet<string> ParseExtensions(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part.StartsWith('.') ? part : "." + part);
        }
        return set;
    }
}
