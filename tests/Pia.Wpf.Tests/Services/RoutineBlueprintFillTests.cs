using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The rules that stop a mistyped or hallucinated slot name from producing a routine that quietly runs the
/// default every morning. Three of the plan's four rules ship: the enum-options rule has nothing to check
/// while <see cref="RoutineSlotKind.Text"/> is the only kind.
/// </summary>
public class RoutineBlueprintFillTests
{
    private static RoutineBlueprint Blueprint(string template, params RoutineSlot[] slots) =>
        new(
            Key: "test",
            TitleKey: "Routines_Blueprint_Test_Title",
            DescriptionKey: "Routines_Blueprint_Test_Description",
            Category: "daily",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: null,
            QueryKey: template,
            GuardKey: null,
            GrantedTools: [],
            Slots: slots);

    private static RoutineSlot Slot(string name, string? @default = null) =>
        new(name, RoutineSlotKind.Text, $"Label_{name}", $"Help_{name}", @default);

    // The resx lookup is the identity here, so a "key" is its own text and the fill rules read without a
    // resx round-trip.
    private static RoutineBlueprintText Text(RoutineBlueprint bp) => RoutineBlueprintText.Resolve(bp, key => key);

    [Fact]
    public void ASuppliedValueReplacesEveryOccurrence()
    {
        var bp = Blueprint("Watch {topic}. Report only {topic}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["topic"] = "shipping" });

        Assert.True(result.IsSuccess);
        Assert.Equal("Watch shipping. Report only shipping.", result.Query);
    }

    [Fact]
    public void NoValueTakesTheSlotsDefault()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic", "AI"));

        Assert.Equal("Watch AI.", RoutineBlueprintFill.ToCreateArgs(bp, Text(bp)).Query);
    }

    /// <summary>An empty string is not a topic. Treating it as one would blank a prompt the blueprint has a
    /// perfectly good default for.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankValueCountsAsUnsuppliedAndTakesTheDefault(string supplied)
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["topic"] = supplied });

        Assert.Equal("Watch AI.", result.Query);
    }

    [Fact]
    public void ASuppliedValueIsTrimmed()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["topic"] = "  shipping  " });

        Assert.Equal("Watch shipping.", result.Query);
    }

    /// <summary>Rule 1, the load-bearing one: a typo must not silently create a job that runs the default.</summary>
    [Fact]
    public void AnUnknownSlotNameIsRefusedAndNamed()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["tpoic"] = "shipping" });

        Assert.False(result.IsSuccess);
        Assert.Null(result.Query);
        Assert.Equal(RoutineFillErrorKind.UnknownSlot, result.Error!.Kind);
        Assert.Equal("tpoic", result.Error.SlotName);
        Assert.Contains("tpoic", result.Error.Message);
    }

    /// <summary>Rule 1 is about the NAME, so it fires even when the value is blank — otherwise the typo passes
    /// whenever the model sends an empty one.</summary>
    [Fact]
    public void AnUnknownSlotNameIsRefusedEvenWithABlankValue()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["tpoic"] = "" });

        Assert.Equal(RoutineFillErrorKind.UnknownSlot, result.Error!.Kind);
    }

    [Fact]
    public void SlotNamesAreCaseSensitive()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["Topic"] = "shipping" });

        Assert.Equal(RoutineFillErrorKind.UnknownSlot, result.Error!.Kind);
    }

    /// <summary>Rule 2: a slot with no default is required, and the error names it so a form can mark the field
    /// and the assistant knows what to ask for.</summary>
    [Fact]
    public void ARequiredSlotWithNoValueIsRefusedAndNamed()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Query);
        Assert.Equal(RoutineFillErrorKind.MissingRequiredSlot, result.Error!.Kind);
        Assert.Equal("topic", result.Error.SlotName);
        Assert.Contains("topic", result.Error.Message);
    }

    [Fact]
    public void ARequiredSlotIsSatisfiedByASuppliedValue()
    {
        var bp = Blueprint("Watch {topic}.", Slot("topic"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["topic"] = "shipping" });

        Assert.Equal("Watch shipping.", result.Query);
    }

    /// <summary>Rule 4: a reference the blueprint does not declare is an error, not a literal <c>{topic}</c>
    /// reaching the model.</summary>
    [Fact]
    public void AnUndeclaredPlaceholderIsRefused()
    {
        var bp = Blueprint("Watch {topic} for {region}.", Slot("topic", "AI"));

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Query);
        Assert.Equal(RoutineFillErrorKind.UnknownPlaceholder, result.Error!.Kind);
        Assert.Equal("region", result.Error.SlotName);
    }

    [Fact]
    public void ATemplateWithNoPlaceholdersRendersVerbatim()
    {
        var bp = Blueprint("Report what changed.");

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp));

        Assert.Equal("Report what changed.", result.Query);
    }

    [Fact]
    public void AValueForASlotOnABlueprintWithNoSlotsIsRefused()
    {
        var bp = Blueprint("Report what changed.");

        var result = RoutineBlueprintFill.ToCreateArgs(bp, Text(bp), new Dictionary<string, string> { ["topic"] = "shipping" });

        Assert.Equal(RoutineFillErrorKind.UnknownSlot, result.Error!.Kind);
    }

    [Theory]
    [InlineData("Watch {topic}.", true)]
    [InlineData("Watch it.", true)]
    [InlineData("Watch {topic.", false)]
    [InlineData("Watch topic}.", false)]
    [InlineData("Watch {{topic}}.", false)]
    public void BracesAreAllPlaceholders_SeesAnUnbalancedBrace(string template, bool expected)
    {
        Assert.Equal(expected, RoutineBlueprintFill.BracesAreAllPlaceholders(template));
    }
}
