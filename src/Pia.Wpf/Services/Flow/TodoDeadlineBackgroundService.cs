using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services.Flow;

/// <summary>
/// Surfaces pending todos whose deadline is within 24h as persistent <c>Warning</c> Flow items
/// (design §7, §8). Reconciles on a periodic timer, on every <see cref="ITodoService.TodoChanged"/>,
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

            var existingKeys = _flowService.Snapshot
                .Where(i => i.Source == FlowSource.TodoDeadline && i.DedupKey is not null)
                .Select(i => i.DedupKey!)
                .ToHashSet();

            // Retract items whose todo is no longer due (completed / deleted / due moved / reloaded-but-stale).
            foreach (var staleKey in existingKeys.Where(k => !dueKeys.Contains(k)))
                _flowService.Retract(staleKey);

            // Publish only newly-due todos; leave already-shown items alone so they don't re-peek or re-mark-unread.
            foreach (var todo in toShow.Where(t => !existingKeys.Contains(t.Id.ToString())))
                Publish(todo);
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

    private void Publish(TodoItem todo)
    {
        _flowService.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Warning,
            Source = FlowSource.TodoDeadline,
            Title = todo.Title,
            Body = _localizationService["Flow_Todo_DueSoon"],
            DedupKey = todo.Id.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenTodoAction(todo.Id, _localizationService["Flow_Action_OpenTodo"]),
            RequestDurable = true,
        });
    }
}
