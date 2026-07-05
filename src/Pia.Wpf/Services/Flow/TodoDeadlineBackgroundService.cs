using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services.Flow;

/// <summary>
/// Surfaces pending todos whose deadline is within 24h as persistent Flow items (design §7, §8):
/// <c>Warning</c> while still upcoming, escalated to <c>Error</c> once the due date has passed
/// (overdue). Reconciles on a periodic timer, on every <see cref="ITodoService.TodoChanged"/>,
/// and on an immediate first tick at startup — which re-validates durable todo items reloaded from a
/// previous session (retracting those no longer due). Todos already covered by a linked reminder are
/// suppressed to avoid surfacing the same deadline twice (design §9).
/// </summary>
public sealed class TodoDeadlineBackgroundService : BackgroundService
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly ITodoService _todoService;
    private readonly IFlowService _flowService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<TodoDeadlineBackgroundService> _logger;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private CancellationToken _stoppingToken;

    public TodoDeadlineBackgroundService(
        ITodoService todoService,
        IFlowService flowService,
        ILocalizationService localizationService,
        ILogger<TodoDeadlineBackgroundService> logger)
    {
        _todoService = todoService;
        _flowService = flowService;
        _localizationService = localizationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TodoDeadlineBackgroundService started");
        _stoppingToken = stoppingToken;
        _todoService.TodoChanged += OnTodoChanged;

        try
        {
            await ReconcileAsync(); // immediate tick re-validates reloaded durable items
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ReconcileAsync();
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _todoService.TodoChanged -= OnTodoChanged;
        }
    }

    // Non-async-void event handler (architecture rule): fire-and-forget a Task-returning helper.
    private void OnTodoChanged(object? sender, EventArgs e) => _ = ReconcileFromChangeAsync();

    private async Task ReconcileFromChangeAsync()
    {
        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Todo-deadline reconcile (on change) failed");
        }
    }

    private async Task ReconcileAsync()
    {
        if (_stoppingToken.IsCancellationRequested)
            return;

        await _reconcileLock.WaitAsync();
        try
        {
            var due = await _todoService.GetDueWithinAsync(Window);
            // Suppress todos a linked reminder already covers (design §9).
            var toShow = due.Where(t => t.LinkedReminderId is null).ToList();
            var dueKeys = toShow.Select(t => t.Id.ToString()).ToHashSet();

            var existing = _flowService.Snapshot
                .Where(i => i.Source == FlowSource.TodoDeadline && i.DedupKey is not null)
                .ToDictionary(i => i.DedupKey!, i => i);

            // Retract items whose todo is no longer due (completed / deleted / due moved / reloaded-but-stale).
            foreach (var staleKey in existing.Keys.Where(k => !dueKeys.Contains(k)))
                _flowService.Retract(staleKey);

            // Publish newly-due todos; re-publish an already-shown one only when its severity or body
            // changed (a passing deadline escalates due-soon → overdue, and the overdue day count ticks
            // up at each midnight), so the card updates without every reconcile gratuitously re-peeking.
            foreach (var todo in toShow)
            {
                var (severity, body) = Describe(todo);
                if (existing.TryGetValue(todo.Id.ToString(), out var current)
                    && current.Severity == severity && current.Body == body)
                    continue;
                Publish(todo, severity, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Todo-deadline reconcile failed");
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    /// <summary>
    /// Severity + body for a due todo. Overdue (due date before today, local — matching the todo board)
    /// escalates to <c>Error</c> and reports how many days overdue, counting up to 9 then "9+"; still
    /// upcoming stays <c>Warning</c> with the generic due-soon line.
    /// </summary>
    private (FlowSeverity Severity, string Body) Describe(TodoItem todo)
    {
        if (todo.DueDate is { } due && due.Date < DateTime.Today)
        {
            var days = (DateTime.Today - due.Date).Days;
            var daysText = days > 9 ? "9+" : days.ToString();
            return (FlowSeverity.Error, _localizationService.Format("Flow_Todo_OverdueDays", daysText));
        }

        return (FlowSeverity.Warning, _localizationService["Flow_Todo_DueSoon"]);
    }

    private void Publish(TodoItem todo, FlowSeverity severity, string body)
    {
        _flowService.Publish(new FlowItemDraft
        {
            Severity = severity,
            Source = FlowSource.TodoDeadline,
            Title = todo.Title,
            Body = body,
            DedupKey = todo.Id.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenTodoAction(todo.Id, _localizationService["Flow_Action_OpenTodo"]),
            RequestDurable = true,
        });
    }
}
