using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>
/// The name Windows prints as the header of every Pia toast. It comes from the AUMID's registered
/// <c>DisplayName</c>, which the notifications toolkit fills from the process name ("Pia.Wpf").
/// </summary>
/// <remarks>
/// Measured 2026-09-10: Windows caches that name per AUMID the first time the id is registered and never
/// reads the key again, so rewriting <c>DisplayName</c> under the old <c>Pia.App</c> left every toast still
/// headed "Pia.Wpf". A previously unseen AUMID picks the value up, which is why this one is versioned.
/// </remarks>
internal static class ToastAppIdentity
{
    public const string Aumid = "Pia.AIAssistant";

    /// <summary>The stuck-on-"Pia.Wpf" id, unregistered so Windows stops listing it as a separate app.</summary>
    private const string LegacyAumid = "Pia.App";

    private const string DisplayName = "Pia AI Assistant";

    private const string AumidKeyPath = @"Software\Classes\AppUserModelId";
    private const string NotificationSettingsKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public static void Ensure()
    {
        try
        {
            // The toolkit registers once, from its static constructor, and writes the process name in. Forcing
            // it here means the overwrite below is the last one for the life of the process.
            RuntimeHelpers.RunClassConstructor(typeof(ToastNotificationManagerCompat).TypeHandle);

            using var key = Registry.CurrentUser.CreateSubKey($@"{AumidKeyPath}\{Aumid}");
            key?.SetValue(nameof(DisplayName), DisplayName, RegistryValueKind.String);

            TryRemoveLegacyRegistration();
        }
        catch (Exception ex)
        {
            // Logging is not up yet, and a toast headed with the wrong name is not worth failing startup for.
            Debug.WriteLine($"Failed to register the toast display name: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the old id's registration, including the COM activator it named — otherwise Windows keeps
    /// offering "Pia.Wpf" in Settings › Notifications alongside the real entry.
    /// </summary>
    private static void TryRemoveLegacyRegistration()
    {
        try
        {
            var legacyPath = $@"{AumidKeyPath}\{LegacyAumid}";

            using (var legacy = Registry.CurrentUser.OpenSubKey(legacyPath))
            {
                if (legacy is null)
                    return;

                if (legacy.GetValue("CustomActivator") is string clsid && clsid.Length > 0)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid}", throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppID\{clsid}", throwOnMissingSubKey: false);
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(legacyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"{NotificationSettingsKeyPath}\{LegacyAumid}", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove the legacy toast registration: {ex.Message}");
        }
    }
}
