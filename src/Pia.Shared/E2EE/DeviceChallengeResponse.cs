namespace Pia.Shared.E2EE;

/// <summary>A single-use challenge for one device-signed request; fetched fresh per request, never cached.</summary>
public class DeviceChallengeResponse
{
    public required string ChallengeId { get; set; }
    public DateTime ExpiresAt { get; set; }
}
