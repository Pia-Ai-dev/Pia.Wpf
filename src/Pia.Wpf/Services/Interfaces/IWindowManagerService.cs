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

    /// <summary>
    /// Opens the chat hosting the agent run <paramref name="runId"/>. A stale run (chat cascaded away)
    /// is retracted from Flow with a brief toast instead of dereferencing a missing chat (R17). Sync
    /// member; the async run-resolve runs fire-and-forget internally (F3).
    /// </summary>
    void ShowAgentRun(Guid runId);
    void ShowFirstRunWizard();
    void HideWindow(WindowMode mode);
    void HideAllWindows();
    void CloseAndDisposeAll();
    bool IsVisible(WindowMode mode);
    bool IsInForeground(WindowMode mode);
    bool CanDismissWithHotkey(WindowMode mode);
}
