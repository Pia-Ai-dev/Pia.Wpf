using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Pia.Views.Controls;
using Pia.ViewModels;
using Wpf.Ui.Controls;

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
            if (e.PropertyName is nameof(AdvancedCreationViewModel.HasStarted)
                or nameof(AdvancedCreationViewModel.IsComplete))
                UpdateButtonIcons();
        };

        UpdateButtonIcons();

        // DialogOverlayHost focuses the panel itself after its show animation, which lands after anything
        // Loaded could do. Handing focus on from there is what actually reaches the box.
        GotKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(e.NewFocus, this) && OpeningBox.IsVisible)
                Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(OpeningBox)), DispatcherPriority.Input);
        };
    }

    /// <summary>The glyph follows the label the view model picked; only the View knows which symbol means
    /// which step.</summary>
    private void UpdateButtonIcons()
    {
        if (_viewModel is null) return;

        PrimaryButtonIcon = new SymbolIcon
        {
            Symbol = _viewModel.IsComplete ? SymbolRegular.Checkmark24
                : _viewModel.HasStarted ? SymbolRegular.Send24
                : SymbolRegular.Sparkle24,
        };
        SecondaryButtonIcon = new SymbolIcon { Symbol = SymbolRegular.ChevronRight24 };
        CloseButtonIcon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 };
    }

    /// <summary>The footer is the interview's only action row, so its primary and secondary drive the turn
    /// instead of ending the dialog. Only the finished draft — and Close — may close it.</summary>
    protected override void RaiseResultChosen(object result)
    {
        if (_viewModel is not null && !_viewModel.IsComplete)
        {
            var command = result switch
            {
                OverlayDialogResult.Primary =>
                    _viewModel.HasStarted ? _viewModel.SendCommand : (ICommand)_viewModel.StartCommand,
                OverlayDialogResult.Secondary => _viewModel.SkipCommand,
                _ => null,
            };

            if (command is not null)
            {
                if (command.CanExecute(null)) command.Execute(null);
                return;
            }
        }

        base.RaiseResultChosen(result);
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
