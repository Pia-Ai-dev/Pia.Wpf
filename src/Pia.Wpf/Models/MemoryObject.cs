using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

public enum MemoryType
{
    Profile,
    Preference,
    Project,
    Skill,
    Context,
    Note
}

public partial class MemoryObject : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _data = "{}";

    public byte[]? Embedding { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.UtcNow;

    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    [ObservableProperty]
    private IReadOnlyList<double> _accessTimeline = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<RelatedMemory> _related = Array.Empty<RelatedMemory>();

    [ObservableProperty]
    private string? _sourceLabel;

    [ObservableProperty]
    private Guid? _sourceConversationId;

    public MemoryType TypeKind => MemoryObjectTypes.ToKind(Type);

    public string ShortId
    {
        get
        {
            var s = Id.ToString("N");
            return s.Length >= 8 ? $"{s[..4]}…{s[^4..]}" : s;
        }
    }

    public bool IsStale => (DateTime.UtcNow - LastAccessedAt) > TimeSpan.FromDays(90);

    public int AccessCount
    {
        get
        {
            var sum = 0d;
            foreach (var v in AccessTimeline) sum += v;
            return (int)sum;
        }
    }

    public string ValuePreview
    {
        get
        {
            try
            {
                var node = JsonNode.Parse(Data);
                return node switch
                {
                    JsonObject obj => SummarizeObject(obj),
                    JsonArray arr => $"[{arr.Count} item{(arr.Count == 1 ? "" : "s")}]",
                    JsonValue val => val.ToJsonString().Trim('"'),
                    _ => Data
                };
            }
            catch
            {
                var first = Data.Split('\n', 2)[0];
                return first.Length > 120 ? first[..120] + "…" : first;
            }
        }
    }

    private static string SummarizeObject(JsonObject obj)
    {
        var parts = new List<string>();
        foreach (var kvp in obj)
        {
            var v = kvp.Value switch
            {
                JsonValue jv => jv.ToJsonString().Trim('"'),
                JsonObject => "{…}",
                JsonArray a => $"[{a.Count}]",
                _ => "null"
            };
            parts.Add($"{kvp.Key}: {v}");
            if (parts.Count >= 3) break;
        }
        var joined = string.Join(" · ", parts);
        return joined.Length > 120 ? joined[..120] + "…" : joined;
    }
}

public sealed record RelatedMemory(Guid Id, string Title, MemoryType Type, double Score);

public static class MemoryObjectTypes
{
    public const string PersonalProfile = "personal_profile";
    public const string ContactList = "contact_list";
    public const string Preference = "preference";
    public const string Note = "note";
    public const string Project = "project";
    public const string Skill = "skill";
    public const string Context = "context";

    public static readonly IReadOnlyList<string> All =
    [
        PersonalProfile,
        ContactList,
        Preference,
        Note
    ];

    public static string GetDisplayName(string type) => type switch
    {
        PersonalProfile => "Personal Profile",
        ContactList => "Contacts",
        Preference => "Preferences",
        Note => "Notes & Knowledge",
        Project => "Projects",
        Skill => "Skills",
        Context => "Context",
        _ => type
    };

    public static MemoryType ToKind(string type) => type switch
    {
        PersonalProfile or "profile" => MemoryType.Profile,
        ContactList => MemoryType.Profile,
        Preference => MemoryType.Preference,
        Project => MemoryType.Project,
        Skill => MemoryType.Skill,
        Context => MemoryType.Context,
        _ => MemoryType.Note
    };
}
