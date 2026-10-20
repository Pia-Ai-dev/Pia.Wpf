namespace Pia.Models;

/// <summary>What the approval dialog shows; <see cref="DeviceName"/> is already sanitized for display.</summary>
public sealed record DeviceApprovalPrompt(
    string DeviceName,
    bool NameHadHiddenCharacters,
    string DeviceFingerprint,
    string OwnFingerprint);
