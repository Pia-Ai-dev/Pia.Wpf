using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>The wizard card and the settings card submit identically and differ only in the follow-up.</summary>
internal static class BusinessProfileSubmission
{
    internal static async Task<(bool Success, string? Error)> SubmitAsync(
        IAuthService authService,
        ILocalizationService localizationService,
        string companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            return (false, localizationService["Sync_Cloud_BusinessProfile_CompanyRequired"]);

        var (success, error) = await authService.SubmitBusinessProfileAsync(companyName.Trim());
        return (success, success ? null : error);
    }
}
