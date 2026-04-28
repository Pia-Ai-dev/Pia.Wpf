using Pia.Services.Consent.Cloud;

namespace Pia.Services.Consent;

/// <summary>
/// Per-speaker consent scope (spec §2.4). Defines which processing categories the speaker
/// has agreed to. Distinct from <see cref="SecurityProfile"/>, which is the operator's
/// global ceiling: a cloud call is permitted only if BOTH the profile permits the
/// jurisdiction AND the speaker's scope permits it.
/// </summary>
public sealed record ConsentScope(
    bool LocalProcessing,
    bool EuCloudProcessing,
    bool NonEuCloudProcessing,
    bool BiometricPersistence)
{
    /// <summary>Default for granted speakers when no per-speaker scope is captured: derive
    /// from the active profile so existing flows keep working without new prompts.</summary>
    public static ConsentScope FromProfile(SecurityProfile profile) => new(
        LocalProcessing: true,
        EuCloudProcessing: profile.AllowEuCloud,
        NonEuCloudProcessing: profile.AllowNonEuCloud,
        BiometricPersistence: false);

    public static readonly ConsentScope LocalOnly = new(true, false, false, false);

    public bool AllowsCloud(CloudJurisdiction jurisdiction) => jurisdiction switch
    {
        CloudJurisdiction.EuOnly => EuCloudProcessing,
        CloudJurisdiction.UsAdequacyFramework => NonEuCloudProcessing,
        CloudJurisdiction.OtherThirdCountry => NonEuCloudProcessing,
        _ => false,
    };
}
