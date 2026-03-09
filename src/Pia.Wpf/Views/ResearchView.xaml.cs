using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

public partial class ResearchView : UserControl
{
    private ResearchViewModel? ViewModel => DataContext as ResearchViewModel;

    public ResearchView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            if (ViewModel.CurrentSession is not null)
            {
                SubscribeToSession(ViewModel.CurrentSession);
            }
        }

        QueryTextBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            if (ViewModel.CurrentSession is not null)
            {
                UnsubscribeFromSession(ViewModel.CurrentSession);
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResearchViewModel.CurrentSession))
        {
            if (ViewModel?.CurrentSession is not null)
            {
                SubscribeToSession(ViewModel.CurrentSession);
            }
        }
    }

    private void SubscribeToSession(ResearchSession session)
    {
        session.Steps.CollectionChanged += OnStepsCollectionChanged;
        foreach (var step in session.Steps)
        {
            step.PropertyChanged += OnStepPropertyChanged;
        }
    }

    private void UnsubscribeFromSession(ResearchSession session)
    {
        session.Steps.CollectionChanged -= OnStepsCollectionChanged;
        foreach (var step in session.Steps)
        {
            step.PropertyChanged -= OnStepPropertyChanged;
        }
    }

    private void OnStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (ResearchStep step in e.NewItems)
            {
                step.PropertyChanged += OnStepPropertyChanged;
            }
            Dispatcher.BeginInvoke(ScrollToBottom, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResearchStep.Content))
        {
            Dispatcher.BeginInvoke(ScrollToBottom, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ScrollToBottom()
    {
        StepsScrollViewer.ScrollToEnd();
    }

    private void QueryTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (ViewModel?.StartResearchCommand.CanExecute(null) == true)
            {
                ViewModel.StartResearchCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel?.CancelResearchCommand.CanExecute(null) == true)
            {
                ViewModel.CancelResearchCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
