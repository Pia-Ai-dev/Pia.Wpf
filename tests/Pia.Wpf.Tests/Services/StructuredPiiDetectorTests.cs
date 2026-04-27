using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class StructuredPiiDetectorTests
{
    private readonly StructuredPiiDetector _sut = new();

    // --- DetectPiiInStructured: personal_profile ---

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsName()
    {
        var json = """{"name": "Maria Schmidt", "nickname": "Mia"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "Maria Schmidt" && m.Category == "Person");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsNickname()
    {
        var json = """{"name": "Maria Schmidt", "nickname": "Mia"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "Mia" && m.Category == "Person");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsEmail()
    {
        var json = """{"email": "maria@example.com"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "maria@example.com" && m.Category == "Email");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsPhone()
    {
        var json = """{"phone": "+49 170 1234567"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "+49 170 1234567" && m.Category == "Phone");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsAddress()
    {
        var json = """{"address": "Hauptstr. 12, Berlin"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "Hauptstr. 12, Berlin" && m.Category == "Address");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsBirthdate()
    {
        var json = """{"birthdate": "1985-03-05"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "1985-03-05" && m.Category == "Date");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsLocation()
    {
        var json = """{"location": "Berlin, Germany"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Contains(matches, m => m.Value == "Berlin, Germany" && m.Category == "Address");
    }

    // --- DetectPiiInStructured: contact_list ---

    [Fact]
    public void DetectPiiInStructured_ContactList_ExtractsContactFields()
    {
        var json = """{"contacts": [{"name": "Hans Müller", "email": "hans@test.de", "phone": "+49 30 9876543"}]}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.ContactList);
        Assert.Contains(matches, m => m.Value == "Hans Müller" && m.Category == "Person");
        Assert.Contains(matches, m => m.Value == "hans@test.de" && m.Category == "Email");
        Assert.Contains(matches, m => m.Value == "+49 30 9876543" && m.Category == "Phone");
    }

    [Fact]
    public void DetectPiiInStructured_ContactList_ExtractsNestedAddressAndBirthdate()
    {
        var json = """{"contacts": [{"name": "Anna", "address": "Berliner Str. 5", "birthdate": "1990-01-15"}]}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.ContactList);
        Assert.Contains(matches, m => m.Value == "Berliner Str. 5" && m.Category == "Address");
        Assert.Contains(matches, m => m.Value == "1990-01-15" && m.Category == "Date");
    }

    // --- DetectPiiInStructured: preference ---

    [Fact]
    public void DetectPiiInStructured_Preference_ReturnsEmpty()
    {
        var json = """{"preference": "dark mode", "value": "enabled"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.Preference);
        Assert.Empty(matches);
    }

    // --- DetectPiiInStructured: note (cross-match only) ---

    [Fact]
    public void DetectPiiInStructured_Note_ReturnsEmpty()
    {
        var json = """{"title": "Meeting notes", "content": "Discussed project timeline"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.Note);
        Assert.Empty(matches);
    }

    // --- DetectPii: regex-based email/phone detection in freeform text ---

    [Fact]
    public void DetectPii_FindsEmailInText()
    {
        var matches = _sut.DetectPii("Contact us at info@example.com for details");
        Assert.Contains(matches, m => m.Value == "info@example.com" && m.Category == "Email");
    }

    [Fact]
    public void DetectPii_FindsPhoneInText()
    {
        var matches = _sut.DetectPii("Call +49 170 1234567 or +1-555-123-4567");
        Assert.True(matches.Count() >= 1);
        Assert.All(matches, m => Assert.Equal("Phone", m.Category));
    }

    [Fact]
    public void DetectPii_NoMatchesInCleanText()
    {
        var matches = _sut.DetectPii("The weather today is nice.");
        Assert.Empty(matches);
    }

    // --- DetectPii: phone false-positive regression cases ---
    // These look digit-heavy but are not phone numbers. Previously the regex
    // (\+?[\d\s\-().]{7,20}\d) matched them, which corrupted file paths like
    // %APPDATA%\Pia\assistant\meetings\transcript-20260427-143025.md.

    [Theory]
    [InlineData("transcript-20260427-143025.md")]
    [InlineData("transcript_20260427_143025.md")]
    [InlineData("Please summarize the meeting transcript saved at `%APPDATA%\\Pia\\assistant\\meetings\\transcript_2026_04_27_14h30m25s.md`.")]
    [InlineData("ISO date 2026-04-27 stays untouched")]
    [InlineData("ISO datetime 2026-04-27T14:30:25 is not a phone")]
    [InlineData("Order #1234567 was shipped")]
    public void DetectPii_DoesNotMatchTimestampOrIdAsPhone(string text)
    {
        var matches = _sut.DetectPii(text);
        Assert.DoesNotContain(matches, m => m.Category == "Phone");
    }

    [Theory]
    [InlineData("+49 175 5555555")]
    [InlineData("+1 (555) 555-5555")]
    [InlineData("(030) 1234-5678")]
    [InlineData("+4915755555555")]
    [InlineData("030-123-4567")]
    public void DetectPii_StillMatchesRealPhoneFormats(string text)
    {
        var matches = _sut.DetectPii(text);
        Assert.Contains(matches, m => m.Category == "Phone");
    }

    // --- Edge cases ---

    [Fact]
    public void DetectPiiInStructured_EmptyJson_ReturnsEmpty()
    {
        var matches = _sut.DetectPiiInStructured("{}", MemoryObjectTypes.PersonalProfile);
        Assert.Empty(matches);
    }

    [Fact]
    public void DetectPiiInStructured_NullFields_ReturnsEmpty()
    {
        var json = """{"name": null, "email": null}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Empty(matches);
    }

    [Fact]
    public void DetectPiiInStructured_EmptyStringFields_ReturnsEmpty()
    {
        var json = """{"name": "", "email": ""}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        Assert.Empty(matches);
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_MultipleFields()
    {
        var json = """{"name": "Maria Schmidt", "email": "maria@example.com", "phone": "+49 170 1234567", "address": "Hauptstr. 12", "birthdate": "1985-03-05"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        var categories = matches.Select(m => m.Category).ToList();
        Assert.Equal(5, categories.Count);
        Assert.Contains("Person", categories);
        Assert.Contains("Email", categories);
        Assert.Contains("Phone", categories);
        Assert.Contains("Address", categories);
        Assert.Contains("Date", categories);
    }
}
