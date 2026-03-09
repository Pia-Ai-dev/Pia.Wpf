using System.Windows.Controls;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class ModelDownloadContentDialog : ContentDialog
{
    private readonly IProgress<ModelDownloadProgress> _progress;

    public ModelDownloadContentDialog(
        ContentPresenter contentPresenter,
        string modelName,
        IProgress<ModelDownloadProgress> progress)
        : base(contentPresenter)
    {
        _progress = progress;
        InitializeComponent();

        ModelNameText.Text = $"Downloading {modelName}...";

        if (_progress is Progress<ModelDownloadProgress> progressImpl)
        {
            progressImpl.ProgressChanged += OnProgressChanged;
        }
    }

    private void OnProgressChanged(object? sender, ModelDownloadProgress e)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgressBar.Value = e.PercentComplete;
            ProgressText.Text = $"{e.PercentComplete}% of {FormatBytes(e.TotalBytes)}";
        });
    }

    public void UpdateProgress(int progress, long totalBytes)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgressBar.Value = progress;
            ProgressText.Text = $"{progress}% of {FormatBytes(totalBytes)}";
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}
