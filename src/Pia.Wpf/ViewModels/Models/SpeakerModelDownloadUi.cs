using Pia.Services.Interfaces;

namespace Pia.ViewModels.Models;

/// <summary>
/// Owns the lazy-show / terminal-dismiss lifecycle of the speaker-embedding model download dialog,
/// driven entirely by the <see cref="IProgress{T}"/> reports the service emits while ensuring the
/// (optional) speaker model. The dialog is shown on the FIRST <see cref="ModelDownloadPhase.Downloading"/>
/// report (so a cached model never flashes it) and dismissed on the terminal
/// <see cref="ModelDownloadPhase.Completed"/> report (which the service emits on success, failure, and
/// cancellation alike — never a stuck dialog). The dialog's own cancel maps to a VM-owned CTS that is
/// NEVER the meeting-start token, so dismissing it cannot abort the meeting join — at worst the
/// already-degrade-to-null speaker download continues harmlessly in the background.
///
/// <para>Lives in <c>Pia.ViewModels.Models</c> (alongside the other VM-adjacent helpers) rather than as a
/// nested type of <c>MeetingAttendeeViewModel</c>: it is plumbing, not a view model, so it must not be
/// caught by the architecture rule that every <c>Pia.ViewModels</c> class inherit <c>ObservableObject</c>.
/// It touches no view-model state — every dependency arrives via its constructor.</para>
/// </summary>
internal sealed class SpeakerModelDownloadUi : IAsyncDisposable
{
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly Action<Action> _dispatchToUi;
    private readonly CancellationTokenSource _dialogCloseCts = new();
    private Task? _dialogTask;
    private bool _shown;

    public Progress<ModelDownloadProgress> Progress { get; }

    public SpeakerModelDownloadUi(
        IDialogService dialogService,
        ILocalizationService localizationService,
        Action<Action> dispatchToUi)
    {
        _dialogService = dialogService;
        _localizationService = localizationService;
        _dispatchToUi = dispatchToUi;
        // The service may report from any thread, so route the WPF dialog show/dismiss through the
        // UI dispatcher explicitly rather than relying on where this Progress was constructed.
        Progress = new Progress<ModelDownloadProgress>(OnProgress);
    }

    private void OnProgress(ModelDownloadProgress report)
    {
        // This Progress is constructed off the UI thread (StartAsync is past ConfigureAwait(false)), so
        // OnProgress runs on thread-pool threads — and the reports can even arrive concurrently/out of
        // order. Route the WHOLE body through the UI dispatcher: (1) _dialogCloseCts.Cancel() invokes the
        // dialog's Hide() callback SYNCHRONOUSLY on the calling thread and ContentDialog is a
        // DispatcherObject, so the dismiss MUST run on the UI thread; (2) serializing on the UI thread
        // makes the show-once guard race-free.
        _dispatchToUi(() =>
        {
            if (report.Phase == ModelDownloadPhase.Completed)
            {
                // Terminal: dismiss. The dialog watches _dialogCloseCts and hides on cancel.
                if (!_dialogCloseCts.IsCancellationRequested) _dialogCloseCts.Cancel();
                return;
            }

            // First real download tick → lazily show the dialog. A cached model never reaches here.
            if (_shown) return;
            _shown = true;
            // Load-bearing re-check: thread-pool reordering can deliver a late Downloading after the
            // terminal Completed already cancelled — skip the orphan show so no dialog is left open.
            if (_dialogCloseCts.IsCancellationRequested) return;
            var modelName = _localizationService["Settings_SpeakerModel_DisplayName"];
            _dialogTask = _dialogService.ShowModelDownloadDialogAsync(modelName, Progress, _dialogCloseCts.Token);
        });
    }

    public async ValueTask DisposeAsync()
    {
        // Backstop dismissal. In practice the terminal Completed report has already cancelled this CTS
        // before StartAsync returns (the speaker step is awaited pre-join and its finally always runs),
        // so the dialog is already hidden and _dialogTask completed. We only ever need to act when a
        // dialog is still pending. Cancel() fires the dialog's Hide() callback SYNCHRONOUSLY on the
        // calling thread, and we are off the UI thread here — so dispatch it (same hazard as OnProgress)
        // and AWAIT that dispatch, so the CTS is not disposed out from under the queued callback.
        var dialogTask = _dialogTask;
        if (dialogTask is not null && !_dialogCloseCts.IsCancellationRequested)
        {
            var dismissed = new TaskCompletionSource();
            _dispatchToUi(() =>
            {
                // If the time-box below already elapsed and disposed the CTS, a late-running callback
                // would otherwise throw ObjectDisposedException on the UI thread — swallow it.
                try { if (!_dialogCloseCts.IsCancellationRequested) _dialogCloseCts.Cancel(); }
                catch (ObjectDisposedException) { /* DisposeAsync timed out and already disposed the CTS */ }
                finally { dismissed.TrySetResult(); }
            });
            // Time-box the wait: once the dispatcher has begun shutting down, the queued cancel can be
            // aborted and never complete the TCS — an unbounded await would then hang app shutdown.
            await Task.WhenAny(dismissed.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }

        if (dialogTask is not null)
        {
            // Same hazard: ShowModelDownloadDialogAsync only completes once the CTS hides the dialog, so
            // if the cancel above was aborted at shutdown this would hang. Bound it the same way.
            try { await Task.WhenAny(dialogTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false); }
            catch { /* dialog already hidden */ }
        }
        _dialogCloseCts.Dispose();
    }
}
