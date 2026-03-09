using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

public partial class VoiceModeOverlay : UserControl
{
    public VoiceModeOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is VoiceModeViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is VoiceModeViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateStateVisuals(newVm.State);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VoiceModeViewModel.State) && sender is VoiceModeViewModel vm)
        {
            Dispatcher.Invoke(() => UpdateStateVisuals(vm.State));
        }
    }

    private void UpdateStateVisuals(VoiceModeState state)
    {
        switch (state)
        {
            case VoiceModeState.Listening:
                VoiceIndicator.IsTimerRunning = true;
                VoiceIndicator.ShowDuration = true;
                break;

            case VoiceModeState.Processing:
            case VoiceModeState.Speaking:
            case VoiceModeState.Idle:
            default:
                VoiceIndicator.IsTimerRunning = false;
                VoiceIndicator.ShowDuration = false;
                break;
        }
    }
}
