using System.Diagnostics;

namespace Pia.Helpers;

/// <summary>Opens a link from untrusted content (model output); any other scheme would reach its shell handler.</summary>
public static class ExternalLinkLauncher
{
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true })
            return false;

        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            return true;

        // Help answers cite mailto links; some mail clients honour attach= and would mail a local file.
        return uri.Scheme == Uri.UriSchemeMailto
               && !uri.Query.Contains("attach", StringComparison.OrdinalIgnoreCase);
    }

    public static void Open(Uri? uri)
    {
        if (!IsAllowed(uri))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No default handler — swallow, the link stays visible.
        }
    }
}
