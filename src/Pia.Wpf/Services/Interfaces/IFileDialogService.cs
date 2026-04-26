namespace Pia.Services.Interfaces;

/// <summary>
/// Thin abstraction over WPF's <c>Microsoft.Win32</c> file/folder dialogs so view-models
/// stay testable. Implementations may show modal UI and must be invoked from the UI thread.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Shows a Save File dialog. Returns the chosen full path or <c>null</c> if cancelled.
    /// </summary>
    string? PromptSaveFile(string title, string filter, string defaultFileName, string? initialDirectory);

    /// <summary>
    /// Shows a folder picker. Returns the chosen folder path or <c>null</c> if cancelled.
    /// </summary>
    string? PromptSelectFolder(string title, string? initialDirectory);
}
