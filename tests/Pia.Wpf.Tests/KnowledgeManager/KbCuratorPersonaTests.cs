using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Shared;
using Pia.Shared.Knowledge;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed partial class KbCuratorPersonaTests : IDisposable
{
    private static readonly Guid CuratorId = Guid.Parse("0000000A-0000-0000-0000-000000000008");

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "PiaKbCurator_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteContext _ctx;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly AppSettings _settings = new();
    private readonly IKnowledgeManagerSurfaceCache _surface = Substitute.For<IKnowledgeManagerSurfaceCache>();

    public KbCuratorPersonaTests()
    {
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _deleteTracker = new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BuiltInPersona Curator() => BuiltInPersonas.All.Single(p => Guid.Parse(p.Id) == CuratorId);

    private PersonaService CreateSut(IKnowledgeManagerSurfaceCache? surface) =>
        new(_ctx, NullLogger<PersonaService>.Instance, _deleteTracker, SettingsStubs.Returning(_settings), surface);

    [Fact]
    public void TheCatalog_KnowsTheCuratorByIdKeyAndGuid()
    {
        Assert.Equal(CuratorId, BuiltInPersonas.PiaKbCuratorId);
        Assert.Equal(CuratorId, BuiltInPersonas.ByKey["PiaKbCurator"]);
        Assert.Equal(CuratorId, BuiltInPersonas.Resolve("PiaKbCurator"));
        Assert.Equal(CuratorId, BuiltInPersonas.Resolve(CuratorId.ToString()));
    }

    [Fact]
    public void TheCuratorEntry_HasTheAgreedIdentity()
    {
        var entry = Curator();

        Assert.Equal("Pia · KB Curator", entry.Name);
        Assert.Equal("Keeps your team's knowledge bases clear and current", entry.Tagline);
        Assert.Equal("📚", entry.Emoji);
        Assert.Matches("^#[0-9A-F]{6}$", entry.AccentColor);
        Assert.Equal("assistant", entry.Archetype);
        Assert.Equal(2, entry.ToolScope);
        Assert.Null(entry.ModelType);
        Assert.Equal(["Knowledge Bases", "Documentation", "Information Architecture"], entry.Expertise);
    }

    [Fact]
    public void TheAccentColor_IsNotTakenByAnotherBuiltIn()
    {
        var others = BuiltInPersonas.All.Where(p => Guid.Parse(p.Id) != CuratorId).Select(p => p.AccentColor);

        Assert.DoesNotContain(Curator().AccentColor, others);
    }

    [Fact]
    public async Task TheCurator_IsHiddenWhileTheSurfaceIsUnavailable_AndShownOnceItIs()
    {
        _surface.IsAvailable.Returns(false);
        var sut = CreateSut(_surface);

        Assert.DoesNotContain(await sut.GetPersonasAsync(), p => p.Id == CuratorId);

        _surface.IsAvailable.Returns(true);

        var listed = Assert.Single(await sut.GetPersonasAsync(), p => p.Id == CuratorId);
        Assert.True(listed.IsBuiltIn);
        Assert.Equal("Pia · KB Curator", listed.Name);
    }

    [Fact]
    public async Task WithoutASurfaceCache_TheCuratorIsNotGated()
    {
        var sut = CreateSut(null);

        Assert.Contains(await sut.GetPersonasAsync(), p => p.Id == CuratorId);
    }

    [Fact]
    public void AFlipOfTheSurface_RefreshesThePicker()
    {
        var sut = CreateSut(_surface);
        var raised = 0;
        sut.PersonasChanged += (_, _) => raised++;

        _surface.Changed += Raise.Event();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A403MidSession_HidesTheCurator_AndRefreshesThePicker()
    {
        var api = Substitute.For<IKnowledgeManagerApiClient>();
        api.ListKnowledgeBasesAsync(Arg.Any<CancellationToken>()).Returns(
            new KbManagerResult<IReadOnlyList<KbManagerKnowledgeBase>>(KbManagerCallStatus.Ok, []));
        api.GetStatsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new KbManagerResult<KbManagerStats>(KbManagerCallStatus.Forbidden));
        var cache = new KnowledgeManagerSurfaceCache(api, NullLogger<KnowledgeManagerSurfaceCache>.Instance);
        var handler = new KnowledgeManagerToolHandler(
            api, cache, Substitute.For<IFilesToolHandler>(), Substitute.For<ILocalizationService>(),
            NullLogger<KnowledgeManagerToolHandler>.Instance);
        var sut = CreateSut(cache);
        var raised = 0;
        sut.PersonasChanged += (_, _) => raised++;

        await cache.RefreshAsync(Ct);
        Assert.Contains(await sut.GetPersonasAsync(), p => p.Id == CuratorId);
        Assert.Equal(1, raised);

        await handler.HandleToolCallAsync(
            new FunctionCallContent("c", "get_knowledge_base_stats",
                new Dictionary<string, object?> { ["kb_id"] = Guid.NewGuid().ToString() }),
            Ct);

        Assert.DoesNotContain(await sut.GetPersonasAsync(), p => p.Id == CuratorId);
        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData(UserOperatingMode.Personal, "0000000A-0000-0000-0000-000000000001")]
    [InlineData(UserOperatingMode.Business, "0000000A-0000-0000-0000-000000000002")]
    public async Task AnActiveCurator_FallsBackToTheModeDefault_WhenTheSurfaceGoesAway(
        UserOperatingMode operatingMode, string expectedId)
    {
        _settings.SetPersonaForMode(WindowMode.Assistant, CuratorId);
        _surface.IsAvailable.Returns(true);
        var sut = CreateSut(_surface);

        Assert.Equal(CuratorId, (await sut.ResolveActiveAsync(WindowMode.Assistant, operatingMode)).Id);

        _surface.IsAvailable.Returns(false);

        var fallback = await sut.ResolveActiveAsync(WindowMode.Assistant, operatingMode);
        Assert.Equal(Guid.Parse(expectedId), fallback.Id);

        // The choice survives, so the persona is back when the surface is.
        Assert.Equal(CuratorId, _settings.GetPersonaForMode(WindowMode.Assistant));
        _surface.IsAvailable.Returns(true);
        Assert.Equal(CuratorId, (await sut.ResolveActiveAsync(WindowMode.Assistant, operatingMode)).Id);
    }

    [Fact]
    public async Task AHiddenCurator_StaysReserved_SoItCannotBeReCreatedOrEdited()
    {
        _surface.IsAvailable.Returns(false);
        var sut = CreateSut(_surface);
        await sut.AddPersonaAsync(new Persona { Id = CuratorId, Name = "Impostor", SystemPrompt = "prompt" });

        Assert.DoesNotContain(await sut.GetPersonasAsync(), p => p.Id == CuratorId);
        await Assert.ThrowsAnyAsync<Exception>(() => sut.UpdatePersonaAsync(
            new Persona { Id = CuratorId, Name = "Hijacked", SystemPrompt = "prompt" }));

        // Still resolvable by id, so a past message attributed to it keeps its persona.
        Assert.Equal("Pia · KB Curator", (await sut.GetPersonaAsync(CuratorId))!.Name);

        _surface.IsAvailable.Returns(true);
        Assert.Equal("Pia · KB Curator", Assert.Single(await sut.GetPersonasAsync(), p => p.Id == CuratorId).Name);
    }

    [Fact]
    public void ThePersonaService_AsksForTheSurfaceCache_AndTheGraphRegistersIt()
    {
        var parameters = typeof(PersonaService).GetConstructors().Single().GetParameters();
        Assert.Contains(parameters, p => p.ParameterType == typeof(IKnowledgeManagerSurfaceCache));

        var configure = typeof(Bootstrapper).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(configure);
        var services = new ServiceCollection();
        configure!.Invoke(null, [services]);

        Assert.Contains(services, d => d.ServiceType == typeof(IKnowledgeManagerSurfaceCache));
    }

    [Fact]
    public void EveryToolThePromptNames_ExistsInTheHandler_AndEveryToolIsNamed()
    {
        var prompt = string.Join('\n', Curator().SystemPrompt, Curator().Guardrails, Curator().OutputFormat);
        var surface = Substitute.For<IKnowledgeManagerSurfaceCache>();
        surface.IsAvailable.Returns(true);
        var handler = new KnowledgeManagerToolHandler(
            Substitute.For<IKnowledgeManagerApiClient>(), surface, Substitute.For<IFilesToolHandler>(),
            Substitute.For<ILocalizationService>(), NullLogger<KnowledgeManagerToolHandler>.Instance);
        var tools = handler.GetTools().Select(t => t.Name).ToList();
        Assert.NotEmpty(tools);

        var named = SnakeCaseWord().Matches(prompt).Select(m => m.Value).Distinct().ToList();

        Assert.Empty(named.Except(tools));
        Assert.Empty(tools.Except(named));
    }

    [GeneratedRegex(@"\b[a-z]+(?:_[a-z]+)+\b")]
    private static partial Regex SnakeCaseWord();

    [Fact]
    public void ThePrompt_CarriesTheAgreedBehaviour()
    {
        var prompt = Curator().SystemPrompt;

        Assert.Contains("numbered plan", prompt, StringComparison.Ordinal);
        Assert.Contains("Failed", prompt, StringComparison.Ordinal);
        Assert.Contains("8,000", prompt, StringComparison.Ordinal);
        Assert.Contains("language", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenTheToolsAreMissing_ThePromptNamesBothCauses_AndNeverSwitchesThePackOnItself()
    {
        var prompt = Curator().SystemPrompt;

        Assert.Contains("switched off under Settings → Plugins", prompt, StringComparison.Ordinal);
        Assert.Contains("no longer lists the user as a knowledge-base manager", prompt, StringComparison.Ordinal);
        Assert.Contains("administrator", prompt, StringComparison.Ordinal);
        Assert.Contains("Never try to switch it on yourself", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardrails_ForbidSecretsAndNameTheTwoPrivacyFacts()
    {
        var guardrails = Curator().Guardrails;

        Assert.NotNull(guardrails);
        Assert.Contains("credentials", guardrails, StringComparison.Ordinal);
        Assert.Contains("end-to-end encrypted", guardrails, StringComparison.Ordinal);
        Assert.Contains("other groups", guardrails, StringComparison.Ordinal);
        Assert.Contains(".txt", guardrails, StringComparison.Ordinal);
        Assert.Contains(".md", guardrails, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOutputFormat_IsThePiaFormatPlusTheReportingLines()
    {
        var format = Curator().OutputFormat!;

        Assert.StartsWith(AssistantPromptComposer.DefaultOutputFormat, format, StringComparison.Ordinal);
        var extra = format[AssistantPromptComposer.DefaultOutputFormat.Length..];
        Assert.Contains("changed", extra, StringComparison.Ordinal);
        Assert.Contains("pending", extra, StringComparison.Ordinal);
        Assert.Contains("needs you", extra, StringComparison.Ordinal);
        Assert.Contains("20", extra, StringComparison.Ordinal);
        Assert.Contains("more", extra, StringComparison.Ordinal);
    }
}
