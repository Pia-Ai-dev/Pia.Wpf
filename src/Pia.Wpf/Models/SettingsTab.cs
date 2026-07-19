namespace Pia.Models;

/// <summary>
/// Outer settings sidebar tabs. Values are the SettingsViewModel.SelectedTabIndex
/// positions and the DataTrigger values in SettingsView.xaml.
/// </summary>
public enum SettingsTab
{
    General = 0,
    Providers = 1,
    Optimize = 2,
    Assistant = 3,
    Account = 4,
    Plugins = 5
}

/// <summary>Inner tabs of the General settings pane (GeneralView.xaml).</summary>
public enum GeneralSettingsInnerTab
{
    Application = 0,
    Hotkeys = 1,
    Speech = 2,
    Privacy = 3
}

/// <summary>Inner tabs of the Assistant settings pane (SettingsViews/AssistantView.xaml).</summary>
public enum AssistantSettingsInnerTab
{
    General = 0,
    Personas = 1,
    ToolAccess = 2,
    Meeting = 3,
    AgentRuns = 4
}
