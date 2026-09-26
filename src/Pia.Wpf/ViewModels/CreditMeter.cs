using System.Globalization;
using Pia.Services.Credits;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed record CreditMeter(string Key, string Label, long Value, long Maximum, string Caption);

public static class CreditMeterBuilder
{
    public static IReadOnlyList<CreditMeter> Build(
        CreditStatusResponse status, ILocalizationService loc, TimeZoneInfo zone, CultureInfo culture)
    {
        var meters = new List<CreditMeter>();

        string Number(long value) => value.ToString("N0", culture);

        string WithReset(string caption, DateTime? resetsAtUtc) => resetsAtUtc is { } utc
            ? $"{caption} · {string.Format(culture, loc["Settings_Credits_Resets"],
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("g", culture))}"
            : caption;

        void Add(string key, string labelKey, long rawValue, long limitOrTotal, string caption, DateTime? resetsAtUtc)
        {
            var maximum = Math.Max(1, limitOrTotal);
            var clampedValue = Math.Clamp(rawValue, 0, maximum);
            meters.Add(new CreditMeter(key, loc[labelKey], clampedValue, maximum, WithReset(caption, resetsAtUtc)));
        }

        void Used(string key, string labelKey, CreditWindowDto? window)
        {
            if (window is null) return;
            var rawValue = window.Used;
            var caption = string.Format(culture, loc["Settings_Credits_UsedOf"], Number(rawValue), Number(window.Limit));
            Add(key, labelKey, rawValue, window.Limit, caption, window.ResetsAt);
        }

        void Left(string key, string labelKey, long total, long remaining, DateTime? resetsAtUtc)
        {
            var rawValue = total - remaining;
            var caption = string.Format(culture, loc["Settings_Credits_LeftOf"], Number(remaining), Number(total));
            Add(key, labelKey, rawValue, total, caption, resetsAtUtc);
        }

        Used("weekly", "Settings_Credits_Weekly", status.Weekly);
        if (status.Pool is { } pool)
            Left("pool", "Settings_Credits_Pool", pool.Total, pool.Remaining, pool.ResetsAt);
        if (status.TopUp is { } topUp)
            Left("topUp", "Settings_Credits_TopUp", topUp.Granted, topUp.Remaining, null);
        Used("daily", "Settings_Credits_Daily", status.Daily);
        Used("hourly", "Settings_Credits_Hourly", status.Hourly);
        Used("groupWeekly", "Settings_Credits_GroupWeekly", status.GroupCap?.Weekly);
        Used("groupDaily", "Settings_Credits_GroupDaily", status.GroupCap?.Daily);

        return meters;
    }
}
