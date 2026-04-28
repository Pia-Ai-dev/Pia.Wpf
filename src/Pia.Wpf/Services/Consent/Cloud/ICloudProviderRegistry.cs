namespace Pia.Services.Consent.Cloud;

public interface ICloudProviderRegistry
{
    IReadOnlyList<CloudProviderDescriptor> All { get; }
    CloudProviderDescriptor? Find(string id);
    IEnumerable<CloudProviderDescriptor> ForProfile(SecurityProfile profile);
}
