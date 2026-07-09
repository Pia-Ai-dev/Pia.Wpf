using CommunityToolkit.Mvvm.Input;
using Pia.Helpers;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

/// <summary>
/// The Memory-vault help content as a modal dialog overlay. Replaces the former inline help card so the
/// help text overlays the page rather than pushing all content down. The "open memory vault" affordance
/// reveals <c>vaultRoot</c> in Explorer.
/// </summary>
public partial class MemoryHelpContentDialog : ContentDialog
{
    public MemoryHelpContentDialog(ContentDialogHost dialogHost, string vaultRoot)
        : base(dialogHost)
    {
        DataContext = new MemoryHelpDialogViewModel(vaultRoot);
        InitializeComponent();
    }

    private sealed partial class MemoryHelpDialogViewModel
    {
        private readonly string _vaultRoot;

        public MemoryHelpDialogViewModel(string vaultRoot) => _vaultRoot = vaultRoot;

        [RelayCommand]
        private void OpenVaultFolder() => ShellLauncher.RevealInExplorer(_vaultRoot);
    }
}
