using System.Reflection;
using Pia.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>ProviderEditModel maps every AiProvider field by hand twice, so a field missing from either mapper silently reverts to the default.</summary>
public class ProviderEditModelRoundTripTests
{
    /// <summary>Set by the persistence layer, not by the edit dialog, so they are legitimately not part of the round trip.</summary>
    private static readonly HashSet<string> NotMappedByTheDialog =
    [
        nameof(AiProvider.Id),
        nameof(AiProvider.EncryptedApiKey),
        nameof(AiProvider.CreatedAt),
        nameof(AiProvider.UpdatedAt),
    ];

    private static AiProvider FullyPopulatedProvider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Round trip provider",
        ProviderType = AiProviderType.Mistral,
        Endpoint = "https://example.invalid/v1",
        ModelName = "some-model",
        EncryptedApiKey = "encrypted",
        AzureDeploymentName = "deployment",
        SupportsToolCalling = true,
        SupportsStreaming = true,
        TimeoutSeconds = 123,
        MaxContextWindowTokens = 200_000,
        MaxOutputTokens = 8_192,
        ReasoningEffort = Pia.Models.ReasoningEffort.High,
        EnableWebSearch = true,
        MistralAgentId = "ag_123",
    };

    [Fact]
    public void EveryDialogMappedProperty_SurvivesFromProviderThenToProvider()
    {
        var source = FullyPopulatedProvider();

        var round = ProviderEditModel.FromProvider(source).ToProvider();

        var unmapped = new List<string>();
        foreach (var property in typeof(AiProvider).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is null || NotMappedByTheDialog.Contains(property.Name))
                continue;

            var value = property.GetValue(round);
            var fallback = property.PropertyType.IsValueType
                ? Activator.CreateInstance(property.PropertyType)
                : null;

            if (Equals(value, fallback))
                unmapped.Add(property.Name);
        }

        Assert.True(
            unmapped.Count == 0,
            "every writable AiProvider property the provider dialog owns must survive "
            + "FromProvider -> ToProvider, but these came back at their default (one of the two "
            + $"hand-written mappers is missing them): {string.Join(", ", unmapped)}");
    }

    [Fact]
    public void EveryDialogMappedProperty_RoundTripsToTheSameValue()
    {
        var source = FullyPopulatedProvider();

        var round = ProviderEditModel.FromProvider(source).ToProvider();

        var changed = new List<string>();
        foreach (var property in typeof(AiProvider).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is null || NotMappedByTheDialog.Contains(property.Name))
                continue;

            if (!Equals(property.GetValue(source), property.GetValue(round)))
                changed.Add($"{property.Name} ({property.GetValue(source)} -> {property.GetValue(round)})");
        }

        Assert.True(
            changed.Count == 0,
            $"the provider round trip must not alter any dialog-owned value: {string.Join(", ", changed)}");
    }

    [Fact]
    public void ZeroInTheDialog_MeansCompactionStaysOff()
    {
        // 0 is the edit model's "not configured" sentinel; the persisted model must see null.
        var model = ProviderEditModel.FromProvider(new AiProvider
        {
            Name = "Unconfigured",
            Endpoint = "https://example.invalid/v1",
        });

        Assert.Equal(0, model.MaxContextWindowTokens);
        Assert.Equal(0, model.MaxOutputTokens);

        var provider = model.ToProvider();

        Assert.Null(provider.MaxContextWindowTokens);
        Assert.Null(provider.MaxOutputTokens);
    }

    [Fact]
    public void NullOnTheProvider_ShowsAsZeroInTheDialog()
    {
        var provider = FullyPopulatedProvider();
        provider.MaxContextWindowTokens = null;
        provider.MaxOutputTokens = null;

        var model = ProviderEditModel.FromProvider(provider);

        Assert.Equal(0, model.MaxContextWindowTokens);
        Assert.Equal(0, model.MaxOutputTokens);
    }
}
