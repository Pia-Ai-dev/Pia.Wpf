using System.Text.Json;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Guards the reason-then-emit planning opt-in on <see cref="AppSettings"/>: it is OFF by default (a
/// decision, not an accident — the split doubles the plan-turn cost) and it survives a JSON round-trip
/// through the same camelCase options the persistence layer uses (<c>JsonPersistenceService.JsonOptions</c>),
/// which is the only automated coverage that the settings CheckBox can actually persist.
/// </summary>
public class AppSettingsAgentPlanningTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void AgentPlanReasoningTurn_DefaultsOff()
    {
        Assert.False(new AppSettings().AgentPlanReasoningTurnEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_PreservesAgentPlanReasoningTurnEnabled(bool enabled)
    {
        var original = new AppSettings { AgentPlanReasoningTurnEnabled = enabled };

        var json = JsonSerializer.Serialize(original, Options);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(reloaded);
        Assert.Equal(enabled, reloaded!.AgentPlanReasoningTurnEnabled);
    }
}
