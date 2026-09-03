using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Pia.Converters;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Architecture;

public class LocalizationTests
{
    private static readonly ResourceManager[] ResourceManagers =
    [
        CommonStrings.ResourceManager,
        ViewStrings.ResourceManager,
        MessageStrings.ResourceManager,
        OptimizingStrings.ResourceManager,
    ];

    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    private static HashSet<string> GetAllResourceKeys()
    {
        var keys = new HashSet<string>();
        foreach (var rm in ResourceManagers)
        {
            var resourceSet = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false);
            if (resourceSet == null) continue;

            foreach (DictionaryEntry entry in resourceSet)
                keys.Add((string)entry.Key);
        }
        return keys;
    }

    private static HashSet<string> GetResourceKeysForCulture(ResourceManager rm, CultureInfo culture)
    {
        var keys = new HashSet<string>();
        var resourceSet = rm.GetResourceSet(culture, true, false);
        if (resourceSet == null) return keys;

        foreach (DictionaryEntry entry in resourceSet)
            keys.Add((string)entry.Key);
        return keys;
    }

    [Fact]
    public void AllXamlLocalizationKeys_MustExistInResources()
    {
        var allKeys = GetAllResourceKeys();
        var xamlKeyPattern = new Regex(@"loc:Str\s+(\w+)", RegexOptions.Compiled);

        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(SourceDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            var matches = xamlKeyPattern.Matches(content);

            foreach (Match match in matches)
            {
                var key = match.Groups[1].Value;
                if (!allKeys.Contains(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.True(missing.Count == 0,
            $"all XAML localization keys must exist in resource files, but these are missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllCodeLocalizationKeys_MustExistInResources()
    {
        var allKeys = GetAllResourceKeys();

        // Patterns for static (non-interpolated) key lookups. The `[` must follow `_localization` immediately
        // so these do not also match `_localizationService[` and double-report every hit above.
        var patterns = new[]
        {
            new Regex(@"_localizationService\[""(\w+)""\]", RegexOptions.Compiled),
            new Regex(@"_localizationService\.Format\(""(\w+)""", RegexOptions.Compiled),
            new Regex(@"_localization\[""(\w+)""\]", RegexOptions.Compiled),
            new Regex(@"_localization\.Format\(""(\w+)""", RegexOptions.Compiled),
            new Regex(@"LocalizationSource\.Instance\[""(\w+)""\]", RegexOptions.Compiled),
            // The file importers take the service as a PARAMETER, so their call sites carry no underscore
            // and the five patterns above cannot see them; \b stops these re-reporting the field form.
            new Regex(@"\blocalizationService\[""(\w+)""\]", RegexOptions.Compiled),
            new Regex(@"\blocalizationService\.Format\(""(\w+)""", RegexOptions.Compiled),
        };

        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(SourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            // Skip Designer.cs files — they define keys, not consume them
            if (file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = File.ReadAllText(file);

            foreach (var pattern in patterns)
            {
                foreach (Match match in pattern.Matches(content))
                {
                    var key = match.Groups[1].Value;
                    if (!allKeys.Contains(key))
                        missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"all C# localization keys must exist in resource files, but these are missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AllTranslations_MustBeComplete()
    {
        var cultures = new[] { new CultureInfo("de"), new CultureInfo("fr") };

        // Only check ResourceManagers that have base keys
        var managersWithKeys = new (ResourceManager Rm, string Name)[]
        {
            (CommonStrings.ResourceManager, "CommonStrings"),
            (ViewStrings.ResourceManager, "ViewStrings"),
            (MessageStrings.ResourceManager, "MessageStrings"),
        };

        var missingTranslations = new List<string>();
        var orphanedTranslations = new List<string>();

        foreach (var (rm, name) in managersWithKeys)
        {
            var baseKeys = GetResourceKeysForCulture(rm, CultureInfo.InvariantCulture);
            if (baseKeys.Count == 0) continue;

            foreach (var culture in cultures)
            {
                var translatedKeys = GetResourceKeysForCulture(rm, culture);

                var missing = baseKeys.Except(translatedKeys).ToList();
                var orphaned = translatedKeys.Except(baseKeys).ToList();

                foreach (var key in missing)
                    missingTranslations.Add($"{name}.{culture.Name}: {key}");

                foreach (var key in orphaned)
                    orphanedTranslations.Add($"{name}.{culture.Name}: {key}");
            }
        }

        Assert.True(missingTranslations.Count == 0,
            $"all base keys must have translations, but these are missing: {string.Join(", ", missingTranslations)}");

        Assert.True(orphanedTranslations.Count == 0,
            $"translation files should not contain keys absent from the base file: {string.Join(", ", orphanedTranslations)}");
    }

    /// <summary>The generated accessors drift the other way too: a property whose key was deleted from the
    /// .resx still compiles and just returns null, so the loss only ever shows up as a blank label.</summary>
    [Fact]
    public void EveryGeneratedStringAccessor_StillResolvesToAResource()
    {
        var accessors = new[]
            {
                typeof(CommonStrings), typeof(ViewStrings),
                typeof(MessageStrings), typeof(OptimizingStrings)
            }
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => (Name: $"{t.Name}.{p.Name}", Value: (string?)p.GetValue(null))))
            .ToArray();

        // Non-vacuity: reflection returning nothing would make the assertion below pass on an empty set.
        Assert.True(accessors.Length >= 300,
            $"only {accessors.Length} generated string accessors were found, which is below the floor.");

        var orphans = accessors.Where(a => a.Value is null).Select(a => a.Name).ToArray();
        Assert.True(orphans.Length == 0,
            "these generated accessors in Resources/Strings/*.Designer.cs name a resource key that no longer " +
            $"exists in the .resx, so they return null at runtime: {string.Join(", ", orphans)}");
    }

    /// <summary>The mapping lives in a helper, so this file's literal-key regexes cannot see the keys it returns.</summary>
    [Fact]
    public void EveryRunStateLabelKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<RunProgressState>()
            .Select(RunStateToLabelConverter.LabelKey)
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        Assert.True(keys.Count >= 7, $"non-vacuity: expected at least 7 distinct run-state keys, found {keys.Count}");

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every run-state label key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary><c>MutationErrorKey</c> is a helper, so this file's literal-key regexes cannot see the keys it returns.</summary>
    [Fact]
    public void EveryPlanMutationErrorKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<PlanMutationOutcome>()
            .Where(o => o != PlanMutationOutcome.Applied) // the success arm shows no note at all
            .Select(RunProgressViewModel.MutationErrorKey)
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        Assert.True(keys.Count >= 6, $"non-vacuity: expected at least 6 distinct mutation-error keys, found {keys.Count}");

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every plan-mutation error key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>The pills format their key from a table, so the literal never follows <c>_localization.Format(</c>
    /// and no regex here can see it. The decision LABEL keys are literals inside a switch, so they are invisible to
    /// the regexes too — both halves are resolved here.</summary>
    [Fact]
    public void EveryDecisionPillKeyResolvesInAllThreeLocales()
    {
        var pillKeys = RunProgressViewModel.DecisionCategories.Select(c => c.PillKey).ToArray();
        var labelKeys = RunProgressViewModel.DecisionCategories.Select(c => c.LabelKey).ToArray();

        Assert.Equal(7, pillKeys.Length); // non-vacuity

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in pillKeys.Concat(labelKeys).Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every decision pill and label key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");

        // Coverage, not a count: a category is derived from a row rather than from an ordinal, so the two sets
        // are not the same size.
        foreach (var mapped in Enum.GetValues<ToolGateDecision>().Select(RunProgressViewModel.DecisionLabelKey).Distinct())
            Assert.Contains(mapped, labelKeys);
        Assert.Contains("Run_Timeline_Decision_NotExecuted", labelKeys);
    }

    /// <summary>The keys sit in a static table, so this file's literal-key regexes cannot see them and a
    /// typo would only surface as a blank chip at runtime.</summary>
    [Fact]
    public void EveryStarterSuggestionKeyResolvesInAllThreeLocales()
    {
        var keys = StarterSuggestionService.AllKeys;

        Assert.True(keys.Count >= 20,
            $"non-vacuity: expected at least 20 distinct starter-suggestion keys, found {keys.Count}");

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every starter-suggestion key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>The mapping lives in a helper, so this file's literal-key regexes cannot see the keys it returns.</summary>
    [Fact]
    public void EveryAutoApprovedStatusKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<ToolGateDecision>()
            .Select(d => ActionCardBuilder.AutoApprovedStatusKey(d))
            .Distinct()
            .ToList();

        // The two tiers that used to fall through to "you always allow"; without them the sweep below is a
        // check on three keys that already shipped.
        Assert.Contains("ActionCard_AutoApprovedByAutonomy", keys);
        Assert.Contains("ActionCard_AutoApprovedByRunGrant", keys);

        var missing = new List<string>();
        var cultures = new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") };
        foreach (var culture in cultures)
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every auto-approved status key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");

        // The card formats these with exactly one argument, so a translation carrying a {1} throws at render
        // time rather than rendering wrong.
        var placeholder = new Regex(@"\{(\d+)");
        foreach (var culture in cultures)
            foreach (var key in keys)
                foreach (Match match in placeholder.Matches(ViewStrings.ResourceManager.GetString(key, culture)!))
                    Assert.Equal("0", match.Groups[1].Value);
    }

    /// <summary>The mapping lives in a helper, so this file's literal-key regexes cannot see the keys it returns.</summary>
    [Fact]
    public void EveryToolGrantCautionKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<ToolGrantCaution>()
            .Select(ToolCatalogRow.CautionKeyFor)
            .Where(k => k is not null)
            .Distinct()
            .ToList();

        // One note per caution, less the None arm — a tool with nothing to caution about shows no line at all.
        Assert.Equal(Enum.GetValues<ToolGrantCaution>().Length - 1, keys.Count);

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k!)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every tool-catalogue reason key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>The routine picker's own copy: unattended there is nobody to ask, so it cannot reuse the Tool
    /// access wording. Same helper-not-literal problem as the pair above.</summary>
    [Fact]
    public void EveryRoutineToolCautionKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<ToolGrantCaution>()
            .Select(RoutineToolRow.RoutineCautionKeyFor)
            .Where(k => k is not null)
            .Distinct()
            .ToList();

        Assert.Equal(Enum.GetValues<ToolGrantCaution>().Length - 1, keys.Count);

        // Distinct from the Tool access copy, or the routine surface would inherit "you will be asked each
        // time" — false for a run with no human in front of it.
        Assert.Empty(keys.Intersect(Enum.GetValues<ToolGrantCaution>().Select(ToolCatalogRow.CautionKeyFor)));

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k!)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every routine caution key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>The status key is formatted from the enum, so this file's literal-key regexes cannot see it.</summary>
    [Fact]
    public void EveryAssignmentStatusKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<AssignmentRowStatus>()
            .Select(AssignmentRowViewModel.StatusLabelKey)
            .Distinct()
            .ToList();

        // One label per status, so a new arm cannot render as a raw key.
        Assert.Equal(Enum.GetValues<AssignmentRowStatus>().Length, keys.Count);

        // Pinned literally as well, so deleting a status arm shrinks coverage loudly instead of silently.
        string[] expected =
        [
            "Assignments_Status_Queued",
            "Assignments_Status_Running",
            "Assignments_Status_Completed",
            "Assignments_Status_Failed",
            "Assignments_Status_Cancelled",
            "Assignments_Status_Unknown",
        ];
        Assert.Equal(expected.Order(), keys.Order());

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every assignment-status key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>The type label comes from a helper, so this file's literal-key regexes cannot see it.</summary>
    [Fact]
    public void EveryAssignmentEntityTypeKeyResolvesInAllThreeLocales()
    {
        var entityTypes = typeof(AssignmentInputEntityTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        // An entity type added to the contract without a label here would otherwise render as "Record".
        Assert.NotEmpty(entityTypes);

        var keys = entityTypes
            .Append("something-this-version-does-not-know")
            .Select(AssignmentScopeItemViewModel.EntityTypeKey)
            .Distinct()
            .ToList();

        Assert.Equal(entityTypes.Count + 1, keys.Count);
        Assert.Contains("AssignmentConsent_EntityType_Unknown", keys);

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every assignment entity-type key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>The outcome message comes from a helper, so this file's literal-key regexes cannot see it.</summary>
    [Fact]
    public void EveryAssignmentStartResultKeyResolvesInAllThreeLocales()
    {
        var keys = Enum.GetValues<AssignmentStartStatus>()
            .Select(AssignmentConsentViewModel.StartResultKey)
            .Distinct()
            .ToList();

        // One message per outcome, so a new arm cannot silently reuse the generic failure line.
        Assert.Equal(Enum.GetValues<AssignmentStartStatus>().Length, keys.Count);
        Assert.Contains("AssignmentConsent_Result_Started", keys);

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every assignment start-result key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EnumConverterKeys_MustExistInResources()
    {
        var allKeys = GetAllResourceKeys();

        // Keys used by EnumToLocalizedStringConverter (explicit switch mapping)
        var enumConverterKeys = new[]
        {
            "Enum_CopyToClipboard",
            "Enum_AutoType",
            "Enum_PasteToPreviousWindow",
            "Enum_WhisperTiny",
            "Enum_WhisperBase",
            "Enum_WhisperSmall",
            "Enum_WhisperMedium",
            "Enum_WhisperLarge",
            "Enum_SpeechAuto",
            "Enum_SpeechEN",
            "Enum_SpeechDE",
            "Enum_SpeechFR",
            "Enum_LangEN",
            "Enum_LangDE",
            "Enum_LangFR",
        };

        // Keys used by CategoryDisplayConverter (Settings_Privacy_Category_{category})
        var piiCategories = new[] { "Person", "Nickname", "Email", "Phone", "Address", "Date", "Custom" };
        var categoryKeys = piiCategories.Select(c => $"Settings_Privacy_Category_{c}");

        var allDynamicKeys = enumConverterKeys.Concat(categoryKeys).ToList();

        var missing = allDynamicKeys
            .Where(key => !allKeys.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            $"all dynamically constructed localization keys must exist in resources, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// A de or fr value that drops a placeholder renders wrong with a green gate: the only existing
    /// placeholder check is scoped to the ActionCard key set, so these keys need their own.
    /// </summary>
    [Theory]
    [InlineData("Settings_ExportDiagnostics_Confirm_Message", 2)]
    [InlineData("Settings_ExportDiagnostics_Confirm_ExcludedByCap", 3)]
    [InlineData("Settings_ExportDiagnostics_Confirm_Excluded", 1)]
    public void ADiagnosticsKeyCarriesTheSamePlaceholdersInEveryLocale(string key, int expected)
    {
        var placeholder = new Regex(@"\{(\d+)");
        var cultures = new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") };

        foreach (var culture in cultures)
        {
            var value = ViewStrings.ResourceManager.GetString(key, culture);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is missing for {culture.Name}");
            var indexes = placeholder.Matches(value!).Select(m => m.Groups[1].Value).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, expected).Select(i => i.ToString(CultureInfo.InvariantCulture)),
                indexes);
        }
    }

    /// <summary>Both sentences can appear in one dialog body, so a shared opening clause reads as a stutter.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("fr")]
    public void TheTwoExclusionSentencesDoNotShareAnOpeningClause(string culture)
    {
        var info = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
        var byCap = ViewStrings.ResourceManager.GetString("Settings_ExportDiagnostics_Confirm_ExcludedByCap", info)!;
        var other = ViewStrings.ResourceManager.GetString("Settings_ExportDiagnostics_Confirm_Excluded", info)!;

        var shared = 0;
        while (shared < byCap.Length && shared < other.Length && byCap[shared] == other[shared])
            shared++;

        Assert.True(shared < 15,
            $"in {info.Name} these two sentences share their first {shared} characters, and both are appended " +
            $"to the same consent body, so the user reads the same clause twice: \"{byCap[..shared]}\"");
    }

    /// <summary>The snackbar bodies take one argument each, so a stray {1} throws at render time.</summary>
    [Theory]
    [InlineData("Msg_Settings_DiagnosticsExported_Body", 1)]
    public void ADiagnosticsMessageKeyCarriesTheSamePlaceholdersInEveryLocale(string key, int expected)
    {
        var placeholder = new Regex(@"\{(\d+)");
        var cultures = new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") };

        foreach (var culture in cultures)
        {
            var value = MessageStrings.ResourceManager.GetString(key, culture);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is missing for {culture.Name}");
            var indexes = placeholder.Matches(value!).Select(m => m.Groups[1].Value).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, expected).Select(i => i.ToString(CultureInfo.InvariantCulture)),
                indexes);
        }
    }

    /// <summary>The file-drop snackbars are formatted with a fixed argument count each, so a locale that
    /// drops or invents a placeholder throws at render time instead of reading wrong.</summary>
    [Theory]
    [InlineData("Msg_File_AttachLimit", 2)]
    [InlineData("Msg_File_ReadFailed", 2)]
    [InlineData("Msg_File_AttachBudget", 1)]
    [InlineData("Msg_File_TooLargeAttachment", 1)]
    [InlineData("Msg_File_UnsupportedAttachment", 1)]
    [InlineData("Msg_File_DuplicateAttachment", 1)]
    [InlineData("Msg_File_Empty", 1)]
    [InlineData("Msg_File_Truncated", 1)]
    [InlineData("Msg_File_OneImageOnly", 1)]
    [InlineData("Msg_File_DropFailed", 1)]
    [InlineData("Msg_File_DropNoFile", 0)]
    public void AFileDropMessageKeyCarriesTheSamePlaceholdersInEveryLocale(string key, int expected)
    {
        var placeholder = new Regex(@"\{(\d+)");
        var cultures = new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") };

        foreach (var culture in cultures)
        {
            var value = MessageStrings.ResourceManager.GetString(key, culture);
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is missing for {culture.Name}");
            var indexes = placeholder.Matches(value!).Select(m => m.Groups[1].Value).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, expected).Select(i => i.ToString(CultureInfo.InvariantCulture)),
                indexes);
        }
    }
}
