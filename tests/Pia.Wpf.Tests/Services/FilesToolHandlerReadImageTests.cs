using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>read_file on an image parks the pixels on the round's channel instead of refusing; every arm
/// that refuses has to do so before the decode, which is what the byte-ceiling and no-channel cases assert.</summary>
public class FilesToolHandlerReadImageTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerReadImageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-readimg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        ToolLoopImageChannel.Current = null;
        TempPath.Remove(_root);
    }

    private ToolLoopImageChannel UseChannel(AiProviderType type = AiProviderType.PiaCloud)
    {
        var channel = new ToolLoopImageChannel(type);
        ToolLoopImageChannel.Current = channel;
        return channel;
    }

    private async Task<string> ReadAsync(string path, string callId = "c1")
    {
        var call = new FunctionCallContent(
            callId, "read_file", new Dictionary<string, object?> { ["path"] = path });
        var (result, _) = await _handler.HandleToolCallAsync(call);
        return (string)result!;
    }

    private string WritePng(string name, int width = 8, int height = 6)
    {
        var full = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var stride = width * 3;
        var pixel = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgr24, null, new byte[stride * height], stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixel));
        using var stream = File.Create(full);
        encoder.Save(stream);
        return name;
    }

    [Fact]
    public async Task ReadFile_OnAPng_ParksTheImageAndSaysSo()
    {
        var channel = UseChannel();
        var name = WritePng("shot.png");

        var result = await ReadAsync(name, callId: "call-7");

        var parked = Assert.Single(channel.Drain());
        Assert.Equal("call-7", parked.CallId);
        Assert.Equal(ToolLoopImageSource.ImageFile, parked.Source);
        Assert.Equal("image/jpeg", parked.MediaType);
        Assert.Contains("shot.png", parked.Caption, StringComparison.Ordinal);
        Assert.Contains("shot.png", result, StringComparison.Ordinal);
        Assert.Contains("withdrawn", result, StringComparison.Ordinal);
        Assert.DoesNotContain("attach the image instead", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_OnAPng_WithNoChannel_RefusesWithoutDecoding()
    {
        // A garbage file with an image extension: a decode would fail with a different message than the
        // no-channel refusal, so the exact text is the proof nothing was opened.
        File.WriteAllBytes(Path.Combine(_root, "junk.png"), [0xFF, 0xFE, 0xFD]);

        var result = await ReadAsync("junk.png");

        Assert.Contains("cannot receive a picture", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_OnAPng_OnANonCloudProvider_Refuses()
    {
        UseChannel(AiProviderType.OpenAI);
        File.WriteAllBytes(Path.Combine(_root, "junk.png"), [0xFF, 0xFE, 0xFD]);

        var result = await ReadAsync("junk.png");

        Assert.Contains("cannot read pictures", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_OnAHugeImage_RefusesOnTheByteCeiling_WithoutDecoding()
    {
        UseChannel();
        // 26 MB of zeroes: over the ceiling and undecodable, so reaching the decoder would say something else.
        File.WriteAllBytes(Path.Combine(_root, "huge.png"), new byte[26 * 1024 * 1024]);

        var result = await ReadAsync("huge.png");

        Assert.Contains("MB ceiling", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_FifthImageInARound_IsRefused()
    {
        var channel = UseChannel();
        for (var i = 0; i < 4; i++)
            Assert.Contains("Showed you", await ReadAsync(WritePng($"i{i}.png")), StringComparison.Ordinal);

        var result = await ReadAsync(WritePng("fifth.png"));

        Assert.Contains("Already showing 4 pictures", result, StringComparison.Ordinal);
        Assert.Equal(4, channel.Count);
    }

    [Fact]
    public async Task ReadFile_OnAnImage_RaisesTheFileTouchChip_AndRecordsNoStaleness()
    {
        UseChannel();
        var staleness = Substitute.For<IFileStalenessStore>();
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });
        var handler = new FilesToolHandler(settings, staleness, NullLogger<FilesToolHandler>.Instance);

        var name = WritePng("chip.png");
        var touched = new List<FileTouch>();
        var call = new FunctionCallContent(
            "c1", "read_file", new Dictionary<string, object?> { ["path"] = name });

        TaskAmbient.Current = new TaskContext(
            Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: touched.Add);
        try
        {
            await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);
        }
        finally
        {
            TaskAmbient.Current = null;
        }

        Assert.Equal(FileTouchKind.Read, Assert.Single(touched).Kind);
        // Nothing can edit an image, so a staleness record would be state no gate ever reads.
        staleness.DidNotReceiveWithAnyArgs().RecordRead(default, default!, default);
    }

    [Fact]
    public async Task ReadFile_OutsideTheSandbox_StillRefusesBeforeAnyImageHandling()
    {
        var channel = UseChannel();

        var result = await ReadAsync(@"..\..\elsewhere.png");

        Assert.Contains("outside the assistant files folder", result, StringComparison.Ordinal);
        Assert.Equal(0, channel.Count);
    }

    [Fact]
    public async Task ReadFile_OnASensitivePath_StillRefuses()
    {
        var channel = UseChannel();

        var result = await ReadAsync(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system.png"));

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.Equal(0, channel.Count);
    }

    [Fact]
    public async Task PromptPreview_OnAnImage_StillRefuses()
    {
        UseChannel();
        var name = WritePng("preview.png");

        var preview = await _handler.ReadPromptPreviewAsync(
            name, workingSubpath: null, maxLines: 50, TestContext.Current.CancellationToken);

        Assert.Contains("attach the image instead", preview.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    public async Task WritingAnImage_IsRefused(string tool)
    {
        var name = WritePng("target.png");
        var args = new Dictionary<string, object?> { ["path"] = name };
        if (tool == "write_file") args["content"] = "plain text";
        else { args["old_string"] = "a"; args["new_string"] = "b"; }

        var (result, pending) = await _handler.HandleToolCallAsync(
            new FunctionCallContent("c1", tool, args), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        var error = (string)result!.GetType().GetProperty("error")!.GetValue(result)!;
        Assert.Contains("read-only here", error, StringComparison.Ordinal);
        Assert.Contains("read_file shows you the picture", error, StringComparison.Ordinal);
    }
}
