using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Pia.Models;
using Pia.Services.Operators;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>Pins that no later "convenience" change quietly adds a remembered blanket consent.</summary>
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
        var files = Directory
            .GetFiles(Path.Combine(SourceDirectory, "Services", "Operators"), "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(
                Path.Combine(SourceDirectory, "ViewModels"), "Assignment*.cs", SearchOption.TopDirectoryOnly))
            .ToList();

        Assert.True(files.Count >= 3, $"non-vacuity: only {files.Count} assignment source files were scanned");

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
    /// only be minted, never looked up.</summary>
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
}
