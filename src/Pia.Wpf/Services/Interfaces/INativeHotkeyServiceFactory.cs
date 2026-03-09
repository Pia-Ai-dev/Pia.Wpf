using Pia.Models;

namespace Pia.Services.Interfaces;

public interface INativeHotkeyServiceFactory
{
    INativeHotkeyService? Create(int hotkeyId, KeyboardShortcut shortcut);
}
