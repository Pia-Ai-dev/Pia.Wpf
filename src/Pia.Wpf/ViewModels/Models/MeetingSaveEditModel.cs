using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Services.LiveTranscription;

namespace Pia.ViewModels.Models;

/// <summary>
/// The optional details a user attaches to a meeting before it is written into the vault. Attendees and
/// tags are comma-separated here and split on the way out.
/// </summary>
public partial class MeetingSaveEditModel : ObservableObject
{
    private readonly DateTimeOffset _sessionStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(TargetReference))]
    private string _title;

    [ObservableProperty]
    private string _attendees;

    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private string _project = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    public MeetingSaveEditModel(DateTimeOffset sessionStart, string title, string attendees)
    {
        _sessionStart = sessionStart;
        _title = title;
        _attendees = attendees;
    }

    public bool CanSave => !string.IsNullOrWhiteSpace(Title);

    /// <summary>Where the file will land, shown live in the dialog so the title's effect is visible.</summary>
    public string TargetReference => MeetingVaultMarkdown.BuildReference(_sessionStart, Title);

    public MeetingVaultMetadata ToMetadata(DateTimeOffset sessionEnd, string source) => new(
        Title.Trim(),
        _sessionStart,
        sessionEnd,
        source,
        MeetingVaultMarkdown.SplitList(Attendees),
        MeetingVaultMarkdown.SplitList(Tags),
        Project,
        Notes);
}
