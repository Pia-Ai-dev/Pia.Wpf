using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITrayIconService
{
    void Initialize();
    void UpdateHotkey(WindowMode mode, KeyboardShortcut? shortcut);
    void UpdateFastPathHotkey(KeyboardShortcut? shortcut);

    /// <summary>Tears down the windows and the tray registration. Front-loaded here because App.OnExit is
    /// async void and races process death.</summary>
    void PrepareForExit();
}
