namespace Pia.Services.Consent.Cloud;

public enum CloudJurisdiction
{
    EuOnly,
    UsAdequacyFramework,
    OtherThirdCountry,
}

/// <summary>
/// Static metadata describing a cloud LLM provider for compliance routing.
/// </summary>
public sealed record CloudProviderDescriptor(
    string Id,
    string DisplayName,
    CloudJurisdiction Jurisdiction,
    bool RequiresExplicitDrittlandConsent,
    string AvvDocumentationUrl);
