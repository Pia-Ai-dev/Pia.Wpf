using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels.Models;

/// <summary>
/// One per-speaker consent chip shown in the direct-transcription overlay header: a diarized loopback
/// speaker, muted while awaiting consent ("Speaker 2 · awaiting consent") and accented once granted
/// ("John Doe · consented"). Driven by <c>IDirectTranscriptionService.SpeakerRegistered</c> and
/// <c>SpeakerConsentChanged</c> (design §3.9).
///
/// <para>Lives in <c>Pia.ViewModels.Models</c>, not <c>Pia.ViewModels</c>: it is not itself a page/
/// overlay view-model (it never appears as a DataContext), and
/// <c>NamingConventionTests.ObservableObjects_InViewModelsNamespace_MustEndWithViewModel</c> only
/// reaches types directly in <c>Pia.ViewModels</c> — the same reason <c>KanbanColumnViewModel</c>'s
/// siblings (<c>ProviderDisplayItem</c>, etc.) sit here too.</para>
///
/// <para>Carries no consent evidence, no transcript text and no audio — only what the chip needs to
/// render. The sensitive payload (the consent sentence itself) never leaves the service boundary.</para>
/// </summary>
public sealed partial class SpeakerConsentChip : ObservableObject
{
    /// <summary>
    /// The key this chip is found by: the diarizer label before consent, the extracted name after (the
    /// service silently renames the diarizer label + consent-map key on grant, and the view model mirrors
    /// that by updating this property so a later revoke/rename still finds the right chip).
    /// </summary>
    [ObservableProperty]
    private string _speakerLabel = string.Empty;

    /// <summary>What the chip shows. Equal to <see cref="SpeakerLabel"/> today; kept distinct so the
    /// two can diverge without a rename (e.g. a future "Speaker 2 (you renamed this)" annotation).</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>True once a consent sentence has been recognised for this speaker.</summary>
    [ObservableProperty]
    private bool _isConsented;

    /// <summary>Localized status line: "awaiting consent" / "consented" / "revoked".</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Palette slot (0..4), assigned independently of the bubble palette in
    /// <c>TranscriptOverlayViewModel</c> (that map is private to the base class) but using the same
    /// wrap-around scheme, so a speaker's chip and bubble colors are usually — not guaranteed — the same.
    /// </summary>
    [ObservableProperty]
    private int _colorIndex;
}
