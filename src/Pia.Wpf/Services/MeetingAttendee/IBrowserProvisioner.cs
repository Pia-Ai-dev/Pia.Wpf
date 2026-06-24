namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Ensures an automated browser (Chromium) is available on disk for the meeting attendee,
/// downloading it on first use. Mirrors the model-provisioning seam used by the live
/// transcription pipeline (see <see cref="Pia.Services.LiveTranscription.LiveTranscriptionModels"/>).
/// </summary>
public interface IBrowserProvisioner
{
    /// <summary>
    /// Ensures the Chromium browser used to join meetings is present on disk and returns the
    /// path to its executable. Idempotent: if a usable browser is already cached the download
    /// is skipped and the cached executable path is returned.
    /// </summary>
    /// <param name="progress">
    /// Optional coarse progress sink. Playwright's installer does not expose byte-level
    /// progress, so reporting is phase-based (Downloading → Completed) rather than a smooth
    /// percentage. See <see cref="ChromiumProvisioner"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Absolute path to the Chromium executable.</returns>
    Task<string> EnsureChromiumAsync(
        IProgress<ChromiumDownloadProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coarse progress for the Chromium provisioning step. Mirrors
/// <see cref="Pia.Services.Interfaces.ModelDownloadProgress"/> in spirit, but the Playwright
/// installer is opaque (it shells out and writes its own progress to stdout), so we only
/// surface a phase rather than byte counts.
/// </summary>
public record ChromiumDownloadProgress(ChromiumProvisioningPhase Phase);

public enum ChromiumProvisioningPhase
{
    /// <summary>A usable Chromium was already cached; nothing was downloaded.</summary>
    AlreadyPresent,

    /// <summary>Chromium is being downloaded/installed (indeterminate; no byte-level granularity).</summary>
    Downloading,

    /// <summary>Provisioning finished successfully.</summary>
    Completed,
}
