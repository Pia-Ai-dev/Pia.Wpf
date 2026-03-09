using System.Runtime.InteropServices;
using Pia.Services.Interfaces;

namespace Pia.Services;

public unsafe partial class NativeHotkeyService : INativeHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly IntPtr _hwnd;
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly int _id;
    private readonly string _className;

    public event Action? HotKeyPressed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public NativeHotkeyService(int id, uint modifiers, uint vk)
    {
        _id = id;
        _className = "HotkeyListener_" + Guid.NewGuid();
        _wndProcDelegate = CustomWndProc;

        fixed (char* pClassName = _className)
        {
            var wndClass = new WNDCLASSEX
            {
                cbSize = (uint)sizeof(WNDCLASSEX),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                lpszClassName = (IntPtr)pClassName,
                hInstance = GetModuleHandle(IntPtr.Zero)
            };

            if (RegisterClassEx(ref wndClass) == 0)
            {
                throw new Exception($"Failed to register window class. Error: {Marshal.GetLastWin32Error()}");
            }
        }

        _hwnd = CreateWindowEx(
            0,
            _className,
            "HotkeyWindow",
            0, 0, 0, 0, 0,
            HWND_MESSAGE,
            IntPtr.Zero,
            GetModuleHandle(IntPtr.Zero),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Exception("Failed to create message window.");

        if (!RegisterHotKey(_hwnd, _id, modifiers, vk))
            throw new InvalidOperationException("Hotkey already in use.");
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && (int)wParam == _id)
        {
            HotKeyPressed?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, _id);
        DestroyWindow(_hwnd);
        UnregisterClass(_className, GetModuleHandle(IntPtr.Zero));
        GC.SuppressFinalize(this);
    }

    #region Win32 P/Invokes

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(IntPtr lpModuleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    #endregion
}
