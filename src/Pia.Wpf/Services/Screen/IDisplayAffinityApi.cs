namespace Pia.Services.Screen;

internal interface IDisplayAffinityApi
{
    bool TryGet(nint hwnd, out uint affinity);

    bool TrySet(nint hwnd, uint affinity);
}
