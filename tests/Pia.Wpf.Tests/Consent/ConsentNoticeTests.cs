using System.Collections;
using System.Globalization;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>A record cites the notice by version, so the wording must not change under an unchanged version.</summary>
public sealed class ConsentNoticeTests
{
    private static readonly CultureInfo[] NoticeCultures =
        [CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr")];

    [Fact]
    public void DisclaimerWording_BelongsToTheCurrentVersion()
    {
        var actual = HashOf(
            CommonStrings.ResourceManager, key => key.StartsWith("DirectTrans_Disclaimer_", StringComparison.Ordinal));

        Assert.True(
            actual == ConsentNotice.TextHash,
            $"The disclaimer wording changed. Bump ConsentNotice.Version and set ConsentNotice.TextHash to \"{actual}\".");
    }

    [Fact]
    public void TeamsAcknowledgementWording_BelongsToTheCurrentVersion()
    {
        var actual = HashOf(ViewStrings.ResourceManager, key => key == "Routines_Field_MeetingConsent");

        Assert.True(
            actual == ConsentNotice.TeamsTextHash,
            "The Teams acknowledgement wording changed. Bump ConsentNotice.TeamsVersion and set "
            + $"ConsentNotice.TeamsTextHash to \"{actual}\".");
    }

    [Theory]
    [InlineData(TargetLanguage.EN, "en")]
    [InlineData(TargetLanguage.DE, "de")]
    [InlineData(TargetLanguage.FR, "fr")]
    public void LanguageOf_IsTheTwoLetterUiLanguage(TargetLanguage uiLanguage, string expected)
    {
        Assert.Equal(expected, ConsentNotice.LanguageOf(uiLanguage));
    }

    // Keys come from the neutral set, so a new disclaimer key changes the hash too.
    private static string HashOf(ResourceManager resources, Func<string, bool> isNoticeKey)
    {
        var neutral = resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        var keys = neutral.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Where(isNoticeKey)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(keys);

        var text = new StringBuilder();
        foreach (var culture in NoticeCultures)
        {
            var translated = false;
            foreach (var key in keys)
            {
                var value = resources.GetString(key, culture);
                Assert.False(string.IsNullOrEmpty(value), $"{key} has no {culture.Name} text");
                translated |= value != resources.GetString(key, CultureInfo.InvariantCulture);
                text.Append(culture.Name).Append('\n').Append(key).Append('\n').Append(value).Append('\n');
            }

            // Without the satellite assembly every culture reads English, and a German edit would go unnoticed.
            Assert.True(culture.Equals(CultureInfo.InvariantCulture) || translated, $"no {culture.Name} text was loaded");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
