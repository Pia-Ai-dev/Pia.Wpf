namespace Pia.Tests.E2EE;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

public class E2EEServiceTests
{
    private readonly CryptoService _crypto = new();
    private readonly IDeviceKeyService _deviceKeysMock;
    private readonly DpapiHelper _dpapiMock;
    private readonly ISettingsService _settingsMock;
    private readonly E2EEService _sut;
    private readonly AppSettings _settings;

    public E2EEServiceTests()
    {
        _settings = new AppSettings();
        _settingsMock = Substitute.For<ISettingsService>();
        _settingsMock.GetSettingsAsync().Returns(_settings);

        // Mock DpapiHelper as identity function for testing
        _dpapiMock = Substitute.ForPartsOf<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        _dpapiMock.Encrypt(Arg.Any<string>())
            .Returns(c => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Arg<string>())));
        _dpapiMock.Decrypt(Arg.Any<string>())
            .Returns(c => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(c.Arg<string>())));

        _deviceKeysMock = Substitute.For<IDeviceKeyService>();
        _deviceKeysMock.GetDeviceId().Returns("test-device");

        _sut = new E2EEService(_crypto, _deviceKeysMock, _dpapiMock,
            _settingsMock, NullLogger<E2EEService>.Instance);
    }

    [Fact]
    public async Task GenerateAndStoreUmkAsync_ShouldReturn32Bytes()
    {
        var umk = await _sut.GenerateAndStoreUmkAsync();
        Assert.Equal(32, umk.Length);
        Assert.True(_sut.HasUmk());
    }

    [Fact]
    public async Task EncryptRecord_DecryptRecord_RoundTrip()
    {
        await _sut.GenerateAndStoreUmkAsync();

        var template = new SyncTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Test Template",
            Prompt = "Write a professional email",
            CreatedAt = DateTime.UtcNow
        };

        var (encryptedPayload, wrappedDek) = _sut.EncryptRecord(
            template, "user123", "template", template.Id.ToString());

        Assert.NotEmpty(encryptedPayload);
        Assert.NotEmpty(wrappedDek);

        var decrypted = _sut.DecryptRecord<SyncTemplate>(
            encryptedPayload, wrappedDek, "user123", "template", template.Id.ToString());

        Assert.Equal(template.Name, decrypted.Name);
        Assert.Equal(template.Prompt, decrypted.Prompt);
    }

    [Fact]
    public async Task DecryptRecord_WrongEntityId_ShouldThrow()
    {
        await _sut.GenerateAndStoreUmkAsync();

        var template = new SyncTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Prompt = "Test",
            CreatedAt = DateTime.UtcNow
        };

        var (encryptedPayload, wrappedDek) = _sut.EncryptRecord(
            template, "user123", "template", template.Id.ToString());

        // Try to decrypt with different entityId (AAD mismatch)
        Assert.ThrowsAny<Exception>(() => _sut.DecryptRecord<SyncTemplate>(
            encryptedPayload, wrappedDek, "user123", "template", Guid.NewGuid().ToString()));
    }
}
