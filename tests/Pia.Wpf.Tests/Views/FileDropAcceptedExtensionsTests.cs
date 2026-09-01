using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>The two drop targets keep their accepted-extension list in XAML while the classifier keeps the
/// truth, so a kind added to one side reaches the user only if the other side is widened too.</summary>
public sealed class FileDropAcceptedExtensionsTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Pia.Wpf"));

    // Whole file names rather than extensions; neither list has ever offered one.
    private static readonly string[] DotfileNames = [".env", ".gitignore", ".editorconfig"];

    // Read off the importer rather than restated, so a kind added there is checked against both views
    // instead of quietly dropping out of this assertion's expected set.
    private static readonly FileKind[] Readable = [.. DroppedFileAttachmentImporter.ReadableKinds];

    /// <summary>Assistant drops route through <c>DroppedFileAttachmentImporter</c>, plus the image path the
    /// ViewModel keeps for itself.</summary>
    [Fact]
    public void TheAssistantAcceptsEveryExtensionItsDropPathCanRead() =>
        AssertListAgreesWithClassifier(
            Path.Combine("Views", "AssistantView.xaml"),
            [.. Readable, FileKind.Image]);

    /// <summary>Optimize drops route through <c>DroppedFileImporter</c>, which reads the same kinds and has
    /// no image branch.</summary>
    [Fact]
    public void OptimizeAcceptsEveryExtensionItsDropPathCanRead() =>
        AssertListAgreesWithClassifier(Path.Combine("Views", "OptimizeView.xaml"), Readable);

    private static void AssertListAgreesWithClassifier(string relativeView, params FileKind[] handled)
    {
        var declared = ReadAcceptedExtensions(relativeView);
        var known = ClassifierExtensions();

        var handledSet = new HashSet<FileKind>(handled);
        var expected = known
            .Where(pair => handledSet.Contains(pair.Value))
            .Select(pair => pair.Key)
            .Where(ext => !DotfileNames.Contains(ext, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var missing = expected.Where(ext => !declared.Contains(ext)).Order().ToList();
        Assert.True(missing.Count == 0,
            $"{relativeView} does not accept {string.Join(", ", missing)}, so its drop handler can read a " +
            "file the drag-over filter throws away first.");

        var dead = declared
            .Where(ext => !known.TryGetValue(ext, out var kind) || !handledSet.Contains(kind))
            .Order()
            .ToList();
        Assert.True(dead.Count == 0,
            $"{relativeView} accepts {string.Join(", ", dead)}, which its drop handler cannot read.");
    }

    private static IReadOnlyDictionary<string, FileKind> ClassifierExtensions()
    {
        var field = typeof(DroppedFileReader).GetField(
            "KindByExtension", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var map = (IReadOnlyDictionary<string, FileKind>)field!.GetValue(null)!;

        Assert.True(map.Count >= 40,
            $"non-vacuity: expected at least 40 classified extensions, found {map.Count} — the reflected " +
            "field is probably no longer the classifier table.");
        return map;
    }

    private static HashSet<string> ReadAcceptedExtensions(string relativeView)
    {
        var path = Path.Combine(SourceDirectory, relativeView);
        Assert.True(File.Exists(path), $"{path} does not exist");

        var match = Regex.Match(
            File.ReadAllText(path), @"AcceptedExtensions=""([^""]+)""");
        Assert.True(match.Success, $"{relativeView} declares no FileDropBehavior.AcceptedExtensions");

        return new HashSet<string>(
            match.Groups[1].Value.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }
}
