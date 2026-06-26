using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Services;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

/// <summary>
/// Drives the drill-down folder picker in the chat-title chip. Navigation is purely
/// relative (forward-slash separators, the stored convention); the empty string is the
/// sandbox root. Drilling into a folder <em>enters</em> it — it becomes the chosen working
/// directory and the list reveals only its immediate children (never deeper before entering).
/// Raises <see cref="WorkingDirectoryChosen"/> whenever the chosen directory changes so the
/// owner can re-point the active chat and refresh the pill.
/// </summary>
public sealed partial class WorkingDirectoryPickerViewModel : ObservableObject
{
    private readonly IWorkingDirectoryService _service;

    /// <summary>The currently-chosen relative path (forward slashes; <c>""</c> = sandbox root).</summary>
    [ObservableProperty]
    private string _currentRelativePath = string.Empty;

    /// <summary>Immediate child folder names of <see cref="CurrentRelativePath"/>.</summary>
    public ObservableCollection<string> Entries { get; } = [];

    /// <summary>Breadcrumb segments from root to the current path (the root crumb included).</summary>
    public ObservableCollection<WorkingDirectoryCrumb> Crumbs { get; } = [];

    /// <summary>True when the current folder has no listable child folders (drives the empty state).</summary>
    [ObservableProperty]
    private bool _isEmpty;

    public IRelayCommand<string> EnterCommand { get; }
    public IRelayCommand<int> JumpToCrumbCommand { get; }

    /// <summary>Raised with the new relative path (forward slashes; <c>""</c> = root) when the chosen directory changes.</summary>
    public event EventHandler<string>? WorkingDirectoryChosen;

    public WorkingDirectoryPickerViewModel(IWorkingDirectoryService service)
    {
        _service = service;
        EnterCommand = new RelayCommand<string>(ExecuteEnter);
        JumpToCrumbCommand = new RelayCommand<int>(ExecuteJumpToCrumb);
        // NOTE: do NOT enumerate here. This VM is constructed on the UI thread during the
        // chat-title chip's construction; ListSubfolders synchronously blocks on
        // ISettingsService.GetSettingsAsync (an async load), which would risk a UI-thread
        // stall at window-open. Enumeration is deferred to InitializeFrom/Refresh, which the
        // chip invokes when the picker popup is opened. Seed only the (offline) root crumb.
        RebuildCrumbs();
    }

    /// <summary>
    /// Re-initialize the picker at <paramref name="relativePath"/> (e.g. the active chat's
    /// working dir) WITHOUT raising <see cref="WorkingDirectoryChosen"/> — this reflects an
    /// external change, it is not a user selection.
    /// </summary>
    public void InitializeFrom(string? relativePath)
    {
        CurrentRelativePath = Normalize(relativePath);
        Refresh();
    }

    /// <summary>Repopulate <see cref="Entries"/> and <see cref="Crumbs"/> from the current path.</summary>
    public void Refresh()
    {
        Entries.Clear();
        foreach (var name in _service.ListSubfolders(CurrentRelativePath))
            Entries.Add(name);
        IsEmpty = Entries.Count == 0;
        RebuildCrumbs();
    }

    private void ExecuteEnter(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return;

        var next = string.IsNullOrEmpty(CurrentRelativePath)
            ? folderName
            : CurrentRelativePath + "/" + folderName;

        Choose(Normalize(next));
    }

    private void ExecuteJumpToCrumb(int index)
    {
        // index 0 = root; index n = the nth segment (1-based segments after root).
        if (index <= 0)
        {
            Choose(string.Empty);
            return;
        }

        var segments = SplitSegments(CurrentRelativePath);
        if (index >= segments.Length) return; // already at this level or beyond

        var target = string.Join('/', segments[..index]);
        Choose(Normalize(target));
    }

    private void Choose(string relativePath)
    {
        CurrentRelativePath = relativePath;
        Refresh();
        WorkingDirectoryChosen?.Invoke(this, relativePath);
    }

    private void RebuildCrumbs()
    {
        Crumbs.Clear();
        // Root crumb is always present (index 0).
        Crumbs.Add(new WorkingDirectoryCrumb(0, string.Empty, IsRoot: true));

        var segments = SplitSegments(CurrentRelativePath);
        for (var i = 0; i < segments.Length; i++)
            Crumbs.Add(new WorkingDirectoryCrumb(i + 1, segments[i], IsRoot: false));
    }

    private static string[] SplitSegments(string relativePath) =>
        string.IsNullOrEmpty(relativePath)
            ? []
            : relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string? relativePath)
    {
        var trimmed = relativePath?.Trim().Replace('\\', '/');
        // SplitSegments collapses empty segments (leading/trailing/doubled slashes).
        return string.IsNullOrEmpty(trimmed) ? string.Empty : string.Join('/', SplitSegments(trimmed));
    }
}
