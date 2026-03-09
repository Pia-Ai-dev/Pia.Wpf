namespace Pia.Services.Interfaces;

public interface INativeHotkeyService : IDisposable
{
    event Action? HotKeyPressed;
}
