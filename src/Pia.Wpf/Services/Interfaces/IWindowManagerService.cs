using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IWindowManagerService
{
    bool HasOpenWindows { get; }

    event EventHandler<ManagedWindow>? WindowOpened;
    event EventHandler<ManagedWindow>? WindowClosed;
    event EventHandler? WindowVisibilityChanged;

    void ShowWindow(WindowMode mode);
    Task<IOptimizeFastPathHandle> ShowOptimizeAndGetViewModelAsync();
    void ShowWindowWithText(WindowMode mode, string text);
    void ShowWindowWithSelection(WindowMode mode, string capturedText);
    void ShowAssistantChat(Guid chatId);
    void ShowFirstRunWizard();
    void HideWindow(WindowMode mode);
    void HideAllWindows();
    void CloseAndDisposeAll();
    bool IsVisible(WindowMode mode);
    bool IsInForeground(WindowMode mode);
    bool CanDismissWithHotkey(WindowMode mode);
}
