using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>A consent is never REUSED: every send mints its own receipt for its own selection. The one caller
/// that may mint without a human is a granted background run, pinned by the two headless facts below.</summary>
public class AssignmentConsentNotRememberedTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    private static readonly Regex RememberedConsent = new(
        @"(remember|always|dont_?ask|don'?t.?ask|suppress|skip|cached|persist)\w*?(consent|affirm|unencrypt|assignment)"
        + @"|(consent|affirm|unencrypt|assignment)\w*?(remembered|always|dontask|suppressed|skipped|cached|persisted)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void TheMatcherFiresOnTheShapeItIsMeantToCatch()
    {
        Assert.Matches(RememberedConsent, "RememberAssignmentConsent");
        Assert.Matches(RememberedConsent, "AlwaysAllowUnencryptedSend");
        Assert.Matches(RememberedConsent, "SkipConsentPrompt");
        Assert.Matches(RememberedConsent, "AssignmentConsentCached");
        Assert.Matches(RememberedConsent, "DontAskAgainAssignment");
        Assert.DoesNotMatch(RememberedConsent, "AssistantDefaultWorkingDirectory");
    }

    [Fact]
    public void NoSettingPersistsAConsentDecision()
    {
        Type[] settingsTypes = [typeof(AppSettings), typeof(PolicySettings), typeof(PrivacySettings)];

        var members = settingsTypes
            .SelectMany(t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{t.Name}.{p.Name}")
                .Concat(t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => $"{t.Name}.{f.Name}")))
            .ToList();

        Assert.True(members.Count > 20, $"non-vacuity: only {members.Count} settings members were swept");

        var offenders = members.Where(m => RememberedConsent.IsMatch(m)).ToList();
        Assert.True(offenders.Count == 0,
            $"a setting must not remember a consent decision: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoSourceFileGrowsARememberedConsent()
    {
        // Deliberately not all of Services/*.cs: that is 100+ unrelated files whose prose would trip the
        // regex. The wildcard leads matter — HeadlessAssignmentLauncher does not start with "Assignment".
        var files = Directory
            .GetFiles(Path.Combine(SourceDirectory, "Services", "Operators"), "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(
                Path.Combine(SourceDirectory, "ViewModels"), "Assignment*.cs", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(
                Path.Combine(SourceDirectory, "Services"), "*Assignment*.cs", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(
                Path.Combine(SourceDirectory, "Views", "Dialogs"), "*Assignment*.cs", SearchOption.TopDirectoryOnly))
            .ToList();

        var names = files.Select(Path.GetFileName).ToList();

        // By name, not by a count: a mistyped glob that matches nothing still satisfies a count the other
        // arms already meet.
        Assert.Contains("AssignmentToolHandler.cs", names);
        Assert.Contains("HeadlessAssignmentLauncher.cs", names);
        Assert.Contains("AssignmentConsentPrompt.cs", names);
        Assert.Contains("JsonlAssignmentConsentStore.cs", names);
        Assert.Contains("AssignmentConsentViewModel.cs", names);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (Match match in RememberedConsent.Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
        }

        Assert.True(offenders.Count == 0,
            $"nothing may remember a consent decision: {string.Join(", ", offenders)}");
    }

    /// <summary>A <c>TryGetReceiptFor(selection)</c> is the shape a convenience change would add; a receipt can
    /// only be minted, never looked up. This is also why <c>RecordAsync</c> stays ONE method: an overload to
    /// extend the signature would make this array read <c>[RecordAsync, RecordAsync, WasRecorded]</c>.</summary>
    [Fact]
    public void TheConsentStoreOffersNoWayToReuseAReceipt()
    {
        var methods = typeof(IAssignmentConsentStore)
            .GetMethods()
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { nameof(IAssignmentConsentStore.RecordAsync), nameof(IAssignmentConsentStore.WasRecorded) },
            methods);
    }

    /// <summary>Two dialogs must never share an affirmation, so the view model cannot be cached by the container.</summary>
    [Fact]
    public void TheConsentViewModelIsRegisteredFresh()
    {
        var configure = typeof(Bootstrapper).GetMethod(
            "ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(configure);
        var services = new ServiceCollection();
        configure!.Invoke(null, [services]);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(AssignmentConsentViewModel));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    /// <summary>Required, and before the token: a caller cannot mint a receipt while leaving the granter out.</summary>
    [Fact]
    public void AHeadlessMintAlwaysNamesItsGranter()
    {
        var parameters = typeof(IAssignmentConsentStore)
            .GetMethod(nameof(IAssignmentConsentStore.RecordAsync))!
            .GetParameters();

        var grantedBy = Assert.Single(parameters, p => p.Name == "grantedBy");
        Assert.Equal(typeof(string), grantedBy.ParameterType);
        Assert.False(grantedBy.HasDefaultValue);
    }

    /// <summary>The behaviour that makes the rewritten summary true: an unattended send carries no records, so
    /// the model that proposed it chose nothing.</summary>
    [Fact]
    public async Task AHeadlessStartSendsNoRecords()
    {
        var consent = new RecordingConsentStore();
        var orchestrator = new RecordingOrchestrator();
        var launcher = new HeadlessAssignmentLauncher(
            new AvailableSurface(), consent, orchestrator,
            NullLogger<HeadlessAssignmentLauncher>.Instance);

        await launcher.StartAsync(
            new AssignmentSkill("deep-research", "Deep research", "Assistant", []),
            "summarise the week", "routine:" + Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.NotNull(consent.Items);
        Assert.Empty(consent.Items!);
        Assert.NotNull(orchestrator.Request);
        Assert.Empty(orchestrator.Request!.Items);
    }

    private sealed class RecordingConsentStore : IAssignmentConsentStore
    {
        public IReadOnlyList<AssignmentScopeItem>? Items { get; private set; }

        private readonly HashSet<Guid> _written = [];

        public Task<AssignmentConsentReceipt> RecordAsync(
            string skillName, string mode, IReadOnlyList<AssignmentScopeItem> items,
            string grantedBy, int promptChars, CancellationToken ct = default)
        {
            Items = items;
            var id = Guid.NewGuid();
            _written.Add(id);
            return Task.FromResult(new AssignmentConsentReceipt(id, skillName, items, DateTime.UtcNow));
        }

        public bool WasRecorded(Guid recordId) => _written.Contains(recordId);
    }

    private sealed class RecordingOrchestrator : IAssignmentRunOrchestrator
    {
        public AssignmentRequest? Request { get; private set; }

        public Task<AssignmentStartOutcome> StartAsync(
            AssignmentRequest request, AssignmentConsentReceipt receipt, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new AssignmentStartOutcome(AssignmentStartStatus.Started, Guid.NewGuid()));
        }

        public Task<int> DrainAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> CancelAsync(Guid assignmentId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class AvailableSurface : IAssignmentSurfaceCache
    {
        public AssignmentSurface Surface { get; } = new(
            true, [new AssignmentSkill("deep-research", "Deep research", "Assistant", [])]);

        public event EventHandler? Changed { add { } remove { } }

        public Task<AssignmentSurface> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Surface);

        public AssignmentSkill? FindSkill(string skillName) =>
            Surface.Skills.FirstOrDefault(s => s.Name == skillName);

        public Task<IReadOnlyList<AssignmentDto>?> GetRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssignmentDto>?>([]);
    }
}
