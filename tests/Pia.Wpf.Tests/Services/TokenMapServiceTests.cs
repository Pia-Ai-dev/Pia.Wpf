using FluentAssertions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class TokenMapServiceTests
{
    private readonly IPiiDetector _piiDetector = Substitute.For<IPiiDetector>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    private TokenMapService CreateService() => new(_piiDetector, _memoryService, _settingsService);

    public TokenMapServiceTests()
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings());
        _memoryService.GetObjectsByTypeAsync(Arg.Any<string>()).Returns(new List<MemoryObject>());
    }

    // --- Tokenize ---

    [Fact]
    public void Tokenize_FirstValue_AssignsToken1()
    {
        var sut = CreateService();
        var token = sut.Tokenize("Maria Schmidt", "Person");
        token.Should().Be("[Person_1]");
    }

    [Fact]
    public void Tokenize_SameValueTwice_ReturnsSameToken()
    {
        var sut = CreateService();
        var token1 = sut.Tokenize("Maria Schmidt", "Person");
        var token2 = sut.Tokenize("Maria Schmidt", "Person");
        token1.Should().Be(token2);
    }

    [Fact]
    public void Tokenize_DifferentValues_AssignsSequentialTokens()
    {
        var sut = CreateService();
        var token1 = sut.Tokenize("Maria Schmidt", "Person");
        var token2 = sut.Tokenize("Hans Müller", "Person");
        token1.Should().Be("[Person_1]");
        token2.Should().Be("[Person_2]");
    }

    [Fact]
    public void Tokenize_DifferentCategories_IndependentCounters()
    {
        var sut = CreateService();
        var personToken = sut.Tokenize("Maria Schmidt", "Person");
        var emailToken = sut.Tokenize("maria@example.com", "Email");
        personToken.Should().Be("[Person_1]");
        emailToken.Should().Be("[Email_1]");
    }

    // --- GetToken ---

    [Fact]
    public void GetToken_KnownValue_ReturnsToken()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.GetToken("Maria Schmidt", "Person").Should().Be("[Person_1]");
    }

    [Fact]
    public void GetToken_UnknownValue_ReturnsNull()
    {
        var sut = CreateService();
        sut.GetToken("Unknown Person", "Person").Should().BeNull();
    }

    // --- Detokenize ---

    [Fact]
    public void Detokenize_KnownToken_ReplacesWithRealValue()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        var result = sut.Detokenize("Hello [Person_1], how are you?");
        result.Should().Be("Hello Maria Schmidt, how are you?");
    }

    [Fact]
    public void Detokenize_MultipleTokens_ReplacesAll()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.Tokenize("maria@example.com", "Email");
        var result = sut.Detokenize("[Person_1]'s email is [Email_1]");
        result.Should().Be("Maria Schmidt's email is maria@example.com");
    }

    [Fact]
    public void Detokenize_UnknownToken_PassesThrough()
    {
        var sut = CreateService();
        var result = sut.Detokenize("Hello [Person_99], how are you?");
        result.Should().Be("Hello [Person_99], how are you?");
    }

    [Fact]
    public void Detokenize_NoTokens_ReturnsOriginal()
    {
        var sut = CreateService();
        var result = sut.Detokenize("Hello, how are you?");
        result.Should().Be("Hello, how are you?");
    }

    // --- TokenizeStructuredResult ---

    [Fact]
    public void TokenizeStructuredResult_ReplacesKnownPii()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.Tokenize("maria@example.com", "Email");

        var input = "Name: Maria Schmidt, Email: maria@example.com";
        var result = sut.TokenizeStructuredResult(input);
        result.Should().Be("Name: [Person_1], Email: [Email_1]");
    }

    [Fact]
    public void TokenizeStructuredResult_DetectsNewEmailsViaRegex()
    {
        var sut = CreateService();
        _piiDetector.DetectPii(Arg.Any<string>()).Returns(callInfo =>
        {
            var text = callInfo.Arg<string>();
            if (text.Contains("new-person@example.com"))
                return new List<PiiMatch> { new("new-person@example.com", "Email", 9, 22) };
            return new List<PiiMatch>();
        });

        var input = "Contact: new-person@example.com";
        var result = sut.TokenizeStructuredResult(input);
        result.Should().Contain("[Email_1]");
        result.Should().NotContain("new-person@example.com");
    }

    [Fact]
    public void TokenizeStructuredResult_DetectsNewPhonesViaRegex()
    {
        var sut = CreateService();
        _piiDetector.DetectPii(Arg.Any<string>()).Returns(callInfo =>
        {
            var text = callInfo.Arg<string>();
            if (text.Contains("+49 170 1234567"))
                return new List<PiiMatch> { new("+49 170 1234567", "Phone", 5, 15) };
            return new List<PiiMatch>();
        });

        var input = "Call +49 170 1234567 for info";
        var result = sut.TokenizeStructuredResult(input);
        result.Should().Contain("[Phone_1]");
        result.Should().NotContain("+49 170 1234567");
    }

    [Fact]
    public void TokenizeStructuredResult_EmptyString_ReturnsEmpty()
    {
        var sut = CreateService();
        sut.TokenizeStructuredResult("").Should().Be("");
    }

    // --- Clear ---

    [Fact]
    public void Clear_ResetsAllMapsAndCounters()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.Detokenize("[Person_1]").Should().Be("Maria Schmidt");

        sut.Clear();

        // After clear, old tokens are gone, counter resets
        sut.GetToken("Maria Schmidt", "Person").Should().BeNull();
        sut.Tokenize("Hans Müller", "Person").Should().Be("[Person_1]");
    }

    // --- InitializeAsync ---

    [Fact]
    public async Task InitializeAsync_LoadsKeywordsAsCustomTokens()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Schmidt family" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        sut.GetToken("Schmidt family", "Custom").Should().Be("[Custom_1]");
    }

    [Fact]
    public async Task InitializeAsync_PrePopulatesFromPersonalProfile()
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings());

        var profile = new MemoryObject
        {
            Type = MemoryObjectTypes.PersonalProfile,
            Label = "Personal Profile",
            Data = """{"name": "Maria Schmidt", "email": "maria@example.com"}"""
        };
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile)
            .Returns(new List<MemoryObject> { profile });
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.ContactList)
            .Returns(new List<MemoryObject>());

        _piiDetector.DetectPiiInStructured(profile.Data, MemoryObjectTypes.PersonalProfile)
            .Returns(new List<PiiMatch>
            {
                new("Maria Schmidt", "Person", 0, 14),
                new("maria@example.com", "Email", 0, 17)
            });

        var sut = CreateService();
        await sut.InitializeAsync();

        sut.GetToken("Maria Schmidt", "Person").Should().Be("[Person_1]");
        sut.GetToken("maria@example.com", "Email").Should().Be("[Email_1]");
    }

    [Fact]
    public async Task InitializeAsync_PrePopulatesFromContactList()
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings());

        var contacts = new MemoryObject
        {
            Type = MemoryObjectTypes.ContactList,
            Label = "Contacts",
            Data = """{"contacts": [{"name": "Hans Müller", "phone": "+49 30 9876543"}]}"""
        };
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile)
            .Returns(new List<MemoryObject>());
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.ContactList)
            .Returns(new List<MemoryObject> { contacts });

        _piiDetector.DetectPiiInStructured(contacts.Data, MemoryObjectTypes.ContactList)
            .Returns(new List<PiiMatch>
            {
                new("Hans Müller", "Person", 0, 11),
                new("+49 30 9876543", "Phone", 0, 14)
            });

        var sut = CreateService();
        await sut.InitializeAsync();

        sut.GetToken("Hans Müller", "Person").Should().Be("[Person_1]");
        sut.GetToken("+49 30 9876543", "Phone").Should().Be("[Phone_1]");
    }

    [Fact]
    public async Task InitializeAsync_TokenizesLabelsViaCrossMatch()
    {
        _settingsService.GetSettingsAsync().Returns(new AppSettings());

        var profile = new MemoryObject
        {
            Type = MemoryObjectTypes.PersonalProfile,
            Label = "Maria Schmidt's profile",
            Data = """{"name": "Maria Schmidt"}"""
        };
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile)
            .Returns(new List<MemoryObject> { profile });
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.ContactList)
            .Returns(new List<MemoryObject>());

        _piiDetector.DetectPiiInStructured(profile.Data, MemoryObjectTypes.PersonalProfile)
            .Returns(new List<PiiMatch>
            {
                new("Maria Schmidt", "Person", 0, 14)
            });

        var sut = CreateService();
        await sut.InitializeAsync();

        // The label "Maria Schmidt's profile" contains "Maria Schmidt" which is now registered
        // TokenizeStructuredResult should replace it
        var tokenized = sut.TokenizeStructuredResult("Maria Schmidt's profile");
        tokenized.Should().Contain("[Person_1]");
    }

    // --- Fuzzy matching ---

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyMatchesTypoInCustomKeyword()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Marco" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        var result = sut.TokenizeStructuredResult("Hello Macro, welcome!");
        result.Should().Be("Hello [Custom_1], welcome!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzySkipsShortWords()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "an" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "as" is 2 chars — should not fuzzy match to "an"
        var result = sut.TokenizeStructuredResult("as expected");
        result.Should().Be("as expected");
    }

    [Fact]
    public async Task TokenizeStructuredResult_ExactMatchTakesPriority()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Marco" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        var result = sut.TokenizeStructuredResult("Hello Marco!");
        result.Should().Be("Hello [Custom_1]!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyDoesNotMatchDistantWords()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Marco" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "XXXXX" is distance 5 from "Marco" — should not match
        var result = sut.TokenizeStructuredResult("Hello XXXXX!");
        result.Should().Be("Hello XXXXX!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyMatchesTranspositionsInLongWords()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Schmidt" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "Schmtid" is a transposition of "Schmidt" — Jaro-Winkler handles this well
        var result = sut.TokenizeStructuredResult("Hello Schmtid!");
        result.Should().Be("Hello [Custom_1]!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyTiebreakPrefersLongerThenAlphabetical()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Margo" });
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Marco" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "Marso" has equal Jaro-Winkler similarity to both "Marco" and "Margo"
        // Tiebreak: alphabetically first → "Marco" ([Custom_2])
        var result = sut.TokenizeStructuredResult("Hello Marso!");
        result.Should().Be("Hello [Custom_2]!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyPrefersHighestSimilarity()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Flit" });
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "Flins" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "Flint" has higher Jaro-Winkler similarity to "Flit" than "Flins"
        // Best similarity wins → "Flit" ([Custom_1])
        var result = sut.TokenizeStructuredResult("Hello Flint!");
        result.Should().Be("Hello [Custom_1]!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyRejectsUnrelatedWords()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "maya" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "tada" should NOT match "maya" — low Jaro-Winkler similarity
        var result = sut.TokenizeStructuredResult("tada!");
        result.Should().Be("tada!");
    }

    [Fact]
    public async Task TokenizeStructuredResult_FuzzyMatchesTransposition()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "john" });
        _settingsService.GetSettingsAsync().Returns(settings);

        var sut = CreateService();
        await sut.InitializeAsync();

        // "jonh" is a transposition of "john" — Jaro-Winkler handles this well
        var result = sut.TokenizeStructuredResult("Hello jonh!");
        result.Should().Be("Hello [Custom_1]!");
    }

    // --- Tokenize longer values first ---

    [Fact]
    public void TokenizeStructuredResult_ReplacesLongerValuesFirst()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.Tokenize("Maria", "Person");

        var result = sut.TokenizeStructuredResult("Contact Maria Schmidt today");
        result.Should().Be("Contact [Person_1] today");
    }
}
