using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels.Models;

/// <summary>One locally added MCP server in the list pane.</summary>
public partial class McpServerRow : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _commandLine;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Switched on but not up — a different badge from the user having switched it off.</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>The subprocess is starting or stopping, and how long that takes is not knowable.</summary>
    [ObservableProperty]
    private bool _isToggling;

    public McpServerRow(Guid id, string name, string commandLine, bool isEnabled)
    {
        Id = id;
        _name = name;
        _commandLine = commandLine;
        _isEnabled = isEnabled;
    }
}

/// <summary>One tool a probe reported, with the tick that decides whether the model ever sees it.</summary>
public partial class McpToolRow : ObservableObject
{
    public string Name { get; }
    public string? Description { get; }
    public bool ServerDeclaredDestructive { get; }

    [ObservableProperty]
    private bool _isAllowed;

    public McpToolRow(string name, string? description, bool serverDeclaredDestructive, bool isAllowed)
    {
        Name = name;
        Description = description;
        ServerDeclaredDestructive = serverDeclaredDestructive;
        _isAllowed = isAllowed;
    }
}

/// <summary>One tool as the read-only detail pane shows it; the editor's ticks live on <see cref="McpToolRow"/>.</summary>
public sealed record McpServerToolInfo(string Name, string? Description, bool ServerDeclaredDestructive, bool IsAllowed)
{
    public bool IsWithheld => !IsAllowed;

    // The item container reports this to UIA; the record default would read out the whole dump.
    public override string ToString() => Name;
}
