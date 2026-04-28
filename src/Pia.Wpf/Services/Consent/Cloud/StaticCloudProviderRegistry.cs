namespace Pia.Services.Consent.Cloud;

public sealed class StaticCloudProviderRegistry : ICloudProviderRegistry
{
    public IReadOnlyList<CloudProviderDescriptor> All { get; }

    public StaticCloudProviderRegistry()
    {
        All = new[]
        {
            new CloudProviderDescriptor(
                Id: "mistral",
                DisplayName: "Mistral (FR)",
                Jurisdiction: CloudJurisdiction.EuOnly,
                RequiresExplicitDrittlandConsent: false,
                AvvDocumentationUrl: "https://mistral.ai/dpa"),
            new CloudProviderDescriptor(
                Id: "anthropic",
                DisplayName: "Anthropic (US)",
                Jurisdiction: CloudJurisdiction.UsAdequacyFramework,
                RequiresExplicitDrittlandConsent: true,
                AvvDocumentationUrl: "https://anthropic.com/dpa"),
            new CloudProviderDescriptor(
                Id: "openai",
                DisplayName: "OpenAI (US)",
                Jurisdiction: CloudJurisdiction.UsAdequacyFramework,
                RequiresExplicitDrittlandConsent: true,
                AvvDocumentationUrl: "https://openai.com/policies/dpa"),
            new CloudProviderDescriptor(
                Id: "azure-openai-eu",
                DisplayName: "Azure OpenAI (EU)",
                Jurisdiction: CloudJurisdiction.EuOnly,
                RequiresExplicitDrittlandConsent: false,
                AvvDocumentationUrl: "https://learn.microsoft.com/azure/ai-services/dpa"),
        };
    }

    public CloudProviderDescriptor? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<CloudProviderDescriptor> ForProfile(SecurityProfile profile)
    {
        foreach (var p in All)
        {
            switch (p.Jurisdiction)
            {
                case CloudJurisdiction.EuOnly:
                    if (profile.AllowEuCloud) yield return p;
                    break;
                case CloudJurisdiction.UsAdequacyFramework:
                case CloudJurisdiction.OtherThirdCountry:
                    if (profile.AllowNonEuCloud) yield return p;
                    break;
            }
        }
    }
}
