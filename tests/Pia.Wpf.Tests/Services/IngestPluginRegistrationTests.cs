using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Regression lock for the ingest tool being REACHABLE by the model. The ingest pipeline was fully
/// built but the tool was never surfaced (memory-vault open-questions Q3). These assert the wiring
/// contract that makes it reachable: a preloaded, default-enabled built-in whose adapter exposes an
/// "ingest" tool plus a staging-recipe system prompt. Combined with PluginService's "ingest" switch
/// arm and its defaultEnabled gate, this locks that GetAllTools() surfaces ingest.
/// </summary>
public class IngestPluginRegistrationTests
{
    private static SyncPlugin IngestConfig() =>
        BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.IngestPluginId];

    [Fact]
    public void IngestPlugin_IsPreloadedAndDefaultEnabled()
    {
        Assert.Contains(BuiltInPluginDefaults.IngestPluginId, BuiltInPluginDefaults.PreloadedPluginIds);

        var config = IngestConfig();
        Assert.True(config.IsPreloaded);
        Assert.True(config.IsActive);
        Assert.Contains("\"handlerId\":\"ingest\"", config.ConfigJson);
        Assert.Contains("\"defaultEnabled\":true", config.ConfigJson);
    }

    [Fact]
    public void IngestPlugin_SystemPrompt_DocumentsSourcesAndStagingRecipe()
    {
        var config = IngestConfig().ConfigJson;

        // The model must learn where raw files live, the Vault/sources staging path, and that the
        // compiled content becomes recall-visible.
        Assert.Contains("sources/", config);
        Assert.Contains("Vault/sources/", config);
        Assert.Contains("recall", config);
    }

    [Fact]
    public void FromIngestHandler_ExposesIngestToolAndSystemPrompt()
    {
        var scheduler = Substitute.For<IIngestScheduler>();
        var handler = new IngestToolHandler(scheduler, NullLogger<IngestToolHandler>.Instance);

        var adapter = BuiltInPluginHandler.FromIngestHandler(handler, IngestConfig());

        Assert.Contains(adapter.GetTools(), t => t.Name == "ingest");
        Assert.False(string.IsNullOrWhiteSpace(adapter.GetSystemPromptAddition()));
    }
}
