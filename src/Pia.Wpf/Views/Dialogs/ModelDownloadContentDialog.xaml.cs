using Pia.Localization;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class ModelDownloadContentDialog : ContentDialog
{
    private readonly IProgress<ModelDownloadProgress> _progress;
    private readonly string _modelName;
    private ModelDownloadPhase _currentPhase = ModelDownloadPhase.Downloading;

    public ModelDownloadContentDialog(
        ContentDialogHost dialogHost,
        string modelName,
        IProgress<ModelDownloadProgress> progress)
        : base(dialogHost)
    {
        _progress = progress;
        _modelName = modelName;
        InitializeComponent();

        ApplyPhase(ModelDownloadPhase.Downloading, 0, 0);

        if (_progress is Progress<ModelDownloadProgress> progressImpl)
        {
            progressImpl.ProgressChanged += OnProgressChanged;
        }
    }

    private void OnProgressChanged(object? sender, ModelDownloadProgress e)
    {
        Dispatcher.Invoke(() => ApplyPhase(e.Phase, e.PercentComplete, e.TotalBytes));
    }

    private void ApplyPhase(ModelDownloadPhase phase, int pct, long totalBytes)
    {
        if (phase != _currentPhase)
        {
            _currentPhase = phase;
            DownloadingPanel.Visibility = phase == ModelDownloadPhase.Downloading
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            ExtractingPanel.Visibility = phase == ModelDownloadPhase.Extracting
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        switch (phase)
        {
            case ModelDownloadPhase.Downloading:
                ModelNameText.Text = string.Format(LocalizationSource.Instance["Dialog_ModelDownload_Downloading"], _modelName);
                DownloadProgressBar.Value = pct;
                ProgressText.Text = totalBytes > 0
                    ? $"{pct}% of {FormatBytes(totalBytes)}"
                    : $"{pct}%";
                break;

            case ModelDownloadPhase.Extracting:
                ModelNameText.Text = string.Format(LocalizationSource.Instance["Dialog_ModelDownload_Extracting"], _modelName);
                break;
        }
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
