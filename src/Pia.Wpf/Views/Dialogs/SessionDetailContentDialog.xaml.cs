using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class SessionDetailContentDialog : ContentDialog
{
    public SessionDetailContentDialog(
        ContentDialogHost dialogHost,
        OptimizationSession session,
        IOutputService outputService)
        : base(dialogHost)
    {
        DataContext = new SessionDetailDialogViewModel(session, outputService);
        InitializeComponent();
    }
}

public partial class SessionDetailDialogViewModel : ObservableObject
{
    private readonly IOutputService _outputService;
    private readonly OptimizationSession _session;

    [ObservableProperty]
    private string _originalText;

    [ObservableProperty]
    private string _optimizedText;

    [ObservableProperty]
    private string? _templateName;

    [ObservableProperty]
    private string? _providerName;

    public string ProcessingTime
    {
        get
        {
            if (_session.ProcessingTimeMs <= 0)
                return "—";
            var time = TimeSpan.FromMilliseconds(_session.ProcessingTimeMs);
            return time.TotalSeconds < 1
                ? $"{time.TotalMilliseconds:F0}ms"
                : $"{time.TotalSeconds:F1}s";
        }
    }

    public string FormattedCreatedAt => _session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public SessionDetailDialogViewModel(OptimizationSession session, IOutputService outputService)
    {
        _session = session;
        _outputService = outputService;
        _originalText = session.OriginalText;
        _optimizedText = session.OptimizedText;
        _templateName = session.TemplateName;
        _providerName = session.ProviderName;
    }

    [RelayCommand]
    private async Task CopyOriginalAsync()
    {
        try
        {
            await _outputService.CopyToClipboardAsync(OriginalText);
        }
        catch
        {
            // Silently fail for clipboard operations
        }
    }

    [RelayCommand]
    private async Task CopyOptimizedAsync()
    {
        try
        {
            await _outputService.CopyToClipboardAsync(OptimizedText);
        }
        catch
        {
            // Silently fail for clipboard operations
        }
    }
}
