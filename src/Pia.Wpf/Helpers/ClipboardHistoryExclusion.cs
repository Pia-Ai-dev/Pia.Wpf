using System.IO;
using System.Windows;

namespace Pia.Helpers;

/// <summary>Builds clipboard data that Windows keeps out of clipboard history (Win+V) and cloud clipboard sync.</summary>
public static class ClipboardHistoryExclusion
{
    public const string ExcludeFromMonitorsFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    public static DataObject Wrap(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        // Streams, not primitives: anything else would need BinaryFormatter, which .NET no longer ships.
        data.SetData(ExcludeFromMonitorsFormat, new MemoryStream([0]));
        data.SetData(CanIncludeInHistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData(CanUploadToCloudFormat, new MemoryStream(BitConverter.GetBytes(0)));
        return data;
    }
}
