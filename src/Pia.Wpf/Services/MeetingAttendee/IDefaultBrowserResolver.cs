using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Resolves the OS default browser to a Chromium-family <see cref="MeetingBrowserSelection"/> the
/// meeting attendee can drive, or <see cref="MeetingBrowserSelection.BundledChromium"/> when the
/// default is non-Chromium / unknown.
/// </summary>
public interface IDefaultBrowserResolver
{
    /// <summary>
    /// Resolves the OS default browser to <see cref="MeetingBrowserSelection.SystemChrome"/> /
    /// <see cref="MeetingBrowserSelection.SystemEdge"/>, or <see cref="MeetingBrowserSelection.BundledChromium"/>
    /// when the default is non-Chromium (Firefox, Brave, Opera, unknown). Never throws.
    /// </summary>
    MeetingBrowserSelection ResolveChromiumSelectionOrBundled();
}

/// <summary>
/// Reads the user's default browser for <c>https</c> from the registry UserChoice key and maps its
/// <c>ProgId</c> to a Chromium-family selection. The registry read is behind an injectable seam so the
/// ProgId-to-selection mapping (<see cref="MapProgIdToSelection"/>) is unit-testable without the live
/// registry.
/// </summary>
public sealed class DefaultBrowserResolver : IDefaultBrowserResolver
{
    private const string UserChoiceKeyPath =
        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";

    private readonly ILogger<DefaultBrowserResolver> _logger;
    private readonly Func<string?> _readHttpsProgId;

    public DefaultBrowserResolver(ILogger<DefaultBrowserResolver> logger)
        : this(logger, ReadHttpsUserChoiceProgId)
    {
    }

    internal DefaultBrowserResolver(ILogger<DefaultBrowserResolver> logger, Func<string?> readHttpsProgId)
    {
        _logger = logger;
        _readHttpsProgId = readHttpsProgId;
    }

    public MeetingBrowserSelection ResolveChromiumSelectionOrBundled()
    {
        string? progId = null;
        try
        {
            progId = _readHttpsProgId();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the default-browser UserChoice; using bundled Chromium");
        }

        var selection = MapProgIdToSelection(progId);
        if (selection == MeetingBrowserSelection.BundledChromium && !string.IsNullOrWhiteSpace(progId))
        {
            _logger.LogInformation(
                "Default browser is not a Chromium-family browser; meeting attendee using bundled Chromium");
        }
        return selection;
    }

    /// <summary>
    /// Pure mapping: a UserChoice <c>ProgId</c> to the browser the attendee can drive. Chrome registers
    /// <c>ChromeHTML</c>; Edge registers <c>MSEdgeHTM</c> (often suffixed, e.g. <c>MSEdgeHTM</c> or a
    /// versioned variant). Anything else (Firefox <c>FirefoxURL</c>, Brave, Opera, null) → bundled.
    /// </summary>
    internal static MeetingBrowserSelection MapProgIdToSelection(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId))
            return MeetingBrowserSelection.BundledChromium;
        if (progId.Contains("ChromeHTML", StringComparison.OrdinalIgnoreCase))
            return MeetingBrowserSelection.SystemChrome;
        if (progId.Contains("MSEdgeHTM", StringComparison.OrdinalIgnoreCase))
            return MeetingBrowserSelection.SystemEdge;
        return MeetingBrowserSelection.BundledChromium;
    }

    private static string? ReadHttpsUserChoiceProgId()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UserChoiceKeyPath);
        return key?.GetValue("ProgId") as string;
    }
}
