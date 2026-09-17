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
