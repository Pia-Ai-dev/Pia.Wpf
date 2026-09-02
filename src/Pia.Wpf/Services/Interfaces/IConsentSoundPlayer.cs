namespace Pia.Services.Interfaces;

/// <summary>
/// Plays the short confirmation tone that follows a spoken-consent grant in direct transcription.
/// Behind an interface so ViewModels never name an audio type and tests never open a sound device.
/// </summary>
public interface IConsentSoundPlayer
{
    /// <summary>Fire-and-forget: returns before the tone finishes and never throws.</summary>
    void PlayConsentGranted();
}
