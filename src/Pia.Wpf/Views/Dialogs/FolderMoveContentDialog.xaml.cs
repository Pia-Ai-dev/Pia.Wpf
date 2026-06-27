using System;
using Pia.Localization;
using Pia.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class FolderMoveContentDialog : ContentDialog
{
    public FolderMoveContentDialog(ContentDialogHost dialogHost, IProgress<FolderMoveProgress> progress)
        : base(dialogHost)
    {
        InitializeComponent();

        if (progress is Progress<FolderMoveProgress> progressImpl)
        {
            progressImpl.ProgressChanged += OnProgressChanged;
        }
    }

    private void OnProgressChanged(object? sender, FolderMoveProgress e)
    {
        Dispatcher.Invoke(() => Apply(e));
    }

    private void Apply(FolderMoveProgress e)
    {
        MoveProgressBar.Value = e.PercentComplete;
        PercentText.Text = $"{e.PercentComplete}%";
        PhaseText.Text = e.Phase switch
        {
            FolderMovePhase.Copying => LocalizationSource.Instance["Dialog_FolderMove_Copying"],
            FolderMovePhase.Verifying => LocalizationSource.Instance["Dialog_FolderMove_Verifying"],
            FolderMovePhase.CleaningUp => LocalizationSource.Instance["Dialog_FolderMove_CleaningUp"],
            _ => string.Empty,
        };
    }
}
