namespace Pia.Services.Interfaces;

public interface IAutostartService
{
    void Enable();
    void Disable();
    bool IsEnabled();
}
