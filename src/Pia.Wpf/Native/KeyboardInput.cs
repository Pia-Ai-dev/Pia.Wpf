using System.Runtime.InteropServices;

namespace Pia.Native;

internal static partial class KeyboardInput
{
    private const int INPUT_KEYBOARD = 1;
    private const int KEYEVENTF_UNICODE = 0x0004;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_C = 0x43;
    private const ushort VK_V = 0x56;

    /// <summary>
    /// Sends Ctrl+C while first releasing any non-Ctrl modifier keys (Alt, Shift, Win) the user
    /// may still be holding from a triggering hotkey. Without this, an active hotkey like
    /// Ctrl+Alt+O turns our injected Ctrl+C into Ctrl+Alt+C, which apps don't treat as copy.
    /// </summary>
    public static uint PressCtrlCReleasingModifiers()
    {
        var inputs = new INPUT[8];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_MENU;
        inputs[0].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_SHIFT;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].ki.wVk = VK_LWIN;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].ki.wVk = VK_RWIN;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;

        inputs[4].type = INPUT_KEYBOARD;
        inputs[4].ki.wVk = VK_CONTROL;
        inputs[5].type = INPUT_KEYBOARD;
        inputs[5].ki.wVk = VK_C;
        inputs[6].type = INPUT_KEYBOARD;
        inputs[6].ki.wVk = VK_C;
        inputs[6].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[7].type = INPUT_KEYBOARD;
        inputs[7].ki.wVk = VK_CONTROL;
        inputs[7].ki.dwFlags = KEYEVENTF_KEYUP;

        return SendInput(8, inputs, Marshal.SizeOf<INPUT>());
    }

    public static uint PressCtrlV() => PressCtrlChord(VK_V);

    public static void PressAltTab()
    {
        var inputs = new INPUT[4];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_MENU;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_TAB;
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].ki.wVk = VK_TAB;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].ki.wVk = VK_MENU;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendCharacter(char c)
    {
        var inputs = new INPUT[2];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wScan = c;
        inputs[0].ki.dwFlags = KEYEVENTF_UNICODE;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wScan = c;
        inputs[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static uint PressCtrlChord(ushort virtualKey)
    {
        var inputs = new INPUT[4];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_CONTROL;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = virtualKey;
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].ki.wVk = virtualKey;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].ki.wVk = VK_CONTROL;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;

        return SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
        private readonly ulong padding;
    }
}
