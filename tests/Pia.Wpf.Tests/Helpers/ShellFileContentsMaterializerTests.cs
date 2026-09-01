using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Covers the half of the materializer that does not need a live drag: how the descriptor is fetched off the
/// data object. WPF hands a registered format back as a <see cref="MemoryStream"/>, so a reader that only
/// handled <c>byte[]</c> would find nothing and silently reject every mail.
/// </summary>
public sealed class ShellFileContentsMaterializerTests
{
    [Fact]
    public void ReadDescriptor_ReadsTheStreamWpfHandsBack()
    {
        var data = new StubDataObject { ["FileGroupDescriptorW"] = new MemoryStream(Descriptor("Angebot.msg")) };

        var items = ShellFileContentsMaterializer.ReadDescriptor(data);

        Assert.Equal(["Angebot.msg"], items.Select(i => i.FileName));
    }

    [Fact]
    public void ReadDescriptor_ReadsARawByteArray()
    {
        var data = new StubDataObject { ["FileGroupDescriptorW"] = Descriptor("Angebot.msg") };

        Assert.Equal(["Angebot.msg"], ShellFileContentsMaterializer.ReadDescriptor(data).Select(i => i.FileName));
    }

    [Fact]
    public void ReadDescriptor_ReadsAForwardOnlyStream()
    {
        var data = new StubDataObject { ["FileGroupDescriptorW"] = new ForwardOnlyStream(Descriptor("Angebot.msg")) };

        Assert.Equal(["Angebot.msg"], ShellFileContentsMaterializer.ReadDescriptor(data).Select(i => i.FileName));
    }

    [Fact]
    public void ReadDescriptor_IsEmptyWhenTheDragCarriesNoDescriptor()
    {
        Assert.Empty(ShellFileContentsMaterializer.ReadDescriptor(new StubDataObject()));
    }

    [Fact]
    public void IsPresent_AsksForTheExactFormatWithoutConversion()
    {
        var data = new StubDataObject { ["FileGroupDescriptorW"] = Descriptor("a.msg") };

        Assert.True(ShellFileContentsMaterializer.IsPresent(data));
        Assert.False(ShellFileContentsMaterializer.IsPresent(new StubDataObject()));
        Assert.Contains(("FileGroupDescriptorW", false), data.PresenceQueries);
    }

    /// <summary>A drag with no COM object behind it — which every managed-only caller is — must come back as a
    /// clean "nothing", not throw into a drop handler.</summary>
    [Fact]
    public void ReadDropPaths_IsEmptyWithoutAComDataObject()
    {
        Assert.Empty(ShellFileContentsMaterializer.ReadDropPaths(new StubDataObject(), NullLogger.Instance));
    }

    [Fact]
    public void Materialize_ReportsEveryNameAsFailedWithoutAComDataObject()
    {
        var items = new[] { new FileGroupDescriptorEntry("a.msg", 1), new FileGroupDescriptorEntry("b.msg", 2) };

        var result = ShellFileContentsMaterializer.Materialize(
            new StubDataObject(), items, Path.GetTempPath(), NullLogger.Instance);

        Assert.Empty(result.Paths);
        Assert.Equal(["a.msg", "b.msg"], result.FailedNames);
    }

    private static byte[] Descriptor(string name)
    {
        const int entrySize = 592;
        var buffer = new byte[4 + entrySize];
        BitConverter.GetBytes(1u).CopyTo(buffer, 0);
        Encoding.Unicode.GetBytes(name).CopyTo(buffer, 4 + 72);
        return buffer;
    }

    private sealed class StubDataObject : IDataObject
    {
        private readonly Dictionary<string, object> _data = [];

        internal List<(string Format, bool AutoConvert)> PresenceQueries { get; } = [];

        internal object this[string format]
        {
            set => _data[format] = value;
        }

        public object? GetData(string format, bool autoConvert) => _data.GetValueOrDefault(format);
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName!, true);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            PresenceQueries.Add((format, autoConvert));
            return _data.ContainsKey(format);
        }

        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, true);

        public string[] GetFormats(bool autoConvert) => [.. _data.Keys];
        public string[] GetFormats() => GetFormats(true);

        public void SetData(string format, object? data, bool autoConvert) => _data[format] = data!;
        public void SetData(string format, object? data) => _data[format] = data!;
        public void SetData(Type format, object? data) => _data[format.FullName!] = data!;
        public void SetData(object? data) => _data[data!.GetType().FullName!] = data;
    }

    /// <summary>Stands in for a stream that cannot report its length, which the descriptor read must still
    /// drain rather than give up on.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
