using System.Text.Json;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AppSettingsToolOutputTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Defaults_CapMcpResultsAt50k_AndLeaveTheToolLoopCacheOff()
    {
        var settings = new AppSettings();

        Assert.True(settings.McpToolResultCapEnabled);
        Assert.Equal(50_000, settings.GetMcpToolResultMaxChars());
        Assert.False(settings.ToolLoopPromptCacheEnabled);
    }

    [Theory]
    [InlineData(0, AppSettings.MinMcpToolResultMaxChars)]
    [InlineData(-5, AppSettings.MinMcpToolResultMaxChars)]
    [InlineData(10_000_000, AppSettings.MaxMcpToolResultMaxChars)]
    [InlineData(20_000, 20_000)]
    public void McpToolResultMaxChars_IsClampedOnRead(int stored, int expected)
    {
        Assert.Equal(expected, new AppSettings { McpToolResultMaxChars = stored }.GetMcpToolResultMaxChars());
    }

    [Fact]
    public void RoundTrip_PreservesTheToolOutputSettings()
    {
        var original = new AppSettings
        {
            McpToolResultCapEnabled = false,
            McpToolResultMaxChars = 20_000,
            ToolLoopPromptCacheEnabled = true,
        };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(reloaded);
        Assert.False(reloaded!.McpToolResultCapEnabled);
        Assert.Equal(20_000, reloaded.McpToolResultMaxChars);
        Assert.True(reloaded.ToolLoopPromptCacheEnabled);
    }
}
