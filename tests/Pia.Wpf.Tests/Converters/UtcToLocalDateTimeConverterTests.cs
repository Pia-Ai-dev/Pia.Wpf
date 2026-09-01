using System.Globalization;
using Pia.Converters;
using Xunit;

namespace Pia.Tests.Converters;

/// <summary>Expectations are derived from <see cref="TimeZoneInfo.Local"/> rather than a fixed offset,
/// so these hold on a UTC CI agent as well as on a CEST developer machine.</summary>
public class UtcToLocalDateTimeConverterTests
{
    private readonly UtcToLocalDateTimeConverter _sut = new();

    private object? Convert(object? value) =>
        _sut.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Utc_ShiftsByTheLocalOffset_AndComesBackAsLocal()
    {
        var utc = new DateTime(2026, 9, 1, 8, 12, 0, DateTimeKind.Utc);

        var result = Assert.IsType<DateTime>(Convert(utc));

        Assert.Equal(DateTimeKind.Local, result.Kind);
        Assert.Equal(utc + TimeZoneInfo.Local.GetUtcOffset(utc), result);
    }

    [Fact]
    public void Unspecified_IsTreatedAsUtc()
    {
        // The stores persist UTC; a Kind-less value (e.g. from a hand-edited archive) is the same instant.
        var bare = new DateTime(2026, 9, 1, 8, 12, 0, DateTimeKind.Unspecified);

        var result = Assert.IsType<DateTime>(Convert(bare));

        Assert.Equal(DateTime.SpecifyKind(bare, DateTimeKind.Utc).ToLocalTime(), result);
    }

    [Fact]
    public void Local_IsLeftAlone_SoAnAlreadyLocalisedSourceIsNotShiftedTwice()
    {
        var local = new DateTime(2026, 9, 1, 10, 12, 0, DateTimeKind.Local);

        Assert.Equal(local, Assert.IsType<DateTime>(Convert(local)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a date")]
    public void NonDateTime_PassesThrough(object? value)
    {
        Assert.Equal(value, Convert(value));
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(
            () => _sut.ConvertBack(DateTime.UtcNow, typeof(DateTime), null, CultureInfo.InvariantCulture));
    }
}
