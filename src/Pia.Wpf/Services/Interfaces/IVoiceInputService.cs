namespace Pia.Services.Interfaces;

public interface IVoiceInputService
{
    Task<string?> CaptureVoiceInputAsync();
}
