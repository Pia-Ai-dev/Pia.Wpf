using System.IO;
using System.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// §17.3 route-table gap: the plugin host registers a handler's tool routes from its
/// <c>GetTools()</c>, which the files adapter gates on <see cref="FilesToolHandler.IsAvailable"/>.
/// Before the fix, <c>IsAvailable</c> required a configured interactive folder, so with no folder set
/// the route table stayed empty and a granted headless <c>write_file</c> routed to "Unknown tool."
/// Now <c>IsAvailable</c> tracks only the enabled flag — the five file tools register (and route to the
/// files handler) even with no interactive folder, because an unattended run supplies its own
/// WorkspaceRoot.
/// </summary>
public sealed class PluginServiceFileToolRoutingTests : IDisposable
{
    private static readonly string[] FileTools =
        { "list_files", "read_file", "write_file", "delete_file", "search_files" };

    private string? _runRoot;

    public void Dispose()
    {
        TaskAmbient.Current = null;
        if (_runRoot is not null)
            try { Directory.Delete(_runRoot, recursive: true); } catch { /* best effort */ }
    }

    private static FilesToolHandler NoFolderHandler()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = null,
            AssistantFileToolsEnabled = true,
        });
        return new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    private static SyncPlugin FilesConfig() =>
        BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.FilesPluginId];

    [Fact]
    public void FilesAdapter_ExposesAllFiveTools_WithNoInteractiveFolder()
    {
        var handler = NoFolderHandler();
        Assert.True(handler.IsAvailable); // enabled flag only now

        var adapter = BuiltInPluginHandler.FromFilesHandler(handler, FilesConfig());
        var names = adapter.GetTools().Select(t => t.Name).ToHashSet();

        foreach (var tool in FileTools)
            Assert.Contains(tool, names);
    }

    [Fact]
    public async Task WriteFile_RoutesToFilesHandler_UnderRunWorkspace_NotUnknownTool()
    {
        _runRoot = Path.Combine(Path.GetTempPath(), "pia-route-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_runRoot);

        var handler = NoFolderHandler();
        var adapter = BuiltInPluginHandler.FromFilesHandler(handler, FilesConfig());

        // An active per-run workspace: a granted write must reach the files handler and produce a
        // pending action (proving it routed to FilesToolHandler, not fell through to "Unknown tool").
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runRoot);

        var call = new FunctionCallContent("w", "write_file",
            new Dictionary<string, object?> { ["path"] = "note.txt", ["content"] = "hi" });
        var (result, pending) = await adapter.HandleToolCallAsync(call);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("write_file", pending!.ToolName);
    }
}
