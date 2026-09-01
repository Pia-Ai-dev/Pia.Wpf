using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class TopicIdentityTests
{
    // The six alias pairs a real vault actually grew, each as two pages.
    [Theory]
    [InlineData("Azure OpenAI", "Azure OpenAI Service")]
    [InlineData("DAX", "DAX 40")]
    [InlineData("Dow Jones", "Dow Jones Industrial Average")]
    [InlineData("Meta", "Meta Platforms")]
    [InlineData("NASDAQ", "NASDAQ 100")]
    [InlineData("Pia", "Pia (Personal Intelligent Assistant)")]
    [InlineData("Acme", "Acme GmbH")]
    [InlineData("Acme Corp", "Acme Corporation")]
    [InlineData("Broadcom", "The Broadcom Group")]
    public void Aliases_share_one_identity(string a, string b)
    {
        Assert.Equal(TopicIdentity.Canonicalize(a), TopicIdentity.Canonicalize(b));
    }

    [Theory]
    [InlineData("Apple", "Apple Intelligence")]
    [InlineData("Microsoft", "Microsoft Copilot")]
    [InlineData("SAP", "SAP Business AI")]
    [InlineData("Meta", "Microsoft")]
    [InlineData("Reality Labs", "Optimus")]
    public void Distinct_topics_keep_distinct_identities(string a, string b)
    {
        Assert.NotEqual(TopicIdentity.Canonicalize(a), TopicIdentity.Canonicalize(b));
    }

    // Stripping must never empty the key, or every such topic would collide with every other.
    [Theory]
    [InlineData("Group")]
    [InlineData("Services")]
    [InlineData("40")]
    [InlineData("(parenthesised only)")]
    public void A_name_made_only_of_dropped_words_keeps_an_identity(string subject)
    {
        Assert.NotEmpty(TopicIdentity.Canonicalize(subject));
    }

    [Fact]
    public void Names_made_only_of_dropped_words_do_not_all_collide()
    {
        Assert.NotEqual(TopicIdentity.Canonicalize("Group"), TopicIdentity.Canonicalize("Services"));
    }

    [Fact]
    public void Diacritics_and_case_do_not_split_an_identity()
    {
        Assert.Equal(
            TopicIdentity.Canonicalize("Kurs-Gewinn-Verhältnis"),
            TopicIdentity.Canonicalize("kurs gewinn verhaltnis"));
    }
}
