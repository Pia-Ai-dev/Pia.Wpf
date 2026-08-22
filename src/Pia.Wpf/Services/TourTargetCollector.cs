using System.Windows;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class TourTargetCollector : ITourTargetCollector
{
    private readonly IUiDispatcher _uiDispatcher;

    public TourTargetCollector(IUiDispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
    }

    public async Task<TourTargetScan> CollectActiveWindowAsync()
    {
        var scan = TourTargetScan.Empty;
        await _uiDispatcher.PostAsync(() => scan = ScanActiveWindow());
        return scan;
    }

    private static TourTargetScan ScanActiveWindow()
    {
        var app = Application.Current;
        if (app is null)
            return TourTargetScan.Empty;

        var window = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        return window is null ? TourTargetScan.Empty : TourTargetWalker.Collect(window);
    }
}
