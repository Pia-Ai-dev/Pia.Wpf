namespace Pia.Services.Interfaces;

/// <summary>Why the app signed the user out without being asked to.</summary>
public enum AuthSignOutReason
{
    /// <summary>The server refused the refresh token outright (401): expired, rotated away or revoked.</summary>
    SessionExpired,

    /// <summary>The server refused to refresh the session for a non-auth reason that cannot recover.</summary>
    ServerRejectedSession,

    /// <summary>The stored session cannot be opened here, so no refresh can ever be sent.</summary>
    LocalSessionUnusable,

    /// <summary>This computer's device keys were lost; it continues under a new device id after a fresh sign-in.</summary>
    DeviceKeysLost,
}

public class AuthSignedOutEventArgs(AuthSignOutReason reason) : EventArgs
{
    public AuthSignOutReason Reason { get; } = reason;
}
