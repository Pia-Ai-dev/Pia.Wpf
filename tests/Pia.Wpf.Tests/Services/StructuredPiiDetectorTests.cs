using FluentAssertions;
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
        matches.Should().Contain(m => m.Value == "Maria Schmidt" && m.Category == "Person");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsNickname()
    {
        var json = """{"name": "Maria Schmidt", "nickname": "Mia"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().Contain(m => m.Value == "Mia" && m.Category == "Person");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsEmail()
    {
        var json = """{"email": "maria@example.com"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().Contain(m => m.Value == "maria@example.com" && m.Category == "Email");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsPhone()
    {
        var json = """{"phone": "+49 170 1234567"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().Contain(m => m.Value == "+49 170 1234567" && m.Category == "Phone");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsAddress()
    {
        var json = """{"address": "Hauptstr. 12, Berlin"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().Contain(m => m.Value == "Hauptstr. 12, Berlin" && m.Category == "Address");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsBirthdate()
    {
        var json = """{"birthdate": "1985-03-05"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().Contain(m => m.Value == "1985-03-05" && m.Category == "Date");
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_ExtractsLocation()
    {
        var json = """{"location": "Berlin, Germany"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().Contain(m => m.Value == "Berlin, Germany" && m.Category == "Address");
    }

    // --- DetectPiiInStructured: contact_list ---

    [Fact]
    public void DetectPiiInStructured_ContactList_ExtractsContactFields()
    {
        var json = """{"contacts": [{"name": "Hans Müller", "email": "hans@test.de", "phone": "+49 30 9876543"}]}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.ContactList);
        matches.Should().Contain(m => m.Value == "Hans Müller" && m.Category == "Person");
        matches.Should().Contain(m => m.Value == "hans@test.de" && m.Category == "Email");
        matches.Should().Contain(m => m.Value == "+49 30 9876543" && m.Category == "Phone");
    }

    [Fact]
    public void DetectPiiInStructured_ContactList_ExtractsNestedAddressAndBirthdate()
    {
        var json = """{"contacts": [{"name": "Anna", "address": "Berliner Str. 5", "birthdate": "1990-01-15"}]}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.ContactList);
        matches.Should().Contain(m => m.Value == "Berliner Str. 5" && m.Category == "Address");
        matches.Should().Contain(m => m.Value == "1990-01-15" && m.Category == "Date");
    }

    // --- DetectPiiInStructured: preference ---

    [Fact]
    public void DetectPiiInStructured_Preference_ReturnsEmpty()
    {
        var json = """{"preference": "dark mode", "value": "enabled"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.Preference);
        matches.Should().BeEmpty();
    }

    // --- DetectPiiInStructured: note (cross-match only) ---

    [Fact]
    public void DetectPiiInStructured_Note_ReturnsEmpty()
    {
        var json = """{"title": "Meeting notes", "content": "Discussed project timeline"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.Note);
        matches.Should().BeEmpty();
    }

    // --- DetectPii: regex-based email/phone detection in freeform text ---

    [Fact]
    public void DetectPii_FindsEmailInText()
    {
        var matches = _sut.DetectPii("Contact us at info@example.com for details");
        matches.Should().Contain(m => m.Value == "info@example.com" && m.Category == "Email");
    }

    [Fact]
    public void DetectPii_FindsPhoneInText()
    {
        var matches = _sut.DetectPii("Call +49 170 1234567 or +1-555-123-4567");
        matches.Should().HaveCountGreaterThanOrEqualTo(1);
        matches.Should().OnlyContain(m => m.Category == "Phone");
    }

    [Fact]
    public void DetectPii_NoMatchesInCleanText()
    {
        var matches = _sut.DetectPii("The weather today is nice.");
        matches.Should().BeEmpty();
    }

    // --- Edge cases ---

    [Fact]
    public void DetectPiiInStructured_EmptyJson_ReturnsEmpty()
    {
        var matches = _sut.DetectPiiInStructured("{}", MemoryObjectTypes.PersonalProfile);
        matches.Should().BeEmpty();
    }

    [Fact]
    public void DetectPiiInStructured_NullFields_ReturnsEmpty()
    {
        var json = """{"name": null, "email": null}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().BeEmpty();
    }

    [Fact]
    public void DetectPiiInStructured_EmptyStringFields_ReturnsEmpty()
    {
        var json = """{"name": "", "email": ""}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().BeEmpty();
    }

    [Fact]
    public void DetectPiiInStructured_PersonalProfile_MultipleFields()
    {
        var json = """{"name": "Maria Schmidt", "email": "maria@example.com", "phone": "+49 170 1234567", "address": "Hauptstr. 12", "birthdate": "1985-03-05"}""";
        var matches = _sut.DetectPiiInStructured(json, MemoryObjectTypes.PersonalProfile);
        matches.Should().HaveCount(5);
        matches.Select(m => m.Category).Should().BeEquivalentTo(["Person", "Email", "Phone", "Address", "Date"]);
    }
}
