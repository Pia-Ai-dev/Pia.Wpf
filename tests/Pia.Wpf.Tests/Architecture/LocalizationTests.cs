using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using Pia.Converters;
using Pia.Resources.Strings;
using Pia.ViewModels;
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

        // Patterns for static (non-interpolated) key lookups
        var patterns = new[]
        {
            new Regex(@"_localizationService\[""(\w+)""\]", RegexOptions.Compiled),
            new Regex(@"_localizationService\.Format\(""(\w+)""", RegexOptions.Compiled),
            new Regex(@"LocalizationSource\.Instance\[""(\w+)""\]", RegexOptions.Compiled),
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

    /// <summary>
    /// T-CONV-3 (Batch 07 G8), <b>GUARD</b>. Every key <c>RunStateToLabelConverter.LabelKey</c> can return
    /// must resolve in en, de AND fr. The other two scans in this file cannot see these: they match
    /// <c>LocalizationSource.Instance["Literal"]</c> at the call site, and this mapping was extracted to a
    /// helper so a theory could pin it — which moved the literals out of reach of the regex.
    /// <para>
    /// The run-state chip is the one string on the run panel a user always sees, and an unresolved key renders
    /// as the key text itself. Non-vacuity: the key set must be non-empty and cover ≥ 7 keys.
    /// </para>
    /// </summary>
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
}
