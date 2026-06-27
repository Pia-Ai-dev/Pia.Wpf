using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The file tools are gated on <see cref="AppSettings.AssistantFileToolsEnabled"/> in addition to a
/// configured folder. The folder is always set now (the vault lives under it), so the toggle is the
/// explicit on/off switch that replaces the old "clear the folder to disable".
/// </summary>
public class FilesToolHandlerAvailabilityTests : IDisposable
{
    private readonly string _root;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerAvailabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-avail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FilesToolHandler Handler(bool enabled)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = _root,
            AssistantFileToolsEnabled = enabled,
        });
        return new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    [Fact]
    public void Available_when_enabled_and_folder_set()
    {
        Assert.True(Handler(enabled: true).IsAvailable);
    }

    [Fact]
    public void Unavailable_when_disabled_even_with_folder_set()
    {
        Assert.False(Handler(enabled: false).IsAvailable);
    }
}
