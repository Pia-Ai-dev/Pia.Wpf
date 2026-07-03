using System.Text.Json;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Sync;

public class SyncMemoryPathTests
{
    [Fact]
    public void Path_RoundTripsThroughJson()
    {
        var original = new SyncMemory
        {
            Id = Guid.NewGuid(),
            Type = "note",
            Label = "test",
            Path = "memories/2026/example.md",
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<SyncMemory>(json);

        Assert.NotNull(restored);
        Assert.Equal(original.Path, restored.Path);
    }

    [Fact]
    public void Path_OmittedFromJson_WhenNull()
    {
        var memory = new SyncMemory
        {
            Id = Guid.NewGuid(),
            Type = "note",
            Path = null,
        };

        var json = JsonSerializer.Serialize(memory);

        Assert.DoesNotContain("\"Path\"", json, StringComparison.Ordinal);
    }
}
