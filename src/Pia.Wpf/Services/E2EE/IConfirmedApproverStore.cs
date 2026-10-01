namespace Pia.Services.E2EE;

/// <summary>Approving devices whose fingerprint a person confirmed on this device; device-local, never synced.</summary>
public interface IConfirmedApproverStore
{
    bool IsConfirmed(string deviceId, string fingerprint);

    Task RecordAsync(string deviceId, string fingerprint);
}
