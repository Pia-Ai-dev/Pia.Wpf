using System.Windows.Input;

namespace Pia.Models;

public record KeyboardShortcut(
    KeyModifiers Modifiers,
    Key Key,
    uint VirtualKeyCode
)
{
    public static KeyboardShortcut DefaultCtrlAltP() =>
        new(KeyModifiers.Control | KeyModifiers.Alt, Key.P, 0x50);

    public static KeyboardShortcut DefaultCtrlAltO() =>
        new(KeyModifiers.Control | KeyModifiers.Alt, Key.O, 0x4F);

    public static KeyboardShortcut DefaultCtrlAltR() =>
        new(KeyModifiers.Control | KeyModifiers.Alt, Key.R, 0x52);

    public string DisplayText => $"{ModifiersToString(Modifiers)}+{Key}";

    private static string ModifiersToString(KeyModifiers modifiers) => modifiers switch
    {
        KeyModifiers.Control => "Ctrl",
        KeyModifiers.Alt => "Alt",
        KeyModifiers.Shift => "Shift",
        KeyModifiers.Control | KeyModifiers.Alt => "Ctrl+Alt",
        KeyModifiers.Control | KeyModifiers.Shift => "Ctrl+Shift",
        KeyModifiers.Alt | KeyModifiers.Shift => "Alt+Shift",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift => "Ctrl+Alt+Shift",
        _ => modifiers.ToString()
    };
}

[Flags]
public enum KeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}
