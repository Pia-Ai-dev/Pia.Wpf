namespace Pia.Services.Interfaces;

public interface IFastPathOptimizer
{
    Task RunAsync();
}

public interface IOptimizeFastPathHandle
{
    Task ReadyAsync { get; }
    string InputText { get; set; }
    string OptimizedText { get; }
    bool IsComparisonView { get; }
    bool IsOptimizing { get; set; }
    void PrepareForFastPath();
    Task ShowOptimizingDialogAsync(CancellationToken cancellationToken);
    Task<bool> RunFastPathOptimizeAsync(CancellationToken externalCt = default);
    Task<bool> RunFastPathAcceptAsync();
    void ShowFastPathSnackbar(string messageKey);
}
