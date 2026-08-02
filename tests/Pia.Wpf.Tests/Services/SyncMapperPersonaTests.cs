namespace Pia.Tests.Services;

using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
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
        OutputFormat = "- Lead with the answer.\n- Use code blocks for code.",
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
        Assert.Equal(original.OutputFormat, sync.OutputFormat);
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
        Assert.Equal(original.OutputFormat, back.OutputFormat);
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
        Assert.Null(sync.OutputFormat);
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
        Assert.Equal(original.OutputFormat, back.OutputFormat);
        Assert.Equal(original.Expertise, back.Expertise);
        Assert.Equal(original.Archetype, back.Archetype);
        Assert.Equal(original.ToolScope, back.ToolScope);
        Assert.Equal(original.PreferredProviderId, back.PreferredProviderId);
        Assert.Equal(original.ReasoningEffort, back.ReasoningEffort);
        Assert.False(back.IsBuiltIn);
    }

    // --- Managed personas (pull-only, admin-published) ---

    private static SyncManagedPersona SampleManagedPersona() => new()
    {
        Id = Guid.Parse("6f1b3f2a-9c44-4d1e-8b77-2a0d5e91c4aa"),
        Name = "Brandvoice",
        Tagline = "Rewrites anything in our house voice",
        SystemPrompt = "You are the company's brand voice editor.",
        Guardrails = "Never invent product claims.",
        OutputFormat = "Return only the rewritten text, no preamble.",
        Expertise = ["copywriting", "brand", "editing"],
        Archetype = "creative",
        Emoji = "🎨",
        AccentColor = "#7A5AF8",
        ToolScope = (int)PersonaToolScope.Full,
        ReasoningEffort = (int)Pia.Models.ReasoningEffort.High,
        SchemaVersion = 1,
        CreatedAt = new DateTime(2026, 7, 30, 11, 2, 41, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 8, 1, 8, 55, 10, DateTimeKind.Utc),
        IsManaged = true,
    };

    [Fact]
    public void FromSyncManagedPersona_MapsEveryField()
    {
        var mapper = PlainMapper();
        var sync = SampleManagedPersona();

        var persona = mapper.FromSyncManagedPersona(sync);

        Assert.Equal(sync.Id, persona.Id);
        Assert.Equal("Brandvoice", persona.Name);
        Assert.Equal("Rewrites anything in our house voice", persona.Tagline);
        Assert.Equal("You are the company's brand voice editor.", persona.SystemPrompt);
        Assert.Equal("Never invent product claims.", persona.Guardrails);
        Assert.Equal("Return only the rewritten text, no preamble.", persona.OutputFormat);
        Assert.Equal(new List<string> { "copywriting", "brand", "editing" }, persona.Expertise);
        Assert.Equal("creative", persona.Archetype);
        Assert.Equal("🎨", persona.Emoji);
        Assert.Equal("#7A5AF8", persona.AccentColor);
        // int → enum for the structural fields.
        Assert.Equal(PersonaToolScope.Full, persona.ToolScope);
        Assert.Equal(Pia.Models.ReasoningEffort.High, persona.ReasoningEffort);
        Assert.Equal(1, persona.SchemaVersion);
        Assert.Equal(sync.CreatedAt, persona.CreatedAt);
        Assert.Equal(sync.UpdatedAt, persona.UpdatedAt);
        // Provenance: managed, never built-in, and therefore read-only in the editor.
        Assert.True(persona.IsManaged);
        Assert.False(persona.IsBuiltIn);
        Assert.True(persona.IsReadOnly);
        // Q8: the DTO has no preferredProviderId, so a managed persona resolves to the member's mode
        // default exactly as a user persona with no preference does.
        Assert.Null(persona.PreferredProviderId);
    }

    [Fact]
    public void FromSyncManagedPersona_AppliesTolerantDefaults()
    {
        var mapper = PlainMapper();
        // Every nullable wire field omitted (the server elides nulls entirely), blank archetype.
        var sync = new SyncManagedPersona
        {
            Id = Guid.NewGuid(),
            Archetype = "   ",
            ToolScope = (int)PersonaToolScope.ReadOnly,
            SchemaVersion = 1,
        };

        var persona = mapper.FromSyncManagedPersona(sync);

        // Expertise null ⇒ empty list, never null (the UI binds it directly).
        Assert.Empty(persona.Expertise);
        // Blank/absent archetype ⇒ "custom", the shared vocabulary's fallback.
        Assert.Equal("custom", persona.Archetype);
        // Absent reasoningEffort ⇒ null (inherit), not None.
        Assert.Null(persona.ReasoningEffort);
        Assert.Equal(PersonaToolScope.ReadOnly, persona.ToolScope);
        // Name/SystemPrompt are `required` locally but nullable on the wire: a malformed row degrades to
        // empty strings rather than throwing, so one bad row cannot abort the whole pull.
        Assert.Equal("", persona.Name);
        Assert.Equal("", persona.SystemPrompt);
        Assert.Null(persona.Tagline);
        Assert.Null(persona.Guardrails);
        Assert.Null(persona.OutputFormat);
    }

    [Fact]
    public void FromSyncManagedPersona_BlankArchetypeVariants_AllMapToCustom()
    {
        var mapper = PlainMapper();

        // "absent/blank ⇒ custom" (handoff §2.1.1) includes whitespace-only: Archetype indexes a closed
        // vocabulary, so "   " would match no glyph and no label.
        foreach (var archetype in new[] { null, "", "   ", "\t" })
        {
            var sync = SampleManagedPersona();
            sync.Archetype = archetype;
            Assert.Equal("custom", mapper.FromSyncManagedPersona(sync).Archetype);
        }
    }

    [Fact]
    public void FromSyncPersona_BlankArchetypeVariants_AllMapToCustom()
    {
        var mapper = PlainMapper();

        // The user-persona mapper is held to the same rule, deliberately: MapPersona and both mappers
        // treat the two persona flavours as column-identical, so a divergence here would show up as one
        // kind of persona rendering an archetype the picker cannot label.
        foreach (var archetype in new[] { null, "", "   ", "\t" })
        {
            var sync = mapper.ToSyncPersona(SamplePersona());
            sync.Archetype = archetype;
            Assert.Equal("custom", mapper.FromSyncPersona(sync).Archetype);
        }
    }

    [Fact]
    public void FromSyncManagedPersona_UnderE2EE_StillMapsPlaintext()
    {
        // Handoff §5.3: managed rows are plaintext even for an E2EE-enabled account, because a
        // group-shared row cannot be wrapped with any single user's UMK. The mapper here has a live,
        // ready E2EEService (IsE2EEActive == true) — the same one the encrypted user-persona test uses —
        // and the managed mapping must ignore it entirely rather than look for a payload to decrypt.
        var mapper = E2EEMapper();
        var sync = SampleManagedPersona();

        var persona = mapper.FromSyncManagedPersona(sync);

        Assert.Equal("Brandvoice", persona.Name);
        Assert.Equal("You are the company's brand voice editor.", persona.SystemPrompt);
        Assert.Equal("Never invent product claims.", persona.Guardrails);
        Assert.Equal(new List<string> { "copywriting", "brand", "editing" }, persona.Expertise);
        Assert.True(persona.IsManaged);
    }

    [Fact]
    public void SyncManagedPersona_HasNoE2EEFields()
    {
        // The absence of these fields is what makes a decrypt branch impossible to write, so it is the
        // real pin for §5.3. If they ever reappear on the DTO, FromSyncManagedPersona needs revisiting.
        var names = typeof(SyncManagedPersona).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.DoesNotContain("EncryptedPayload", names);
        Assert.DoesNotContain("WrappedDek", names);
        Assert.DoesNotContain("UserId", names);
        // Q8: deliberately absent — see FromSyncManagedPersona's comment.
        Assert.DoesNotContain("PreferredProviderId", names);
    }

    [Fact]
    public void SyncMapper_HasNoManagedPersonaPushDirection()
    {
        // The managed channel is pull-only: the client never authors these rows, and a push direction
        // would be a write path into another user's shared data. Expressed by reflection so it fails the
        // day someone adds one.
        var methods = typeof(SyncMapper)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();

        // Non-vacuity: the pull direction must be there, or the assertion below proves nothing.
        Assert.Contains(nameof(SyncMapper.FromSyncManagedPersona), methods);
        Assert.DoesNotContain("ToSyncManagedPersona", methods);
        // Also nothing that merely returns the DTO under another name.
        Assert.DoesNotContain(
            typeof(SyncMapper).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            m => m.ReturnType == typeof(SyncManagedPersona));
    }
}
