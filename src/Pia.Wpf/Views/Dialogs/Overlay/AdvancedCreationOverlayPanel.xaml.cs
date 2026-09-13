using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Pia.Views.Controls;
using Pia.ViewModels;

namespace Pia.Views.Dialogs.Overlay;

public partial class AdvancedCreationOverlayPanel : OverlayDialogPanel
{
    private readonly AdvancedCreationViewModel? _viewModel;

    public AdvancedCreationOverlayPanel()
    {
        InitializeComponent();
    }

    public AdvancedCreationOverlayPanel(AdvancedCreationViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AdvancedCreationViewModel.IsComplete))
                IsPrimaryButtonEnabled = viewModel.IsComplete;
        };

        // Only meaningful once a draft exists; until then the panel's own buttons carry the interview.
        IsPrimaryButtonEnabled = viewModel.IsComplete;

        // DialogOverlayHost focuses the panel itself after its show animation, which lands after anything
        // Loaded could do. Handing focus on from there is what actually reaches the box.
        GotKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(e.NewFocus, this) && OpeningBox.IsVisible)
                Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(OpeningBox)), DispatcherPriority.Input);
        };
    }

    /// <summary>Confirms inside the panel instead of closing: the host holds one panel, so a confirmation
    /// dialog would replace this one and orphan the task the caller is awaiting.</summary>
    public override void OnEscapePressed()
    {
        if (_viewModel is null)
        {
            base.OnEscapePressed();
            return;
        }

        if (_viewModel.IsConfirmingDiscard)
            return;

        if (_viewModel.RequestClose())
            base.OnEscapePressed();
    }

    private void OnConfirmDiscard(object sender, RoutedEventArgs e) => base.OnEscapePressed();
}
