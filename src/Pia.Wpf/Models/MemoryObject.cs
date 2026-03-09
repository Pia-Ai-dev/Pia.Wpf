using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

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
}

public static class MemoryObjectTypes
{
    public const string PersonalProfile = "personal_profile";
    public const string ContactList = "contact_list";
    public const string Preference = "preference";
    public const string Note = "note";

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
        _ => type
    };
}
