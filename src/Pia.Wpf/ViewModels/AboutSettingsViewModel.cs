using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;

namespace Pia.ViewModels;

/// <summary>Settings → About: version, the AI-system notice and the legal links (AI Act Art. 50 transparency).</summary>
public sealed partial class AboutSettingsViewModel : ObservableObject
{
    public string Version { get; } = AppVersionInfo.Version;

    public string WebsiteUrl => PiaLinks.Website;
    public string ImprintUrl => PiaLinks.Imprint;
    public string PrivacyPolicyUrl => PiaLinks.PrivacyPolicy;
    public string DocumentationUrl => PiaLinks.Documentation;
    public string AiFeedbackAddress => PiaLinks.AiFeedbackAddress;
    public string AiFeedbackMailto => PiaLinks.AiFeedbackMailto;
}
