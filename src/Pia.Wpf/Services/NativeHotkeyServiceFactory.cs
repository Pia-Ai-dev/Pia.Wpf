using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class NativeHotkeyServiceFactory : INativeHotkeyServiceFactory
{
    public INativeHotkeyService? Create(int hotkeyId, KeyboardShortcut shortcut)
    {
        try
        {
            return new NativeHotkeyService(hotkeyId, (uint)shortcut.Modifiers, shortcut.VirtualKeyCode);
        }
        catch
        {
            return null;
        }
    }
}
