using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class FilesToolHandlerWriteTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerWriteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-write-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<FilesToolCall> PrepareWrite(string path, object? content, bool includeContent = true)
    {
        var args = new Dictionary<string, object?> { ["path"] = path };
        if (includeContent) args["content"] = content;
        var call = new FunctionCallContent("c1", "write_file", args);
        var (_, pending) = await _handler.HandleToolCallAsync(call);
        Assert.NotNull(pending);
        return pending!;
    }

    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    // ---- (c) atomic write preserves CRLF + BOM ----

    [Fact]
    public async Task Write_PreservesCrlfAndBom_OnExistingFile()
    {
        var full = Path.Combine(_root, "crlf-bom.txt");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("one\r\ntwo\r\n")).ToArray();
        File.WriteAllBytes(full, original);

        var pending = await PrepareWrite("crlf-bom.txt", "alpha\nbeta\ngamma");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));

        var written = File.ReadAllBytes(full);
        // BOM preserved.
        Assert.Equal(0xEF, written[0]);
        Assert.Equal(0xBB, written[1]);
        Assert.Equal(0xBF, written[2]);
        // LF in the content was normalized to CRLF (the file's dominant EOL).
        var text = Encoding.UTF8.GetString(written, 3, written.Length - 3);
        Assert.Contains("alpha\r\nbeta\r\ngamma", text);
        Assert.DoesNotContain("alpha\nbeta", text.Replace("\r\n", "<CRLF>"));
    }

    [Fact]
    public async Task Write_NewFile_DefaultsToCrlf_NoBom()
    {
        var pending = await PrepareWrite("fresh.txt", "a\nb\nc");
        var result = await pending.Execute();
        Assert.True(Prop<bool>(result!, "success"));

        var bytes = File.ReadAllBytes(Path.Combine(_root, "fresh.txt"));
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Equal("a\r\nb\r\nc", text); // CRLF convention for new files
    }

    [Fact]
    public async Task Write_PreservesLf_OnLfFile()
    {
        var full = Path.Combine(_root, "lf.txt");
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes("x\ny\nz\n"));

        var pending = await PrepareWrite("lf.txt", "p\r\nq\r\nr");
        await pending.Execute();

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(full));
        Assert.Equal("p\nq\nr", text); // CRLF input normalized down to the file's LF
    }

    // ---- (a) missing content arg yields an error, not an empty file ----

    [Fact]
    public async Task Write_MissingContent_ReturnsError_NoFileWritten()
    {
        var pending = await PrepareWrite("nope.txt", null, includeContent: false);
        var result = await pending.Execute();

        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("missing", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "nope.txt")));
    }

    [Fact]
    public async Task Write_NullContent_ReturnsError_NoFileWritten()
    {
        var pending = await PrepareWrite("nullc.txt", null, includeContent: true);
        var result = await pending.Execute();

        Assert.False(Prop<bool>(result!, "success"));
        Assert.False(File.Exists(Path.Combine(_root, "nullc.txt")));
    }

    [Fact]
    public async Task Write_EmptyStringContent_IsAllowed()
    {
        var pending = await PrepareWrite("empty.txt", "");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        Assert.True(File.Exists(Path.Combine(_root, "empty.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(_root, "empty.txt")).Length);
    }

    // ---- (a) internal-content guard fires ----

    [Fact]
    public async Task Write_ReadFileEcho_IsRejected()
    {
        var echo = "total_lines=3\n1|first line\n2|second line\n3|third line";
        var pending = await PrepareWrite("echo.txt", echo);
        var result = await pending.Execute();

        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("read_file", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "echo.txt")));
    }

    [Fact]
    public async Task Write_NormalContentWithOnePipe_IsNotRejected()
    {
        var content = "name|value\nrealdata\nmore lines\nfinal";
        var pending = await PrepareWrite("table.txt", content);
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
    }

    // ---- (e) delta-filtered lint: new JSON error surfaced, pre-existing not ----

    [Fact]
    public async Task Write_NewJsonSyntaxError_IsSurfaced()
    {
        // Create a valid JSON file, then overwrite with broken JSON → NEW error.
        File.WriteAllText(Path.Combine(_root, "cfg.json"), "{\"a\":1}");

        var pending = await PrepareWrite("cfg.json", "{\"a\": }");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        var lint = Prop<string?>(result!, "lint");
        Assert.NotNull(lint);
        Assert.Contains("JSON", lint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_PreExistingJsonError_IsNotBlamed()
    {
        // File is already broken; writing keeps it broken the SAME way → not surfaced.
        File.WriteAllText(Path.Combine(_root, "broken.json"), "{ broken");

        var pending = await PrepareWrite("broken.json", "{ broken");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        Assert.Null(Prop<string?>(result!, "lint"));
    }

    [Fact]
    public async Task Write_ValidJson_NoLint()
    {
        var pending = await PrepareWrite("ok.json", "{\"x\": [1,2,3]}");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        Assert.Null(Prop<string?>(result!, "lint"));
    }

    // ---- (f) staleness guard sets _warning on an out-of-band edit ----

    [Fact]
    public async Task Write_OutOfBandEdit_SetsWarning()
    {
        var full = Path.Combine(_root, "track.txt");
        File.WriteAllText(full, "v1");

        // Simulate a prior read recording the mtime under the ambient task (Guid.Empty in tests).
        _staleness.RecordRead(Guid.Empty, full, File.GetLastWriteTimeUtc(full));

        // Out-of-band touch AFTER the recorded read.
        File.WriteAllText(full, "tampered");
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(10));

        var pending = await PrepareWrite("track.txt", "v2");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        var warning = Prop<string?>(result!, "_warning");
        Assert.NotNull(warning);
        Assert.Contains("changed", warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_NoStaleness_NoWarning()
    {
        var full = Path.Combine(_root, "fresh2.txt");
        File.WriteAllText(full, "v1");
        _staleness.RecordRead(Guid.Empty, full, File.GetLastWriteTimeUtc(full));

        var pending = await PrepareWrite("fresh2.txt", "v2");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        Assert.Null(Prop<string?>(result!, "_warning"));
    }

    // ---- diff model populated for create and update ----

    [Fact]
    public async Task DiffPreview_AllAdded_ForNewFile()
    {
        var pending = await PrepareWrite("created.txt", "line1\nline2");

        Assert.NotNull(pending.DiffPreview);
        Assert.All(pending.DiffPreview!, d => Assert.Equal(DiffLineKind.Added, d.Kind));
        Assert.Equal(2, pending.DiffPreview!.Count);
    }

    [Fact]
    public async Task DiffPreview_AddedAndRemoved_ForUpdate()
    {
        File.WriteAllText(Path.Combine(_root, "upd.txt"), "keep\nremove-me\n");

        var pending = await PrepareWrite("upd.txt", "keep\nadd-me\n");

        Assert.NotNull(pending.DiffPreview);
        Assert.Contains(pending.DiffPreview!, d => d.Kind == DiffLineKind.Context && d.Text == "keep");
        Assert.Contains(pending.DiffPreview!, d => d.Kind == DiffLineKind.Removed && d.Text == "remove-me");
        Assert.Contains(pending.DiffPreview!, d => d.Kind == DiffLineKind.Added && d.Text == "add-me");
    }

    [Fact]
    public async Task Write_StructuredResult_ReportsBytesAndLines()
    {
        var pending = await PrepareWrite("metrics.txt", "a\nb\nc\nd");
        var result = await pending.Execute();

        Assert.True(Prop<bool>(result!, "success"));
        Assert.Equal(4, Prop<int>(result!, "lines"));
        Assert.True(Prop<long>(result!, "bytes_written") > 0);
        Assert.Equal("metrics.txt", Prop<string?>(result!, "resolved_path"));
    }

    [Fact]
    public async Task Write_AtomicReplace_LeavesNoTempFiles()
    {
        File.WriteAllText(Path.Combine(_root, "atomic.txt"), "before");
        var pending = await PrepareWrite("atomic.txt", "after");
        await pending.Execute();

        var temps = Directory.GetFiles(_root, "*.tmp");
        Assert.Empty(temps);
        Assert.Equal("after", File.ReadAllText(Path.Combine(_root, "atomic.txt")).Replace("\r\n", "\n"));
    }

    // ---- sensitive-path blocklist applies to delete_file and read_file (broad-sandbox case) ----

    // Builds a handler whose sandbox is configured broadly enough to CONTAIN a blocked root,
    // so the blocklist (not mere containment) is what must reject the operation.
    private static FilesToolHandler BroadSandboxHandler(string broadRoot)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = broadRoot });
        return new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    [Fact]
    public async Task Delete_BlockedPath_IsRefused_NoFileTouched()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        Assert.False(string.IsNullOrEmpty(localAppData));

        // Sandbox = %LOCALAPPDATA% (broad). The blocklist must still refuse Pia's own data dir.
        var handler = BroadSandboxHandler(localAppData);
        var target = Path.Combine("Pia", "delete-guard-" + Guid.NewGuid().ToString("N") + ".txt");

        var call = new FunctionCallContent("d1", "delete_file",
            new Dictionary<string, object?> { ["path"] = target });
        var (_, pending) = await handler.HandleToolCallAsync(call);
        Assert.NotNull(pending);

        var result = await pending!.Execute();
        Assert.Contains("Refusing to delete", (string)result!);
    }

    [Fact]
    public async Task Read_BlockedPath_IsRefused()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        Assert.False(string.IsNullOrEmpty(localAppData));

        var handler = BroadSandboxHandler(localAppData);
        var target = Path.Combine("Pia", "history.db");

        var call = new FunctionCallContent("r1", "read_file",
            new Dictionary<string, object?> { ["path"] = target });
        var (result, _) = await handler.HandleToolCallAsync(call);
        Assert.Contains("Refusing to read", (string)result!);
    }
}
