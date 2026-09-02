using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Helpers;
using Pia.Logging;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that turns a UIElement into a file drop target.
/// Fires <c>FilesDroppedCommand</c> with the dropped file paths and exposes a
/// read-only <c>IsDragOver</c> attached property the view can bind a visual overlay to.
/// A source whose items have no path — Outlook's message list — is materialised to disk first.
/// </summary>
public static class FileDropBehavior
{
    /// <summary>Beyond this we would be writing an unbounded amount of another process's data to disk on the
    /// UI thread; the ones past it are reported as failures rather than dropped in silence.</summary>
    private const int MaxMaterializedItems = 20;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(FileDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static readonly DependencyProperty FilesDroppedCommandProperty =
        DependencyProperty.RegisterAttached("FilesDroppedCommand", typeof(ICommand), typeof(FileDropBehavior));

    public static ICommand? GetFilesDroppedCommand(DependencyObject obj) => (ICommand?)obj.GetValue(FilesDroppedCommandProperty);
    public static void SetFilesDroppedCommand(DependencyObject obj, ICommand? value) => obj.SetValue(FilesDroppedCommandProperty, value);

    /// <summary>Fired with the name of an item the source offered but would not hand over. Only a
    /// materialised drop can reach it — a path either exists or the drag never carried it.</summary>
    public static readonly DependencyProperty DropFailedCommandProperty =
        DependencyProperty.RegisterAttached("DropFailedCommand", typeof(ICommand), typeof(FileDropBehavior));

    public static ICommand? GetDropFailedCommand(DependencyObject obj) => (ICommand?)obj.GetValue(DropFailedCommandProperty);
    public static void SetDropFailedCommand(DependencyObject obj, ICommand? value) => obj.SetValue(DropFailedCommandProperty, value);

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

    /// <summary>What the drag turned out to be, decided once on arrival.</summary>
    private enum DragVerdict
    {
        Reject,
        /// <summary>CF_HDROP — the paths either exist already or arrive with the drop.</summary>
        Paths,
        /// <summary>The items have no path; their bytes have to be pulled and written first.</summary>
        Descriptor,
    }

    private static readonly DependencyProperty DragVerdictProperty =
        DependencyProperty.RegisterAttached("DragVerdict", typeof(DragVerdict), typeof(FileDropBehavior),
            new PropertyMetadata(DragVerdict.Reject));

    private static DragVerdict GetDragVerdict(DependencyObject obj) => (DragVerdict)obj.GetValue(DragVerdictProperty);
    private static void SetDragVerdict(DependencyObject obj, DragVerdict value) => obj.SetValue(DragVerdictProperty, value);

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
            SetDragVerdict(element, DragVerdict.Reject);
        }
    }

    private static void OnDragEnter(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;
        if (HasNearerTarget(target, e.OriginalSource)) return;

        var count = GetDragCounter(target) + 1;
        SetDragCounter(target, count);
        if (count == 1)
        {
            LogDragArrival(e);
            SetDragVerdict(target, Evaluate(target, e));
        }

        if (TryAcceptDrag(target, e))
            SetIsDragOver(target, true);
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;
        if (HasNearerTarget(target, e.OriginalSource)) return;
        TryAcceptDrag(target, e);
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;
        if (HasNearerTarget(target, e.OriginalSource)) return;

        // DragEnter/DragLeave fire for child elements too; only clear once every enter
        // has been matched by a leave (i.e. the drag has truly left the target).
        var count = Math.Max(0, GetDragCounter(target) - 1);
        SetDragCounter(target, count);
        if (count == 0)
        {
            SetIsDragOver(target, false);
            SetDragVerdict(target, DragVerdict.Reject);
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject target) return;

        SetDragCounter(target, 0);
        SetIsDragOver(target, false);
        var verdict = GetDragVerdict(target);
        SetDragVerdict(target, DragVerdict.Reject);

        // Standing down for a nearer target happens AFTER the reset above: the drag is over either way,
        // and leaving the overlay latched would strand this target's hint on screen.
        if (HasNearerTarget(target, e.OriginalSource)) return;

        if (verdict == DragVerdict.Reject) return;

        IReadOnlyList<string> accepted;
        if (verdict == DragVerdict.Descriptor)
        {
            accepted = MaterializeShellDrop(target, e);
        }
        else
        {
            var paths = DropPaths(e);
            accepted = FilterAccepted(paths, GetAcceptedExtensions(target));

            // The drag claimed CF_HDROP and then produced nothing. New Outlook does exactly this: its drag
            // carries mailbox row keys, not a file. Say so, rather than swallowing the drop.
            if (paths.Length == 0) ReportFailure(target, null);
        }

        LogDropOutcome(verdict.ToString(), accepted.Count);

        if (accepted.Count == 0) return;

        e.Handled = true;

        var command = GetFilesDroppedCommand(target);
        if (command is not null && command.CanExecute(accepted))
            command.Execute(accepted);
    }

    private static bool TryAcceptDrag(DependencyObject target, DragEventArgs e)
    {
        var accepted = GetDragVerdict(target) != DragVerdict.Reject;

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return accepted;
    }

    /// <summary>
    /// Whether something between <paramref name="originalSource"/> and this target is itself an enabled
    /// drop target. The handlers tunnel, so an ancestor sees every drag first and would swallow it;
    /// it stands down here and lets the nearer target answer instead. A collapsed subtree is not
    /// hit-tested, so a hidden inner target never takes a drag away from its ancestor.
    /// </summary>
    internal static bool HasNearerTarget(DependencyObject target, object? originalSource)
    {
        var node = originalSource as DependencyObject;
        while (node is not null && node != target)
        {
            if (GetIsEnabled(node)) return true;

            // VisualTreeHelper throws on anything that is not a Visual, and a hit can land on a
            // ContentElement (a Run inside a TextBlock).
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>
    /// Decides once, when the drag arrives, what this drop would be. Both reads here cross a process boundary,
    /// and DragOver fires at mouse-move frequency, so the answer is cached rather than recomputed.
    /// </summary>
    private static DragVerdict Evaluate(DependencyObject target, DragEventArgs e)
    {
        ShellFileContentsMaterializer.LogDragFormats(e.Data, ResolveLogger());

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // A Chromium-hosted source (new Outlook is one) advertises CF_HDROP but writes the file only on
            // the drop itself, so there is nothing to filter yet. Accept, and filter the real paths in OnDrop.
            var paths = DropPaths(e);
            return paths.Length == 0 || FilterAccepted(paths, GetAcceptedExtensions(target)).Count > 0
                ? DragVerdict.Paths
                : DragVerdict.Reject;
        }

        if (!ShellFileContentsMaterializer.IsPresent(e.Data)) return DragVerdict.Reject;

        // The descriptor's names are the only extension a virtual drag carries, so the hover verdict comes
        // from them; the authoritative check still runs on the real path after materialisation.
        return AcceptedDescriptorItems(target, e).Count > 0 ? DragVerdict.Descriptor : DragVerdict.Reject;
    }

    private static string[] DropPaths(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) return paths;
        }
        catch (ExternalException)
        {
            // Falls through to the COM read below.
        }

        return [.. ShellFileContentsMaterializer.ReadDropPaths(e.Data, ResolveLogger())];
    }

    private static List<FileGroupDescriptorEntry> AcceptedDescriptorItems(DependencyObject target, DragEventArgs e)
    {
        var filter = ParseExtensions(GetAcceptedExtensions(target));
        return ShellFileContentsMaterializer.ReadDescriptor(e.Data)
            .Where(item => filter.Count == 0 || filter.Contains(Path.GetExtension(item.FileName)))
            .ToList();
    }

    /// <summary>
    /// Writes the dragged items to disk and hands back the paths. Runs synchronously on the drag's own thread:
    /// the source may tear its data object down the moment the drop returns.
    /// </summary>
    private static IReadOnlyList<string> MaterializeShellDrop(DependencyObject target, DragEventArgs e)
    {
        var logger = ResolveLogger();
        var items = AcceptedDescriptorItems(target, e);
        if (items.Count == 0) return [];

        var overflow = items.Skip(MaxMaterializedItems).ToList();
        if (overflow.Count > 0) items = items.Take(MaxMaterializedItems).ToList();

        string directory;
        try
        {
            directory = ShellDropCache.CreateDropDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Virtual-file drop: could not open the drop cache");
            ReportFailure(target, items[0].FileName);
            return [];
        }

        var result = ShellFileContentsMaterializer.Materialize(e.Data, items, directory, logger);
        var accepted = FilterAccepted(result.Paths, GetAcceptedExtensions(target));

        if (accepted.Count == 0) ShellDropCache.Delete(directory);

        var firstFailure = result.FailedNames.Concat(overflow.Select(i => i.FileName)).FirstOrDefault();
        if (firstFailure is not null) ReportFailure(target, firstFailure);

        return accepted;
    }

    /// <summary>
    /// A null name means the source handed over nothing at all, which needs different wording. Posted rather
    /// than called: a drop handler still runs inside the OLE drag loop, and a snackbar raised there is gone
    /// before the loop unwinds. The staging path gets away with it only because it is async and resumes after.
    /// </summary>
    private static void ReportFailure(DependencyObject target, string? fileName)
    {
        var command = GetDropFailedCommand(target);
        if (command is null || !command.CanExecute(fileName)) return;

        target.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, () => command.Execute(fileName));
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void LogDropOutcome(string verdict, int accepted) =>
        ResolveLogger().LogDebug("Drop verdict={Verdict} accepted={Accepted}", verdict, accepted);

    /// <summary>Dev-only: proves the drag reached us at all, and names the formats it carries. What a source
    /// advertises is the one thing no amount of reading settles.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void LogDragArrival(DragEventArgs e)
    {
        var logger = ResolveLogger();
        try
        {
            var formats = e.Data.GetFormats(autoConvert: false);
            logger.LogDebug("Drag arrived with {Count} formats: {Formats}", formats.Length, string.Join(", ", formats));
        }
        catch (ExternalException ex)
        {
            logger.LogWarning(ex, "Drag arrived but its formats could not be listed");
        }
    }

    private static ILogger ResolveLogger()
    {
        try
        {
            return Bootstrapper.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(FileDropBehavior).FullName!)
                ?? NullLogger.Instance;
        }
        catch (InvalidOperationException)
        {
            return NullLogger.Instance;
        }
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
