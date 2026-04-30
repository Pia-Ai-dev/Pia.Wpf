using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IWindowManagerService
{
    bool HasOpenWindows { get; }

    event EventHandler<ManagedWindow>? WindowOpened;
    event EventHandler<ManagedWindow>? WindowClosed;
    event EventHandler? WindowVisibilityChanged;

    void ShowWindow(WindowMode mode);
    void ShowWindowWithText(WindowMode mode, string text);
    void ShowWindowWithSelection(WindowMode mode, string capturedText);
    void ShowFirstRunWizard();
    void HideWindow(WindowMode mode);
    void HideAllWindows();
    void CloseAndDisposeAll();
    bool IsVisible(WindowMode mode);
    bool IsInForeground(WindowMode mode);
    bool CanDismissWithHotkey(WindowMode mode);
}
