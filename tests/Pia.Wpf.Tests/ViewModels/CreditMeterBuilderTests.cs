namespace Pia.Tests.ViewModels;

using System.Globalization;
using NSubstitute;
using Pia.Services.Credits;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

public class CreditMeterBuilderTests
{
    private static readonly DateTime ResetsUtc = new(2026, 9, 27, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public CreditMeterBuilderTests()
    {
        _loc[Arg.Any<string>()].Returns(call => call.Arg<string>());
        _loc["Settings_Credits_UsedOf"].Returns("{0} of {1} used");
        _loc["Settings_Credits_LeftOf"].Returns("{0} of {1} left");
        _loc["Settings_Credits_Resets"].Returns("Resets {0}");
    }

    [Fact]
    public void Build_FullResponse_OrdersAndCaptionsEveryRow()
    {
        var status = new CreditStatusResponse(
            true, null,
            new CreditWindowDto(50, 3, null),
            new CreditWindowDto(200, 41, null),
            new CreditWindowDto(1000, 620, ResetsUtc),
            new CreditPoolDto(3000, 1200, 1800, ResetsUtc),
            new CreditTopUpDto(25000, 8300, 16700),
            new CreditGroupCapDto(new CreditWindowDto(5000, 900, null), new CreditWindowDto(20000, 7400, ResetsUtc)));

        var meters = CreditMeterBuilder.Build(status, _loc, Berlin, German);

        Assert.Equal(
            new[] { "weekly", "pool", "topUp", "daily", "hourly", "groupWeekly", "groupDaily" },
            meters.Select(m => m.Key));
        Assert.Equal(new CreditMeter("weekly", "Settings_Credits_Weekly", 620, 1000,
            "620 of 1.000 used · Resets 28.09.2026 00:00"), meters[0]);
        Assert.Equal(new CreditMeter("pool", "Settings_Credits_Pool", 1200, 3000,
            "1.800 of 3.000 left · Resets 28.09.2026 00:00"), meters[1]);
        Assert.Equal(new CreditMeter("topUp", "Settings_Credits_TopUp", 8300, 25000,
            "16.700 of 25.000 left"), meters[2]);
        Assert.Equal("3 of 50 used", meters[4].Caption);
    }

    [Fact]
    public void Build_OnlySomeSections_SkipsTheAbsentOnes()
    {
        var status = new CreditStatusResponse(
            true, null, null, new CreditWindowDto(20, 5, null), null, null, null, null);

        var meter = Assert.Single(CreditMeterBuilder.Build(status, _loc, Berlin, German));

        Assert.Equal("daily", meter.Key);
    }

    [Fact]
    public void Build_EmptyPool_KeepsAnEmptyBarRatherThanAFullOne()
    {
        var status = new CreditStatusResponse(
            true, null, null, null, new CreditWindowDto(10, 0, ResetsUtc),
            new CreditPoolDto(0, 0, 0, ResetsUtc), null, null);

        var pool = CreditMeterBuilder.Build(status, _loc, Berlin, German).Single(m => m.Key == "pool");

        Assert.Equal(0, pool.Value);
        Assert.Equal(1, pool.Maximum);
    }

    [Fact]
    public void Build_Overshoot_ClampsTheBarAndKeepsTheRawCaption()
    {
        var status = new CreditStatusResponse(
            true, null,
            null, null,
            new CreditWindowDto(1000, 1200, ResetsUtc),
            null, null, null);

        var weekly = CreditMeterBuilder.Build(status, _loc, Berlin, German).Single(m => m.Key == "weekly");

        Assert.Equal(1000, weekly.Value);
        Assert.Equal(1000, weekly.Maximum);
        Assert.Equal("1.200 of 1.000 used · Resets 28.09.2026 00:00", weekly.Caption);
    }

    [Fact]
    public void Build_Pool_FillsFromRemainingSoBarAndCaptionAgree()
    {
        var status = new CreditStatusResponse(
            true, null,
            null, null,
            new CreditWindowDto(1000, 0, ResetsUtc),
            new CreditPoolDto(3000, 1201, 1800, ResetsUtc),
            null, null);

        var pool = CreditMeterBuilder.Build(status, _loc, Berlin, German).Single(m => m.Key == "pool");

        Assert.Equal(1200, pool.Value);
        Assert.Equal("1.800 of 3.000 left · Resets 28.09.2026 00:00", pool.Caption);
    }
}
