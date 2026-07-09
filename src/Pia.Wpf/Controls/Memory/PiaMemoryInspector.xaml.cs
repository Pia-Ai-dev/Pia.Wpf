using System.Windows.Controls;
using Pia.Helpers;
using Pia.ViewModels;

namespace Pia.Controls.Memory;

public partial class PiaMemoryInspector : UserControl
{
    public PiaMemoryInspector()
    {
        InitializeComponent();
        // BodyMarkdown lives for the inspector's lifetime, so a one-time subscription in the ctor cannot
        // leak (both objects are collected together) — no Loaded/Unloaded churn needed.
        BodyMarkdown.WikiLinkNavigate += OnWikiLinkNavigate;
    }

    // The inspector's DataContext is the selected VaultMemoryItem, not the VM, so reach the VM through the
    // MemoryView ancestor (mirrors PiaMemoryCategoryCard) and let it resolve the target and select the page.
    private void OnWikiLinkNavigate(object? sender, string target)
    {
        var vm = this.FindAncestor<Views.MemoryView>()?.DataContext as MemoryViewModel;
        vm?.NavigateToLinkCommand.Execute(target);
    }
}
