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
        Assert.Equal("[Person_1]", token);
    }

    [Fact]
    public void Tokenize_SameValueTwice_ReturnsSameToken()
    {
        var sut = CreateService();
        var token1 = sut.Tokenize("Maria Schmidt", "Person");
        var token2 = sut.Tokenize("Maria Schmidt", "Person");
        Assert.Equal(token2, token1);
    }

    [Fact]
    public void Tokenize_DifferentValues_AssignsSequentialTokens()
    {
        var sut = CreateService();
        var token1 = sut.Tokenize("Maria Schmidt", "Person");
        var token2 = sut.Tokenize("Hans Müller", "Person");
        Assert.Equal("[Person_1]", token1);
        Assert.Equal("[Person_2]", token2);
    }

    [Fact]
    public void Tokenize_DifferentCategories_IndependentCounters()
    {
        var sut = CreateService();
        var personToken = sut.Tokenize("Maria Schmidt", "Person");
        var emailToken = sut.Tokenize("maria@example.com", "Email");
        Assert.Equal("[Person_1]", personToken);
        Assert.Equal("[Email_1]", emailToken);
    }

    // --- GetToken ---

    [Fact]
    public void GetToken_KnownValue_ReturnsToken()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        Assert.Equal("[Person_1]", sut.GetToken("Maria Schmidt", "Person"));
    }

    [Fact]
    public void GetToken_UnknownValue_ReturnsNull()
    {
        var sut = CreateService();
        Assert.Null(sut.GetToken("Unknown Person", "Person"));
    }

    // --- Detokenize ---

    [Fact]
    public void Detokenize_KnownToken_ReplacesWithRealValue()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        var result = sut.Detokenize("Hello [Person_1], how are you?");
        Assert.Equal("Hello Maria Schmidt, how are you?", result);
    }

    [Fact]
    public void Detokenize_MultipleTokens_ReplacesAll()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.Tokenize("maria@example.com", "Email");
        var result = sut.Detokenize("[Person_1]'s email is [Email_1]");
        Assert.Equal("Maria Schmidt's email is maria@example.com", result);
    }

    [Fact]
    public void Detokenize_UnknownToken_PassesThrough()
    {
        var sut = CreateService();
        var result = sut.Detokenize("Hello [Person_99], how are you?");
        Assert.Equal("Hello [Person_99], how are you?", result);
    }

    [Fact]
    public void Detokenize_NoTokens_ReturnsOriginal()
    {
        var sut = CreateService();
        var result = sut.Detokenize("Hello, how are you?");
        Assert.Equal("Hello, how are you?", result);
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
        Assert.Equal("Name: [Person_1], Email: [Email_1]", result);
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
        Assert.Contains("[Email_1]", result);
        Assert.DoesNotContain("new-person@example.com", result);
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
        Assert.Contains("[Phone_1]", result);
        Assert.DoesNotContain("+49 170 1234567", result);
    }

    [Fact]
    public void TokenizeStructuredResult_EmptyString_ReturnsEmpty()
    {
        var sut = CreateService();
        Assert.Equal("", sut.TokenizeStructuredResult(""));
    }

    // --- Clear ---

    [Fact]
    public void Clear_ResetsAllMapsAndCounters()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        Assert.Equal("Maria Schmidt", sut.Detokenize("[Person_1]"));

        sut.Clear();

        // After clear, old tokens are gone, counter resets
        Assert.Null(sut.GetToken("Maria Schmidt", "Person"));
        Assert.Equal("[Person_1]", sut.Tokenize("Hans Müller", "Person"));
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

        Assert.Equal("[Custom_1]", sut.GetToken("Schmidt family", "Custom"));
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

        Assert.Equal("[Person_1]", sut.GetToken("Maria Schmidt", "Person"));
        Assert.Equal("[Email_1]", sut.GetToken("maria@example.com", "Email"));
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

        Assert.Equal("[Person_1]", sut.GetToken("Hans Müller", "Person"));
        Assert.Equal("[Phone_1]", sut.GetToken("+49 30 9876543", "Phone"));
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
        Assert.Contains("[Person_1]", tokenized);
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
        Assert.Equal("Hello [Custom_1], welcome!", result);
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
        Assert.Equal("as expected", result);
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
        Assert.Equal("Hello [Custom_1]!", result);
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
        Assert.Equal("Hello XXXXX!", result);
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
        Assert.Equal("Hello [Custom_1]!", result);
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
        Assert.Equal("Hello [Custom_2]!", result);
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
        Assert.Equal("Hello [Custom_1]!", result);
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
        Assert.Equal("tada!", result);
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
        Assert.Equal("Hello [Custom_1]!", result);
    }

    // --- Tokenize longer values first ---

    [Fact]
    public void TokenizeStructuredResult_ReplacesLongerValuesFirst()
    {
        var sut = CreateService();
        sut.Tokenize("Maria Schmidt", "Person");
        sut.Tokenize("Maria", "Person");

        var result = sut.TokenizeStructuredResult("Contact Maria Schmidt today");
        Assert.Equal("Contact [Person_1] today", result);
    }

    // --- TokenizeVerbatim ---

    private TokenMapService CreateVerbatimService()
    {
        var sut = new TokenMapService(new StructuredPiiDetector(), _memoryService, _settingsService);
        sut.Tokenize("Max", "Person");
        sut.Tokenize("Schmidt", "Custom");
        sut.Tokenize("maria@example.com", "Email");
        return sut;
    }

    [Theory]
    [InlineData("max(a, b)", "max(a, b)")]
    [InlineData("maximum", "maximum")]
    [InlineData("MaxLength", "MaxLength")]
    [InlineData("maria-schmidt.md", "maria-schmidt.md")]
    [InlineData("Schmitt.pdf", "Schmitt.pdf")]
    [InlineData("SCHMIDT and Schmidts", "SCHMIDT and Schmidts")]
    [InlineData("ÜMax, Max2 and Maxé", "ÜMax, Max2 and Maxé")]
    [InlineData("Max(a, b)", "[Person_1](a, b)")]
    [InlineData("Max_Mustermann.pdf", "[Person_1]_Mustermann.pdf")]
    [InlineData("Schmidt.pdf", "[Custom_1].pdf")]
    [InlineData("maria-Schmidt.md", "maria-[Custom_1].md")]
    [InlineData("src/Max/'Max' \"Max\" (Max) [Max] Max\tMax", "src/[Person_1]/'[Person_1]' \"[Person_1]\" ([Person_1]) [[Person_1]] [Person_1]\t[Person_1]")]
    [InlineData("Hallo\\nMax and C:\\Max", "Hallo\\n[Person_1] and C:\\[Person_1]")]
    public void TokenizeVerbatim_MatchesWholeWordsInExactCaseOnly(string input, string expected)
    {
        var sut = CreateVerbatimService();

        Assert.Equal(expected, sut.TokenizeVerbatim(input));
    }

    [Fact]
    public void TokenizeVerbatim_TreatsAJsonEscapeAsTheEndOfAWord()
    {
        var sut = CreateVerbatimService();
        var json = System.Text.Json.JsonSerializer.Serialize("Hallo\nMax, \"Max\" and Max+Schmidt");

        var result = sut.TokenizeVerbatim(json);

        Assert.DoesNotContain("Max", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Schmidt", result, StringComparison.Ordinal);
        Assert.Equal(json, sut.Detokenize(result));
    }

    [Theory]
    [InlineData("total_lines=3\n1|var m = max(a, b); // Max wrote this\n2|var maximum = Max(m, 0);\n3|// see maria-schmidt.md")]
    [InlineData("Mail Maria@Example.com, maria@example.com or anna.berg@example.org; call (+49) 30 1234567.")]
    [InlineData("Id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 for Max\r\nMÜLLER Müller Schmidt\u0301 Schmidt")]
    [InlineData("Unknown tokens stay: [Person_99] [Chapter 1] [person_1] Max")]
    [InlineData("")]
    public void TokenizeVerbatim_ThenDetokenize_ReturnsTheInput(string input)
    {
        var sut = CreateVerbatimService();

        Assert.Equal(input, sut.Detokenize(sut.TokenizeVerbatim(input)));
    }

    [Fact]
    public void TokenizeVerbatim_LeavesExistingTokensAndGuidsWhole()
    {
        var sut = new TokenMapService(new StructuredPiiDetector(), _memoryService, _settingsService);
        sut.Tokenize("Custom", "Person");
        sut.Tokenize("1234", "Custom");
        const string guid = "3f2504e0-4f89-1234-9a0c-0305e82c3301";
        var input = $"[Custom_2] {guid} Custom 1234 {guid}@reply.example.org";

        var result = sut.TokenizeVerbatim(input);

        Assert.Equal($"[Custom_2] {guid} [Person_1] [Custom_1] [Email_1]", result);
        Assert.Equal(input, sut.Detokenize(result));
    }

    [Fact]
    public void TokenizeVerbatim_FollowsTheMapAcrossCallsAndClear()
    {
        var sut = CreateVerbatimService();
        Assert.Equal("Anna and [Person_1]", sut.TokenizeVerbatim("Anna and Max"));

        sut.Tokenize("Anna", "Person");
        Assert.Equal("[Person_2] and [Person_1]", sut.TokenizeVerbatim("Anna and Max"));

        sut.Clear();
        Assert.Equal("Anna and Max", sut.TokenizeVerbatim("Anna and Max"));

        sut.Tokenize("Anna", "Person");
        Assert.Equal("[Person_1] and Max", sut.TokenizeVerbatim("Anna and Max"));
    }

    [Fact]
    public void TokenizeVerbatim_GivesACaseVariantOfAKnownEmailItsOwnToken()
    {
        var sut = CreateVerbatimService();

        var result = sut.TokenizeVerbatim("Maria@Example.com and maria@example.com");

        Assert.Equal("[Email_2] and [Email_1]", result);
        Assert.Equal("Maria@Example.com and maria@example.com", sut.Detokenize(result));
        // The case-insensitive side is unchanged: the first spelling keeps its token everywhere else.
        Assert.Equal("[Email_1]", sut.GetToken("MARIA@EXAMPLE.COM", "Email"));
        Assert.Equal("[Email_1]", sut.TokenizeStructuredResult("MARIA@EXAMPLE.COM"));
        Assert.Equal("[Email_1]", sut.Tokenize("Maria@Example.com", "Email"));
    }

    [Fact]
    public async Task TokenizeVerbatim_MasksEverySpellingOfAValueStoredInSeveralCases()
    {
        var settings = new AppSettings();
        settings.Privacy.PiiKeywords.Add(new PiiKeywordEntry { Keyword = "anna berg" });
        _settingsService.GetSettingsAsync().Returns(settings);
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.PersonalProfile).Returns(new List<MemoryObject>
        {
            new() { Type = MemoryObjectTypes.PersonalProfile, Label = "Profile", Data = """{"name": "Anna Berg"}""" },
        });
        _memoryService.GetObjectsByTypeAsync(MemoryObjectTypes.ContactList).Returns(new List<MemoryObject>
        {
            new() { Type = MemoryObjectTypes.ContactList, Label = "Contacts", Data = """{"contacts": [{"name": "ANNA BERG"}]}""" },
        });
        var sut = new TokenMapService(new StructuredPiiDetector(), _memoryService, _settingsService);
        await sut.InitializeAsync();
        const string mail = "Mail from Anna Berg, ANNA BERG and anna berg";

        var result = sut.TokenizeVerbatim(mail);

        Assert.DoesNotContain("anna berg", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mail, sut.Detokenize(result));
        Assert.Equal("Mail from [Custom_1], [Custom_1] and [Custom_1]", sut.TokenizeStructuredResult(mail));
        Assert.Equal("anna berg", sut.Detokenize("[Custom_1]"));
    }

    [Fact]
    public void TokenizeVerbatim_MasksACaseVariantStoredAfterTheMatcherWasBuilt()
    {
        var sut = CreateVerbatimService();
        Assert.Equal("MAX", sut.TokenizeVerbatim("MAX"));

        Assert.Equal("[Person_1]", sut.Tokenize("MAX", "Person"));
        var result = sut.TokenizeVerbatim("MAX and Max");

        Assert.DoesNotContain("MAX", result, StringComparison.Ordinal);
        Assert.Equal("MAX and Max", sut.Detokenize(result));
    }

    [Theory]
    [InlineData("see 0301234567.txt", "see [Phone_1].txt")]
    [InlineData("Tel.0301234567", "Tel.[Phone_1]")]
    public void TokenizeVerbatim_MasksADetectedNumberLaterWhereTheDetectorMissesIt(string later, string expected)
    {
        var sut = CreateVerbatimService();
        Assert.Equal("call [Phone_1]", sut.TokenizeVerbatim("call 0301234567"));

        var result = sut.TokenizeVerbatim(later);

        Assert.Equal(expected, result);
        Assert.Equal(later, sut.Detokenize(result));
    }

    [Fact]
    public void TokenizeVerbatim_MasksADetectedNumberInTheSameTextWhereTheDetectorMissesIt()
    {
        var sut = CreateVerbatimService();

        Assert.Equal("call [Phone_1], see [Phone_1].txt", sut.TokenizeVerbatim("call 0301234567, see 0301234567.txt"));
    }

    [Theory]
    [InlineData("born 1990-05-12T00:00:00", "born [Date_1]T00:00:00")]
    [InlineData("Kd123456, 123456kg and (123456)", "Kd[Custom_2], [Custom_2]kg and ([Custom_2])")]
    [InlineData("1234567 and 9123456", "1234567 and 9123456")]
    [InlineData("MaxLength, Max2 and maximum", "MaxLength, Max2 and maximum")]
    public void TokenizeVerbatim_LetsALetterButNotADigitTouchADigitAtTheEdgeOfAValue(string input, string expected)
    {
        var sut = CreateVerbatimService();
        sut.Tokenize("123456", "Custom");
        sut.Tokenize("1990-05-12", "Date");

        var result = sut.TokenizeVerbatim(input);

        Assert.Equal(expected, result);
        Assert.Equal(input, sut.Detokenize(result));
    }
}
