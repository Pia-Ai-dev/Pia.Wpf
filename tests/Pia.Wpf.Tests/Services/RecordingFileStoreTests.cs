using System.IO;
using Pia.Services;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public sealed class RecordingFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pia-recording-store-{Guid.NewGuid():N}");

    public void Dispose() => TempPath.Remove(_root);

    private string Recording(TimeSpan age)
    {
        var path = RecordingFileStore.NewRecordingPath(_root);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void NewRecordingPath_creates_its_directory_and_names_a_wav_inside_it()
    {
        var path = RecordingFileStore.NewRecordingPath(_root);

        Assert.True(Directory.Exists(_root));
        Assert.Equal(_root, Path.GetDirectoryName(path));
        Assert.EndsWith(".wav", path);
    }

    [Fact]
    public void Sweep_deletes_stale_recordings_and_keeps_fresh_ones()
    {
        var stale = Recording(TimeSpan.FromHours(3));
        var fresh = Recording(TimeSpan.FromMinutes(5));

        var deleted = RecordingFileStore.Sweep([_root]);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Sweep_leaves_files_that_are_not_recordings()
    {
        Directory.CreateDirectory(_root);
        var other = Path.Combine(_root, "notes.wav");
        File.WriteAllBytes(other, [1]);
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromDays(2));

        Assert.Equal(0, RecordingFileStore.Sweep([_root]));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Sweep_skips_a_recording_that_is_still_open()
    {
        var path = Recording(TimeSpan.FromHours(3));
        using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Equal(0, RecordingFileStore.Sweep([_root]));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Sweep_ignores_missing_and_blank_directories()
        => Assert.Equal(0, RecordingFileStore.Sweep([Path.Combine(_root, "absent"), "", "   "]));
}
