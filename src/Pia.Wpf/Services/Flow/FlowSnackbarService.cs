using Pia.Models.Flow;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Services.Flow;

/// <summary>
/// A Pia implementation of the WPF-UI <see cref="ISnackbarService"/> that funnels every snackbar into
/// Flow instead of driving the WPF-UI slide-in (design §7 "Snackbar — capture via chokepoints"). The
/// 5-arg <c>Show</c> is the interface member every producer (directly or via the shorter extension
/// overloads) funnels through, so all ~85 call sites are captured untouched. Snackbar items are
/// session-only (Durable = false) with a null dedup key.
/// </summary>
public sealed class FlowSnackbarService : ISnackbarService, IFlowActionPublisher
{
    private static readonly TimeSpan DefaultTransient = TimeSpan.FromSeconds(4);

    private readonly IFlowService _flow;
    private SnackbarPresenter? _presenter;

    public FlowSnackbarService(IFlowService flow)
    {
        _flow = flow;
    }

    /// <summary>Unused by Flow (producers pass an explicit timeout); kept to satisfy the interface.</summary>
    public TimeSpan DefaultTimeOut { get; set; } = DefaultTransient;

    /// <summary>Stored but never driven — Flow renders its own peek. Windows still call this in their constructors.</summary>
    public void SetSnackbarPresenter(SnackbarPresenter contentPresenter) => _presenter = contentPresenter;

    public SnackbarPresenter GetSnackbarPresenter() => _presenter!;

    public void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout)
    {
        var severity = FlowSeverityMapper.FromSnackbar(appearance);
        _flow.Publish(new FlowItemDraft
        {
            Severity = severity,
            Source = FlowSource.Snackbar,
            Title = title,
            Body = message,
            DedupKey = null,
            Lifetime = LifetimeFor(severity, timeout),
            Action = null,
            RequestDurable = false,
        });
    }

    public void PublishAction(string title, string message, string actionText, Action onAction, ControlAppearance appearance, TimeSpan timeout)
    {
        // Action snackbars are always ActionRequired and carry the onAction callback as a non-serializable Invoke.
        _flow.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.Snackbar,
            Title = title,
            Body = message,
            DedupKey = null,
            Lifetime = FlowLifetime.Persistent,
            Action = new InvokeAction(onAction, actionText),
            RequestDurable = false,
        });
    }

    /// <summary>Info/Success whisper-peek then auto-expire; Warning/Error persist until resolved (design §8).</summary>
    private static FlowLifetime LifetimeFor(FlowSeverity severity, TimeSpan timeout) => severity switch
    {
        FlowSeverity.Warning or FlowSeverity.Error or FlowSeverity.ActionRequired => FlowLifetime.Persistent,
        _ => FlowLifetime.Transient(timeout > TimeSpan.Zero ? timeout : DefaultTransient),
    };
}
