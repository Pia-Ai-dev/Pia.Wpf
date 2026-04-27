namespace Pia.Services.Consent;

public interface IConsentClassifier
{
    ConsentClassification Classify(string transcriptText);
}
