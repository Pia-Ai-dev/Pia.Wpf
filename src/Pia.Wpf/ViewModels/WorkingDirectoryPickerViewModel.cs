using System.Collections.ObjectModel;
using System.IO;
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

    /// <summary>True while the inline "new folder" input row is shown.</summary>
    [ObservableProperty]
    private bool _isCreatingFolder;

    /// <summary>Text bound to the inline "new folder" input.</summary>
    [ObservableProperty]
    private string _newFolderName = string.Empty;

    /// <summary>Leaf name of the folder created by the last successful
    /// <see cref="ConfirmCreateFolderCommand"/> — the view reads it once to select/scroll the new
    /// row into view. Not observable; navigation does not clear it.</summary>
    public string? LastCreatedFolder { get; private set; }

    public IRelayCommand<string> EnterCommand { get; }
    public IRelayCommand<int> JumpToCrumbCommand { get; }
    public IRelayCommand BeginCreateFolderCommand { get; }
    public IRelayCommand ConfirmCreateFolderCommand { get; }
    public IRelayCommand CancelCreateFolderCommand { get; }

    /// <summary>Raised with the new relative path (forward slashes; <c>""</c> = root) when the chosen directory changes.</summary>
    public event EventHandler<string>? WorkingDirectoryChosen;

    public WorkingDirectoryPickerViewModel(IWorkingDirectoryService service)
    {
        _service = service;
        EnterCommand = new RelayCommand<string>(ExecuteEnter);
        JumpToCrumbCommand = new RelayCommand<int>(ExecuteJumpToCrumb);
        BeginCreateFolderCommand = new RelayCommand(ExecuteBeginCreateFolder);
        ConfirmCreateFolderCommand = new RelayCommand(ExecuteConfirmCreateFolder, CanConfirmCreateFolder);
        CancelCreateFolderCommand = new RelayCommand(ExecuteCancelCreateFolder);
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
        CancelFolderCreation();
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
        // Navigating (drill/ascend/jump) abandons any in-progress folder creation.
        CancelFolderCreation();
        CurrentRelativePath = relativePath;
        Refresh();
        WorkingDirectoryChosen?.Invoke(this, relativePath);
    }

    private void ExecuteBeginCreateFolder()
    {
        LastCreatedFolder = null;
        NewFolderName = string.Empty;
        IsCreatingFolder = true;
    }

    private void ExecuteCancelCreateFolder() => CancelFolderCreation();

    private void CancelFolderCreation()
    {
        IsCreatingFolder = false;
        NewFolderName = string.Empty;
    }

    private bool CanConfirmCreateFolder() => IsValidLeafName(NewFolderName);

    private void ExecuteConfirmCreateFolder()
    {
        var name = NewFolderName?.Trim();
        if (!IsValidLeafName(name)) return;

        var relative = string.IsNullOrEmpty(CurrentRelativePath) ? name! : CurrentRelativePath + "/" + name;

        // EnsureSubfolder validates sandbox containment / sensitive paths, is idempotent, and
        // returns null on a blocked or failed create. Keep the input open on failure so the user
        // can amend the name; creating never re-points the active chat (no WorkingDirectoryChosen).
        var created = _service.EnsureSubfolder(relative);
        if (created is null) return;

        IsCreatingFolder = false;
        NewFolderName = string.Empty;
        Refresh();
        // Highlight the new row. Match the actual on-disk entry — its casing can differ from what
        // was typed on a case-insensitive volume (an idempotent re-create doesn't rename), and the
        // view selects by ordinal string equality, so mismatched casing would silently no-op the
        // highlight. Fall back to the typed leaf. Set after Refresh so a navigation-triggered reset
        // can't clear it.
        var leaf = SplitSegments(created) is { Length: > 0 } parts ? parts[^1] : name;
        LastCreatedFolder = Entries.FirstOrDefault(e => string.Equals(e, leaf, StringComparison.OrdinalIgnoreCase)) ?? leaf;
    }

    partial void OnNewFolderNameChanged(string value) =>
        ConfirmCreateFolderCommand.NotifyCanExecuteChanged();

    /// <summary>A single sandbox-relative folder segment: non-empty, no path separators, no
    /// invalid filename chars, and not <c>.</c>/<c>..</c>. Deeper containment and sensitive-path
    /// checks are enforced by <see cref="IWorkingDirectoryService.EnsureSubfolder"/>.</summary>
    private static bool IsValidLeafName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        if (trimmed is "." or "..") return false;
        if (trimmed.Contains('/') || trimmed.Contains('\\')) return false;
        return trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
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
