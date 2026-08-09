using System.Text.Json;
using Pia.Models;
using Xunit;
using SyncSettings = Pia.Shared.Models.SyncSettings;

namespace Pia.Tests.Models;

public class AppSettingsAgentRosterTests
{
    // The same camelCase options JsonPersistenceService uses.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void AgentPersonaRoster_DefaultsEmpty()
    {
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

        // Bypasses the setter, because a hand-edited settings file never went through SetAgentPersonaRoster.
        var settings = new AppSettings { AgentPersonaRoster = { [UserOperatingMode.Personal] = ids } };

        var roster = settings.GetAgentPersonaRoster(UserOperatingMode.Personal);

        Assert.Equal(AppSettings.MaxAgentPersonaRoster, roster.Count);
        Assert.Equal(roster.Count, roster.Distinct().Count());
        Assert.Equal(ids.Distinct().Take(AppSettings.MaxAgentPersonaRoster), roster);
    }

    [Fact]
    public void AgentPersonaRoster_IsAbsentFromSyncSettings()
    {
        var members = typeof(SyncSettings).GetProperties();

        Assert.True(members.Length > 10, $"SyncSettings has only {members.Length} properties — the assert below would be vacuous.");
        Assert.DoesNotContain(members, p => p.Name.Contains("Roster", StringComparison.OrdinalIgnoreCase));
    }
}
