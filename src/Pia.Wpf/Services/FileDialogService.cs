using System.IO;
using Microsoft.Win32;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? PromptSaveFile(string title, string filter, string defaultFileName, string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            try { Directory.CreateDirectory(initialDirectory); }
            catch { /* dialog will fall back to last-used folder */ }
        }

        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true,
            AddExtension = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PromptSelectFolder(string title, string? initialDirectory)
    {
        var dlg = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
