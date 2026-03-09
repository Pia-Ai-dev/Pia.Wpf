using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITrayIconService
{
    void Initialize();
    void UpdateHotkey(WindowMode mode, KeyboardShortcut? shortcut);
}
