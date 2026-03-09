namespace Pia.Services.Interfaces;

public interface IAuthService
{
    /// <summary>Whether the user is currently logged in with valid tokens.</summary>
    bool IsLoggedIn { get; }

    /// <summary>Display name of the logged-in user (null if not logged in).</summary>
    string? UserDisplayName { get; }

    /// <summary>Email of the logged-in user (null if not logged in).</summary>
    string? UserEmail { get; }

    /// <summary>OAuth provider name (null if not logged in).</summary>
    string? Provider { get; }

    /// <summary>Initiates OAuth login via system browser. Returns success and optional error message.</summary>
    Task<(bool Success, string? ErrorMessage)> LoginAsync(string provider);

    /// <summary>Logs in with email and password. Returns success and optional error message.</summary>
    Task<(bool Success, string? ErrorMessage)> LoginWithPasswordAsync(string email, string password);

    /// <summary>Logs out and clears stored tokens.</summary>
    Task LogoutAsync();

    /// <summary>Gets a valid access token (refreshing if needed). Returns null if not logged in.</summary>
    Task<string?> GetAccessTokenAsync();

    /// <summary>Fired when login state changes.</summary>
    event EventHandler<bool>? LoginStateChanged;
}
