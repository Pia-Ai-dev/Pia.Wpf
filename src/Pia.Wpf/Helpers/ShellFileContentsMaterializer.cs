using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Native;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Pia.Helpers;

/// <summary>What one virtual-file drop produced: the files we wrote, and the names we could not.</summary>
public sealed record ShellDropMaterializeResult(IReadOnlyList<string> Paths, IReadOnlyList<string> FailedNames);

/// <summary>
/// Writes the items of a virtual-file drag to disk. Outlook's message list has no path to offer — a mail is a
/// MAPI store row — so it publishes names in CFSTR_FILEDESCRIPTORW and bytes in CFSTR_FILECONTENTS, and the
/// receiver materialises them itself. Explorer does the same, which is why dropping a mail into a folder
/// yields a real .msg.
/// </summary>
public static class ShellFileContentsMaterializer
{
    // Ordered by what the sources we care about actually offer. A .msg IS a compound file, so Outlook's
    // message list hands over the live IStorage; attachments dragged out of an open mail come as a stream.
    private static readonly ComTypes.TYMED[] Mediums =
    [
        ComTypes.TYMED.TYMED_ISTORAGE,
        ComTypes.TYMED.TYMED_ISTREAM,
        ComTypes.TYMED.TYMED_HGLOBAL,
    ];

    private const ComTypes.TYMED AnyMedium =
        ComTypes.TYMED.TYMED_ISTORAGE | ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL;

    /// <summary>A ceiling on one item, so a hostile or broken source cannot fill the disk during a drop. Well
    /// above any mail the readers downstream will accept.</summary>
    private const long MaxItemBytes = 256L * 1024 * 1024;

    public static bool IsPresent(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(ShellDataObject.FileGroupDescriptorW, autoConvert: false);
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads CF_HDROP straight off the COM data object. WPF's managed <c>GetData</c> catches whatever the source
    /// returned and hands back null, so a Chromium-hosted source that writes its file during the drop looks
    /// indistinguishable from one carrying no files at all.
    /// </summary>
    public static IReadOnlyList<string> ReadDropPaths(IDataObject data, ILogger logger)
    {
        if (data is not ComTypes.IDataObject comData) return [];

        var request = new ComTypes.FORMATETC
        {
            cfFormat = CF_HDROP,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };

        var medium = default(ComTypes.STGMEDIUM);
        try
        {
            comData.GetData(ref request, out medium);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or NotImplementedException)
        {
            // Expected, and the whole reason this method exists: new Outlook advertises CF_HDROP and then
            // answers DV_E_FORMATETC, because its drag carries mailbox row keys rather than a file.
            logger.LogDebug("Drop: the source refused CF_HDROP (0x{Hr:X8})", ex.HResult);
            return [];
        }

        try
        {
            if (medium.tymed != ComTypes.TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero) return [];

            var pointer = ShellDataObject.GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero) return [];
            try
            {
                var size = (int)ShellDataObject.GlobalSize(medium.unionmember);
                if (size <= DropFilesHeaderSize) return [];

                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return ParseDropFiles(bytes);
            }
            finally
            {
                ShellDataObject.GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ShellDataObject.ReleaseStgMedium(ref medium);
        }
    }

    private const short CF_HDROP = 15;
    private const int DropFilesHeaderSize = 20;
    private const int DropFilesOffsetField = 0;
    private const int DropFilesWideField = 16;

    private static IReadOnlyList<string> ParseDropFiles(byte[] buffer)
    {
        var offset = (int)BitConverter.ToUInt32(buffer, DropFilesOffsetField);
        var wide = BitConverter.ToInt32(buffer, DropFilesWideField) != 0;
        if (offset < DropFilesHeaderSize || offset >= buffer.Length) return [];

        var list = wide
            ? System.Text.Encoding.Unicode.GetString(buffer, offset, (buffer.Length - offset) & ~1)
            : System.Text.Encoding.Default.GetString(buffer, offset, buffer.Length - offset);

        return list.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The names the source would give its items. The only place a dragged mail has an extension.</summary>
    public static IReadOnlyList<FileGroupDescriptorEntry> ReadDescriptor(IDataObject data)
    {
        var bytes = TryReadDescriptorBytes(data);
        return bytes is null ? [] : FileGroupDescriptor.Parse(bytes);
    }

    /// <summary>
    /// Pulls each item's bytes and writes them into <paramref name="targetDirectory"/>. Must run on the drag's
    /// own thread while the drop is still in flight: the source is free to tear the data object down once
    /// DoDragDrop returns, and the interface is apartment-bound.
    /// </summary>
    public static ShellDropMaterializeResult Materialize(
        IDataObject data,
        IReadOnlyList<FileGroupDescriptorEntry> items,
        string targetDirectory,
        ILogger logger)
    {
        var paths = new List<string>();
        var failed = new List<string>();
        if (items.Count == 0) return new ShellDropMaterializeResult(paths, failed);

        if (data is not ComTypes.IDataObject comData)
        {
            logger.LogWarning("Virtual-file drop: no COM data object behind {Type}", data.GetType().Name);
            return new ShellDropMaterializeResult(paths, items.Select(i => i.FileName).ToList());
        }

        LogAdvertisedFormats(comData, logger);

        var format = unchecked((short)ShellDataObject.RegisterClipboardFormat(ShellDataObject.FileContents));
        if (format == 0)
        {
            logger.LogWarning("Virtual-file drop: FileContents is not a registered clipboard format");
            return new ShellDropMaterializeResult(paths, items.Select(i => i.FileName).ToList());
        }

        Directory.CreateDirectory(targetDirectory);

        for (var index = 0; index < items.Count; index++)
        {
            var name = items[index].FileName;
            var safeName = FileGroupDescriptor.ToSafeFileName(name);
            if (safeName is null)
            {
                failed.Add(name);
                continue;
            }

            var destination = UniquePath(targetDirectory, safeName);
            if (TryWriteItem(comData, format, index, items[index].Length, destination, logger))
                paths.Add(destination);
            else
                failed.Add(name);
        }

        logger.LogInformation(
            "Virtual-file drop materialised {Written} of {Total} items", paths.Count, items.Count);
        return new ShellDropMaterializeResult(paths, failed);
    }

    private static bool TryWriteItem(
        ComTypes.IDataObject data, short format, int index, long? declaredLength, string destination, ILogger logger)
    {
        // Ask for anything first — one cross-process round trip in the happy path — then fall back to the
        // single-medium forms for a source that refuses a combined mask.
        if (TryWriteItem(data, format, index, AnyMedium, declaredLength, destination, logger)) return true;

        foreach (var medium in Mediums)
        {
            if (TryWriteItem(data, format, index, medium, declaredLength, destination, logger)) return true;
        }

        logger.LogWarning("Virtual-file drop: item {Index} could not be read in any medium", index);
        return false;
    }

    private static bool TryWriteItem(
        ComTypes.IDataObject data,
        short format,
        int index,
        ComTypes.TYMED tymed,
        long? declaredLength,
        string destination,
        ILogger logger)
    {
        var request = new ComTypes.FORMATETC
        {
            cfFormat = format,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = tymed,
        };

        var medium = default(ComTypes.STGMEDIUM);
        try
        {
            data.GetData(ref request, out medium);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or NotImplementedException)
        {
            logger.LogDebug("Virtual-file drop: item {Index} refused {Tymed} (0x{Hr:X8})", index, tymed, ex.HResult);
            return false;
        }

        try
        {
            switch (medium.tymed)
            {
                case ComTypes.TYMED.TYMED_ISTORAGE:
                    return WriteStorage(medium.unionmember, destination, index, logger);
                case ComTypes.TYMED.TYMED_ISTREAM:
                    return WriteStream(medium.unionmember, declaredLength, destination, index, logger);
                case ComTypes.TYMED.TYMED_HGLOBAL:
                    return WriteGlobal(medium.unionmember, destination, index, logger);
                default:
                    logger.LogDebug("Virtual-file drop: item {Index} came back as {Tymed}", index, medium.tymed);
                    return false;
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException
            or UnauthorizedAccessException or InvalidCastException)
        {
            logger.LogWarning(ex, "Virtual-file drop: writing item {Index} failed", index);
            TryDelete(destination);
            return false;
        }
        finally
        {
            ShellDataObject.ReleaseStgMedium(ref medium);
        }
    }

    private static bool WriteStorage(IntPtr source, string destination, int index, ILogger logger)
    {
        if (source == IntPtr.Zero) return false;

        if (Marshal.GetObjectForIUnknown(source) is not ShellDataObject.IStorage storage) return false;
        try
        {
            ShellDataObject.StgCreateDocfile(
                destination,
                ShellDataObject.STGM_CREATE | ShellDataObject.STGM_READWRITE | ShellDataObject.STGM_SHARE_EXCLUSIVE,
                0,
                out var target);
            try
            {
                storage.CopyTo(0, IntPtr.Zero, IntPtr.Zero, target);
                target.Commit(0);
            }
            finally
            {
                Marshal.ReleaseComObject(target);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(storage);
        }

        logger.LogDebug("Virtual-file drop: wrote item {Index} from a storage", index);
        return true;
    }

    private static bool WriteStream(
        IntPtr source, long? declaredLength, string destination, int index, ILogger logger)
    {
        if (source == IntPtr.Zero) return false;
        if (Marshal.GetObjectForIUnknown(source) is not ComTypes.IStream stream) return false;

        var read = Marshal.AllocHGlobal(sizeof(int));
        long written = 0;
        try
        {
            using var file = File.Create(destination);
            var buffer = new byte[81920];
            while (written < MaxItemBytes)
            {
                stream.Read(buffer, buffer.Length, read);
                var count = Marshal.ReadInt32(read);
                if (count <= 0) break;
                file.Write(buffer, 0, count);
                written += count;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
            Marshal.ReleaseComObject(stream);
        }

        // A source is allowed to answer S_OK with a short read, so "the loop ended" is not "the file is
        // whole". Without this a truncated .msg reaches the CFB reader and surfaces as an unexplained
        // parse failure instead of a transfer failure.
        if (declaredLength is { } expected && expected > 0 && written != expected)
        {
            logger.LogWarning(
                "Virtual-file drop: item {Index} stopped at {Written} of {Expected} bytes", index, written, expected);
            TryDelete(destination);
            return false;
        }

        logger.LogDebug("Virtual-file drop: wrote item {Index} from a stream ({Bytes} bytes)", index, written);
        return true;
    }

    private static bool WriteGlobal(IntPtr source, string destination, int index, ILogger logger)
    {
        if (source == IntPtr.Zero) return false;

        var pointer = ShellDataObject.GlobalLock(source);
        if (pointer == IntPtr.Zero) return false;
        try
        {
            var size = (long)ShellDataObject.GlobalSize(source);
            if (size <= 0 || size > MaxItemBytes) return false;

            using var file = File.Create(destination);
            var buffer = new byte[81920];
            for (long written = 0; written < size;)
            {
                var chunk = (int)Math.Min(buffer.Length, size - written);
                Marshal.Copy(pointer + (nint)written, buffer, 0, chunk);
                file.Write(buffer, 0, chunk);
                written += chunk;
            }
        }
        finally
        {
            ShellDataObject.GlobalUnlock(source);
        }

        logger.LogDebug("Virtual-file drop: wrote item {Index} from memory", index);
        return true;
    }

    private static byte[]? TryReadDescriptorBytes(IDataObject data)
    {
        try
        {
            switch (data.GetData(ShellDataObject.FileGroupDescriptorW, autoConvert: false))
            {
                case byte[] bytes:
                    return bytes;
                case MemoryStream memory:
                    return memory.ToArray();
                case Stream stream:
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        return buffer.ToArray();
                    }
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or NotSupportedException)
        {
            // Falls through to the COM read: a source that will not serve the descriptor through WPF's
            // managed conversion may still serve it as a plain HGLOBAL.
        }

        return TryReadDescriptorBytesViaCom(data);
    }

    private static byte[]? TryReadDescriptorBytesViaCom(IDataObject data)
    {
        if (data is not ComTypes.IDataObject comData) return null;

        var format = unchecked((short)ShellDataObject.RegisterClipboardFormat(ShellDataObject.FileGroupDescriptorW));
        if (format == 0) return null;

        var request = new ComTypes.FORMATETC
        {
            cfFormat = format,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };

        var medium = default(ComTypes.STGMEDIUM);
        try
        {
            comData.GetData(ref request, out medium);
            if (medium.tymed != ComTypes.TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero) return null;

            var pointer = ShellDataObject.GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                var size = (int)ShellDataObject.GlobalSize(medium.unionmember);
                if (size <= 0) return null;
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                ShellDataObject.GlobalUnlock(medium.unionmember);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or NotImplementedException)
        {
            return null;
        }
        finally
        {
            ShellDataObject.ReleaseStgMedium(ref medium);
        }
    }

    private static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Dev-only: what the source actually advertises, which is the one thing no amount of reading settles.</summary>
    [Conditional("DEBUG")]
    public static void LogDragFormats(IDataObject data, ILogger logger)
    {
        logger.LogDebug("Virtual-file drag from {Type}, com={Com}",
            data.GetType().Name, data is ComTypes.IDataObject);

        if (data is ComTypes.IDataObject comData) LogAdvertisedFormats(comData, logger);

        var bytes = TryReadDescriptorBytes(data);
        logger.LogDebug("Virtual-file drag descriptor: {Bytes} bytes", bytes?.Length ?? -1);
        if (bytes is null) return;

        foreach (var item in FileGroupDescriptor.Parse(bytes))
            logger.SensitiveDebug("Virtual-file drag offers {Name} ({Length})", item.FileName, item.Length);
    }

    [Conditional("DEBUG")]
    private static void LogAdvertisedFormats(ComTypes.IDataObject data, ILogger logger)
    {
        try
        {
            var enumerator = data.EnumFormatEtc(ComTypes.DATADIR.DATADIR_GET);
            var entry = new ComTypes.FORMATETC[1];
            var fetched = new int[1];
            var name = new char[256];

            while (enumerator.Next(1, entry, fetched) == 0 && fetched[0] == 1)
            {
                var id = unchecked((ushort)entry[0].cfFormat);
                var length = ShellDataObject.GetClipboardFormatName(id, name, name.Length);
                var label = length > 0 ? new string(name, 0, length) : $"#{id}";
                logger.SensitiveDebug(
                    "Virtual-file drop advertises {Format} (id {Id}) tymed={Tymed} lindex={Lindex}",
                    label, id, entry[0].tymed, entry[0].lindex);
            }
        }
        catch (Exception ex) when (ex is ExternalException or NotImplementedException)
        {
            logger.LogDebug("Virtual-file drop: the source does not enumerate its formats");
        }
    }
}
