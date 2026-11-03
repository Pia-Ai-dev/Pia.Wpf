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

    [Theory]
    [InlineData("Call +49 170 1234567 today", "+49 170 1234567")]
    [InlineData("Call +1-555-123-4567 today", "+1-555-123-4567")]
    [InlineData("Tel. +49 (0)30 1234567", "+49 (0)30 1234567")]
    [InlineData("Tel. +49 (0) 30 1234567", "+49 (0) 30 1234567")]
    [InlineData("Tel. +49-30-1234-5678", "+49-30-1234-5678")]
    [InlineData("Tel. 0049 30 1234567", "0049 30 1234567")]
    [InlineData("Tel.: 030 1234567", "030 1234567")]
    [InlineData("Tel.: 030/1234567", "030/1234567")]
    [InlineData("Mobil: 0171/1234567", "0171/1234567")]
    [InlineData("Zentrale 030 / 123 456-0.", "030 / 123 456-0")]
    [InlineData("Ruf an: (030) 123 4567", "(030) 123 4567")]
    [InlineData("Mobil: 0171 1234567, Fax folgt", "0171 1234567")]
    [InlineData("Mobil: 01711234567", "01711234567")]
    [InlineData("Hotline 0800-1234567", "0800-1234567")]
    public void DetectPii_FindsPhoneNumber(string text, string phone)
    {
        var match = Assert.Single(_sut.DetectPii(text));

        Assert.Equal("Phone", match.Category);
        Assert.Equal(phone, match.Value);
        Assert.Equal(phone, text.Substring(match.Start, match.Length));
    }

    [Fact]
    public void DetectPii_FindsPhoneNumberWithNoBreakSpaces()
    {
        var phone = "+49 30 12345678".Replace(' ', (char)0xA0);

        var match = Assert.Single(_sut.DetectPii($"Tel. {phone}"));

        Assert.Equal(phone, match.Value);
    }

    [Theory]
    [InlineData("Citrix Workspace App x64 in Version 26.3.20.31")]
    [InlineData("Plugin 26.29.1.4")]
    [InlineData("Teams 1.7.00.12345")]
    [InlineData("Windows 10.0.26100.1234")]
    [InlineData("am 12.03.2026")]
    [InlineData("am 2026-03-27")]
    [InlineData("am 01-12-2026")]
    [InlineData("am 01/12/2026")]
    [InlineData("2026/08/26 09:00 UTC")]
    [InlineData("Date: 2026-08-31 11:46 +00:00")]
    [InlineData("IP 192.168.10.25")]
    [InlineData("Betrag 1.234.567,89 EUR")]
    [InlineData("Musterweg 22\n51674 Musterstadt")]
    [InlineData("Musterweg 22\r\n51674 Musterstadt")]
    [InlineData("Bestellnummer 1023456789")]
    [InlineData("Ticket 1234567")]
    [InlineData("ISBN 978-0-123456-47-2")]
    [InlineData("ID a1b2c3d4-0123-4567-89ab-cdef01234567")]
    public void DetectPii_LeavesNumbersThatAreNotPhoneNumbersAlone(string text)
    {
        Assert.Empty(_sut.DetectPii(text));
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
