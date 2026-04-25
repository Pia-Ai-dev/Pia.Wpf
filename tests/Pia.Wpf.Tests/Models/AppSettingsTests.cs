using System.Text.Json;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void LastCounterpartName_DefaultsToNull()
    {
        var s = new AppSettings();
        Assert.Null(s.LastCounterpartName);
    }

    [Fact]
    public void LastCounterpartName_RoundTripsThroughJson()
    {
        var s = new AppSettings { LastCounterpartName = "Alex" };
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(back);
        Assert.Equal("Alex", back!.LastCounterpartName);
    }
}
