namespace Pia.Tests.Services;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Xunit;

/// <summary>
/// Persona mapper round-trip coverage in both plaintext (E2EE off) and encrypted (E2EE on) modes.
/// Verifies the E2EE field split from contract §3: textual fields (Name/Tagline/SystemPrompt/
/// Guardrails/Expertise) are encrypted; structural fields stay plaintext.
/// </summary>
public class SyncMapperPersonaTests
{
    private const string UserId = "user-123";

    private static SyncMapper PlainMapper()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        return new SyncMapper(dpapi);
    }

    private static SyncMapper E2EEMapper()
    {
        var crypto = new CryptoService();
        var deviceKeys = Substitute.For<IDeviceKeyService>();
        deviceKeys.GetDeviceId().Returns("dev-test");

        var dpapi = Substitute.ForPartsOf<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(c => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Arg<string>())));
        dpapi.Decrypt(Arg.Any<string>())
            .Returns(c => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(c.Arg<string>())));

        var appSettings = new AppSettings { IsE2EEEnabled = true };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings);

        var e2ee = new E2EEService(crypto, deviceKeys, dpapi, settings, NullLogger<E2EEService>.Instance);
        e2ee.GenerateAndStoreUmkAsync().GetAwaiter().GetResult();

        return new SyncMapper(dpapi, e2ee);
    }

    private static Persona SamplePersona() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Experienced Coder",
        Tagline = "Senior engineer, production-minded",
        SystemPrompt = "You are a senior software engineer with 15+ years of experience.",
        Guardrails = "Flag security concerns proactively.",
        Archetype = "analyst",
        Expertise = ["C#", "Distributed Systems", "Security"],
        Emoji = "💻",
        AccentColor = "#00C853",
        ToolScope = PersonaToolScope.Full,
        PreferredProviderId = Guid.NewGuid(),
        ReasoningEffort = ReasoningEffort.High,
        SchemaVersion = 1,
        IsBuiltIn = false,
        CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Persona_RoundTrips_Plaintext()
    {
        var mapper = PlainMapper();
        var original = SamplePersona();

        var sync = mapper.ToSyncPersona(original);

        Assert.Null(sync.EncryptedPayload);
        Assert.Null(sync.WrappedDek);
        Assert.Equal(original.Name, sync.Name);
        Assert.Equal(original.SystemPrompt, sync.SystemPrompt);
        Assert.Equal(original.Guardrails, sync.Guardrails);
        Assert.Equal(original.Expertise, sync.Expertise);
        // Structural fields stay plaintext in both modes.
        Assert.Equal("analyst", sync.Archetype);
        Assert.Equal((int)PersonaToolScope.Full, sync.ToolScope);
        Assert.Equal(original.PreferredProviderId, sync.PreferredProviderId);
        Assert.Equal((int)ReasoningEffort.High, sync.ReasoningEffort);

        var back = mapper.FromSyncPersona(sync);

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Tagline, back.Tagline);
        Assert.Equal(original.SystemPrompt, back.SystemPrompt);
        Assert.Equal(original.Guardrails, back.Guardrails);
        Assert.Equal(original.Expertise, back.Expertise);
        Assert.Equal(original.Archetype, back.Archetype);
        Assert.Equal(original.Emoji, back.Emoji);
        Assert.Equal(original.AccentColor, back.AccentColor);
        Assert.Equal(original.ToolScope, back.ToolScope);
        Assert.Equal(original.PreferredProviderId, back.PreferredProviderId);
        Assert.Equal(original.ReasoningEffort, back.ReasoningEffort);
        Assert.Equal(original.CreatedAt, back.CreatedAt);
        Assert.Equal(original.UpdatedAt, back.UpdatedAt);
        // Synced personas are never built-in.
        Assert.False(back.IsBuiltIn);
    }

    [Fact]
    public void Persona_RoundTrips_Encrypted()
    {
        var mapper = E2EEMapper();
        var original = SamplePersona();

        var sync = mapper.ToSyncPersona(original, UserId);

        // Textual fields go into the encrypted blob; their plaintext is nulled on the wire.
        Assert.NotNull(sync.EncryptedPayload);
        Assert.NotNull(sync.WrappedDek);
        Assert.Null(sync.Name);
        Assert.Null(sync.Tagline);
        Assert.Null(sync.SystemPrompt);
        Assert.Null(sync.Guardrails);
        Assert.Null(sync.Expertise);
        // Structural fields remain plaintext even under E2EE.
        Assert.Equal(original.Id, sync.Id);
        Assert.Equal("analyst", sync.Archetype);
        Assert.Equal("💻", sync.Emoji);
        Assert.Equal("#00C853", sync.AccentColor);
        Assert.Equal((int)PersonaToolScope.Full, sync.ToolScope);
        Assert.Equal(original.PreferredProviderId, sync.PreferredProviderId);
        Assert.Equal((int)ReasoningEffort.High, sync.ReasoningEffort);
        Assert.Equal(original.CreatedAt, sync.CreatedAt);
        Assert.Equal(original.UpdatedAt, sync.UpdatedAt);

        var back = mapper.FromSyncPersona(sync, UserId);

        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Tagline, back.Tagline);
        Assert.Equal(original.SystemPrompt, back.SystemPrompt);
        Assert.Equal(original.Guardrails, back.Guardrails);
        Assert.Equal(original.Expertise, back.Expertise);
        Assert.Equal(original.Archetype, back.Archetype);
        Assert.Equal(original.ToolScope, back.ToolScope);
        Assert.Equal(original.PreferredProviderId, back.PreferredProviderId);
        Assert.Equal(original.ReasoningEffort, back.ReasoningEffort);
        Assert.False(back.IsBuiltIn);
    }
}
