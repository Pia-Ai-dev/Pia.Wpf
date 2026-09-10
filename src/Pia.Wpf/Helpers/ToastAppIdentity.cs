using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>
/// The name Windows prints as the header of every Pia toast. It comes from the AUMID's registered
/// <c>DisplayName</c>, which the notifications toolkit fills from the process name ("Pia.Wpf") — so it
/// has to be overwritten after the toolkit has registered, not before.
/// </summary>
internal static class ToastAppIdentity
{
    public const string Aumid = "Pia.App";

    private const string DisplayName = "Pia AI Assistant";

    public static void Ensure()
    {
        try
        {
            // The toolkit registers once, from its static constructor. Forcing it here means the write
            // below is the last one for the life of the process.
            RuntimeHelpers.RunClassConstructor(typeof(ToastNotificationManagerCompat).TypeHandle);

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
            key?.SetValue(nameof(DisplayName), DisplayName, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // Logging is not up yet, and a toast headed with the wrong name is not worth failing startup for.
            Debug.WriteLine($"Failed to register the toast display name: {ex.Message}");
        }
    }
}
