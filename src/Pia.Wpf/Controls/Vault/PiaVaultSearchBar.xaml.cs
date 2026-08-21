using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Vault;

public partial class PiaVaultSearchBar : UserControl
{
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(nameof(Query), typeof(string), typeof(PiaVaultSearchBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(PiaVaultSearchBar),
            new FrameworkPropertyMetadata(false));

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public PiaVaultSearchBar() => InitializeComponent();
}
