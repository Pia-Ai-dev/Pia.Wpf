namespace Pia.Infrastructure;

public static class HttpClientNames
{
    /// <summary>Follows no redirect, so a body carrying a password or code never reaches another URL.</summary>
    public const string NoRedirect = "Pia.NoRedirect";
}
