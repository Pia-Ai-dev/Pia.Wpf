namespace Pia.Services.Consent;

public interface IConsentClassifier
{
    Task<ConsentClassification> ClassifyAsync(string transcriptText, string promptText, CancellationToken cancellationToken = default);
}
