using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

/// <summary>
/// Portable export envelope for assistant chats. An importer sniffs the file by
/// <see cref="Format"/>, so that literal is a contract — never localize or rename it.
/// </summary>
public class PiaChatArchive
{
    public const string FormatMarker = "pia.chat-archive";
    public const int CurrentFormatVersion = 1;

    public string Format { get; set; } = FormatMarker;

    /// <summary>Envelope version, independent of each chat's own <c>SchemaVersion</c>.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string? App { get; set; }
    public DateTime ExportedAt { get; set; }

    public List<SyncAssistantChat> Chats { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
