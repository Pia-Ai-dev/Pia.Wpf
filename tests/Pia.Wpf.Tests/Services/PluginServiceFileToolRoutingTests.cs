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
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class PluginServiceFileToolRoutingTests : IDisposable
{
    private static readonly string[] FileTools =
        { "list_files", "find_files", "read_file", "write_file", "delete_file", "search_files" };

    private string? _runRoot;

    public void Dispose()
    {
        TaskAmbient.Current = null;
        if (_runRoot is not null)
            TempPath.Remove(_runRoot);
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
    public void FilesAdapter_ExposesEveryFileTool_WithNoInteractiveFolder()
    {
        var handler = NoFolderHandler();
        Assert.True(handler.IsAvailable);

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

        // A pending action is the evidence of routing: an unrouted tool falls through to "Unknown tool".
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), WorkingSubpath: null, OnFileTouched: null, WorkspaceRoot: _runRoot);

        var call = new FunctionCallContent("w", "write_file",
            new Dictionary<string, object?> { ["path"] = "note.txt", ["content"] = "hi" });
        var (result, pending) = await adapter.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("write_file", pending!.ToolName);
    }
}
