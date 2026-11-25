using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The drop paths and the file tools read through one reader table; these keep a format the
/// table learns from reaching only one of them, or reaching the tools without the model being told.</summary>
public sealed class FilesToolFormatCoverageTests
{
    public static TheoryData<FileKind> AllKinds => new(Enum.GetValues<FileKind>());

    public static TheoryData<string> ExtractedExtensions
    {
        get
        {
            var extensions = DroppedFileReader.KindByExtension
                .Where(pair => FilesToolHandler.IsExtractedKind(pair.Value))
                .Select(pair => pair.Key)
                .Order()
                .ToList();

            Assert.Contains(".pdf", extensions);
            return new(extensions);
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void EveryKindHasADecidedReader(FileKind kind)
        => _ = DroppedFileReader.ReaderFor(kind);

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void TheFileToolsExtractEveryNonTextKindADropCanRead(FileKind kind)
    {
        var dropReads = DroppedFileReader.ReaderFor(kind) is not null;

        Assert.Equal(dropReads && kind != FileKind.Text, FilesToolHandler.IsExtractedKind(kind));
    }

    [Theory]
    [MemberData(nameof(ExtractedExtensions))]
    public void ReadFileNamesEveryExtractedExtension(string extension)
        => Assert.Contains(extension, ToolDescription("read_file"), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(ExtractedExtensions))]
    public void SearchFilesNamesEveryExtractedExtension(string extension)
        => Assert.Contains(extension, ToolDescription("search_files"), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(ExtractedExtensions))]
    public void TheFilesSystemPromptNamesEveryExtractedExtension(string extension)
        => Assert.Contains(extension, FilesSystemPrompt(), StringComparison.OrdinalIgnoreCase);

    private static string ToolDescription(string name)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = Path.GetTempPath() });
        var handler = new FilesToolHandler(
            settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        return handler.GetTools().OfType<AIFunction>().Single(t => t.Name == name).Description;
    }

    private static string FilesSystemPrompt()
    {
        var config = BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.FilesPluginId].ConfigJson;
        using var doc = JsonDocument.Parse(config);
        return doc.RootElement.GetProperty("systemPromptAddition").GetString()!;
    }
}
