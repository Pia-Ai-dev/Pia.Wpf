using System.Text.Json;
using Pia.Models;
using Xunit;
using SyncSettings = Pia.Shared.Models.SyncSettings;

namespace Pia.Tests.Models;

/// <summary>
/// Guards the Batch 07 "step specialists" roster on <see cref="AppSettings"/>. The roster IS the opt-in for
/// per-step personas, so its default emptiness is a decision rather than an accident; and since no test parses
/// the settings view, the JSON round-trip here is the only automated proof that the surface G7 builds can
/// actually persist.
/// </summary>
public class AppSettingsAgentRosterTests
{
    // The same camelCase options JsonPersistenceService uses, mirroring AppSettingsAgentPlanningTests.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void AgentPersonaRoster_DefaultsEmpty()
    {
        // D1: with no roster the planner is told about no personas and no step is ever assigned.
        Assert.Empty(new AppSettings().AgentPersonaRoster);
        Assert.Empty(new AppSettings().GetAgentPersonaRoster(UserOperatingMode.Personal));
        Assert.Empty(new AppSettings().GetAgentPersonaRoster(UserOperatingMode.Business));
    }

    [Fact]
    public void RosterRoundTripsThroughCamelCaseJson_KeyedByOperatingMode()
    {
        var personal = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var business = new[] { Guid.NewGuid() };
        var original = new AppSettings();
        original.SetAgentPersonaRoster(UserOperatingMode.Personal, personal);
        original.SetAgentPersonaRoster(UserOperatingMode.Business, business);

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(reloaded);
        // Independently keyed: a work roster and a home roster do not bleed into each other (D7).
        Assert.Equal(personal, reloaded!.GetAgentPersonaRoster(UserOperatingMode.Personal));
        Assert.Equal(business, reloaded.GetAgentPersonaRoster(UserOperatingMode.Business));
    }

    [Fact]
    public void SetEmptyRoster_RemovesTheKey()
    {
        var settings = new AppSettings();
        settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [Guid.NewGuid()]);
        Assert.True(settings.AgentPersonaRoster.ContainsKey(UserOperatingMode.Personal));

        settings.SetAgentPersonaRoster(UserOperatingMode.Personal, []);

        // No residue — the same thing SetPersonaForMode(null) does, so clearing the roster is indistinguishable
        // from never having configured one.
        Assert.False(settings.AgentPersonaRoster.ContainsKey(UserOperatingMode.Personal));
        Assert.Empty(settings.GetAgentPersonaRoster(UserOperatingMode.Personal));
    }

    [Fact]
    public void GetRoster_ClampsAndDedupes_PreservingOrder()
    {
        var dup = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var i = 0; i < 9; i++)
            ids.Add(Guid.NewGuid());
        ids.Insert(2, dup);
        ids.Insert(5, dup);

        // Written STRAIGHT into the dictionary, bypassing the setter: read-side clamping is the property under
        // test, because a hand-edited settings file never went through SetAgentPersonaRoster.
        var settings = new AppSettings { AgentPersonaRoster = { [UserOperatingMode.Personal] = ids } };

        var roster = settings.GetAgentPersonaRoster(UserOperatingMode.Personal);

        Assert.Equal(AppSettings.MaxAgentPersonaRoster, roster.Count);
        Assert.Equal(roster.Count, roster.Distinct().Count());
        Assert.Equal(ids.Distinct().Take(AppSettings.MaxAgentPersonaRoster), roster);
    }

    [Fact]
    public void AgentPersonaRoster_IsAbsentFromSyncSettings()
    {
        // R26: every Agent* knob is local-only. A synced roster would name personas the other device may not
        // have, and the roster is per-device configuration rather than shared state.
        var members = typeof(SyncSettings).GetProperties();

        // Non-vacuity: a renamed or emptied SyncSettings must not be able to satisfy this by having no members.
        Assert.True(members.Length > 10, $"SyncSettings has only {members.Length} properties — the assert below would be vacuous.");
        Assert.DoesNotContain(members, p => p.Name.Contains("Roster", StringComparison.OrdinalIgnoreCase));
    }
}
