using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// Whether a timestamp renders in local time depends on the store it came from, and nothing in a build
/// or a walkthrough catches getting it backwards. The two stores disagree on purpose:
/// <c>AssistantChatService.MapChat</c> calls <c>ToUniversalTime()</c> so its rows are
/// <c>DateTimeKind.Utc</c> and need the converter, while <c>HistoryService.MapSession</c> does not, so
/// <c>DateTime.Parse</c> leaves its rows already local and the converter would shift them twice.
/// </summary>
public class HistoryTimestampBindingTests
{
    private static readonly string ControlsRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf", "Controls"));

    private const string Converter = "UtcToLocalDateTimeConverter";

    public static TheoryData<string, string, bool> Bindings => new()
    {
        // Assistant chat history — UTC in, converter required.
        { "AssistantHistory/PiaAssistantChatRowContent.xaml", "UpdatedAt", true },
        { "AssistantHistory/PiaAssistantChatInspector.xaml", "CreatedAt", true },
        { "AssistantHistory/PiaAssistantChatInspector.xaml", "UpdatedAt", true },
        { "AssistantHistory/PiaAssistantChatInspector.xaml", "LastAccessedAt", true },
        // Optimization-session history — already local, converter must stay off.
        { "History/PiaHistorySessionRow.xaml", "CreatedAt", false },
        { "History/PiaHistoryInspectorHeader.xaml", "CreatedAt", false },
    };

    [Theory]
    [MemberData(nameof(Bindings))]
    public void TimestampBindings_ConvertOnlyWhereTheStoreHandsOutUtc(
        string relativePath, string property, bool converterExpected)
    {
        var file = Path.Combine(ControlsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file), $"{relativePath} moved; update this guard with it.");

        var bindings = Regex.Matches(File.ReadAllText(file), @"\{Binding " + property + @"[,}][^}]*\}")
            .Select(m => m.Value)
            .ToList();
        Assert.NotEmpty(bindings);

        foreach (var binding in bindings)
        {
            Assert.Equal(converterExpected, binding.Contains(Converter, StringComparison.Ordinal));
        }
    }
}

/// <summary>
/// The scan above proves the converter is REFERENCED; this proves the composition actually renders, which is
/// the part no unit test on the converter alone can show: a <c>Converter</c> and a <c>StringFormat</c> on one
/// binding have to run in that order, and the resource has to resolve from App.xaml.
/// </summary>
[Collection("WpfApplicationStatic")]
public class HistoryTimestampRenderTests
{
    private sealed class Row
    {
        public DateTime UpdatedAt { get; init; }
    }

    private static TextBlock? _block;

    [Fact]
    public void TheRowBinding_RendersTheLocalClock_NotTheStoredUtcOne()
    {
        var utc = new DateTime(2026, 9, 1, 8, 12, 0, DateTimeKind.Utc);

        WpfStaHost.Run(() =>
        {
            // Resolving by key is half the assertion: an unregistered converter throws here.
            var converter = (IValueConverter)Application.Current.Resources["UtcToLocalDateTimeConverter"];

            // The exact binding shape used by PiaAssistantChatRowContent's time column.
            _block = new TextBlock { DataContext = new Row { UpdatedAt = utc } };
            _block.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.UpdatedAt))
            {
                Converter = converter,
                StringFormat = "{0:HH:mm}",
                ConverterCulture = CultureInfo.GetCultureInfo("en-US"),
            });
            return true;
        });

        WpfStaHost.Pump();

        var rendered = WpfStaHost.Run(() => _block!.Text);

        var expected = utc.ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(expected, rendered);
        // Redundant unless the machine sits at UTC+0, where there is nothing to shift and the case is vacuous.
        if (TimeZoneInfo.Local.GetUtcOffset(utc) != TimeSpan.Zero)
            Assert.NotEqual("08:12", rendered);
    }
}
