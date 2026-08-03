namespace Pia.Services;

/// <summary>
/// A LIVE-RESIZABLE concurrency pool (T1-1). Behaves like the <see cref="SemaphoreSlim"/> it wraps —
/// <see cref="WaitAsync(CancellationToken)"/> to be admitted, <see cref="Release"/> when done — plus
/// <see cref="Resize"/>, which changes the effective width of the pool while runs are in flight, without an app
/// restart, and plus the <see cref="Ticket"/> pair (<see cref="TakeTicket"/> /
/// <see cref="WaitAsync(Ticket, CancellationToken)"/>), which fixes the ORDER waiters queue in.
/// <para>
/// WHY the ticket exists (T1-3). Both dispatch paths in <c>HeadlessRunLauncher</c> do their slot wait as the
/// first statement INSIDE an un-awaited <c>Task.Run</c>, so without a ticket the order runs reach
/// <c>WaitAsync</c> is thread-pool scheduling, not the order they were launched in — the scheduler tick creates
/// its dispatches oldest-due-first (<c>GetDueJobsAsync</c> orders <c>NextFireAt ASC</c> and
/// <c>ExecuteOnceAsync</c> awaits each launch in turn) and that order was then lost between the launch and the
/// queue. A ticket is taken SYNCHRONOUSLY on the launching thread; the ticketed wait blocks until its
/// PREDECESSOR has been enqueued, then enqueues itself and hands the chain on.
/// </para>
/// <para>
/// What that does and does NOT claim: creation order now equals ENQUEUE order, and nothing is dropped — a
/// predecessor that is cancelled, faulted or rejected still hands the chain on, so it can never head-of-line
/// block its successors. It is NOT strict FIFO admission: which enqueued waiter a <see cref="Release"/> wakes
/// is <see cref="SemaphoreSlim"/>'s own approximately-FIFO behaviour, and that residual approximation is
/// RATIFIED here rather than papered over — removing it would mean either taking the slot on the launching
/// thread (which rebuilds the head-of-line block on the scheduler tick that T0-2 exists to remove) or
/// hand-rolling a fair queue, which is a much larger primitive than the fairness gap justifies.
/// </para>
/// <para>
/// Ordering is only as good as the order the launches ARRIVE in, which is a property of the caller: since T0-2
/// a scheduled job past its grace period is dispatched from its own tracked task once a human answers the
/// missed-run prompt, so its position in this queue is decided by when that answer comes, not by how late the
/// job is. Ticket order equals DUE order for the jobs a tick dispatches itself, which is the case this exists
/// for.
/// </para>
/// <para>
/// WHY the wrapped semaphore's <c>maxCount</c> is the HARD CAP and not the current width: a semaphore
/// constructed <c>new(2, 2)</c> throws <see cref="SemaphoreFullException"/> on the very first widening
/// <c>Release</c>, so the first 2→3 resize would kill the resizing thread. Constructing it
/// <c>new(width, hardCap)</c> costs nothing (the count is just a ceiling) and makes every widening release
/// legal by construction.
/// </para>
/// <para>
/// WHY a NARROWING resize cannot just <c>Wait</c> the permits back: the permits are held by RUNS, and a
/// narrowing must never preempt one — a run that already started keeps its permit until it finishes
/// (that is the whole difference between "lower the cap" and "cancel a run"). So narrowing records
/// <see cref="_debt"/>: permits this pool owes back to itself. Free permits are absorbed immediately
/// (a non-blocking <c>Wait(0)</c> sweep); the rest are absorbed by the next <see cref="Release"/> calls,
/// which decrement the debt INSTEAD of handing the permit back to the semaphore.
/// </para>
/// <para>
/// WHY it can never over-release (the failure that would silently raise the real cap above the configured
/// one, forever): the class maintains the invariant <c>CurrentCount + held == _width + _debt</c>, and
/// <c>_width + _debt</c> never exceeds <see cref="HardCap"/> — a widening cancels debt BEFORE it releases
/// anything, so a lower-then-raise pair nets to zero rather than to a released permit. <c>Release</c>
/// therefore cannot exceed <c>maxCount</c>, and the symmetric error (losing a permit permanently, which is
/// unrecoverable and silent) cannot happen either, because absorbing and cancelling are the same counter.
/// </para>
/// <para>
/// Every mutation of the (<see cref="_width"/>, <see cref="_debt"/>) pair is under <see cref="_lock"/>, and
/// the lock is NEVER held across a semaphore wait — only across integer arithmetic and, since T1-3, the one
/// task-reference swap that appends a ticket to the chain (<see cref="TakeTicket"/>). <see cref="Resize"/> is
/// synchronous, allocation-free and early-returns on an unchanged width, because it is called on the hot
/// dispatch path (every launch, including every child of a fan-out) as well as from the settings-changed
/// event, which fires on every settings save.
/// </para>
/// </summary>
public sealed class RunSlotPool
{
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new();

    /// <summary>The configured width — how many permits this pool WANTS to have outstanding.</summary>
    private int _width;

    /// <summary>
    /// Permits a narrowing resize has taken away on paper but not yet reclaimed, because they were held by
    /// in-flight runs at the time. Always <c>&gt;= 0</c>; drained by <see cref="Release"/> and by a widening
    /// <see cref="Resize"/>, whichever comes first.
    /// </summary>
    private int _debt;

    /// <summary>
    /// The <see cref="Ticket.Enqueued"/> signal of the most recently issued ticket — i.e. the tail of the
    /// admission chain, which the next ticket takes as its predecessor. Starts completed so the very first
    /// ticket waits on nothing. Guarded by <see cref="_lock"/>.
    /// </summary>
    private Task _lastEnqueued = Task.CompletedTask;

    /// <param name="width">Initial width, clamped into <c>[1, <paramref name="hardCap"/>]</c>.</param>
    /// <param name="hardCap">The most this pool may EVER be resized to. Also the wrapped semaphore's
    /// <c>maxCount</c>, so it is enforced by the type and not only by <see cref="Resize"/>'s clamp — which is
    /// why the child pool can be constructed with a hard cap equal to its width and then simply cannot be
    /// widened by a stray call.</param>
    public RunSlotPool(int width, int hardCap)
    {
        if (hardCap < 1) throw new ArgumentOutOfRangeException(nameof(hardCap), hardCap, "A pool needs at least one slot.");
        HardCap = hardCap;
        _width = Math.Clamp(width, 1, hardCap);
        _semaphore = new SemaphoreSlim(_width, hardCap);
    }

    /// <summary>The ceiling <see cref="Resize"/> clamps to; fixed for the lifetime of the pool.</summary>
    public int HardCap { get; }

    /// <summary>The configured width. Diagnostic: the effective width is only observable by who gets admitted.</summary>
    public int Width
    {
        get { lock (_lock) return _width; }
    }

    /// <summary>Queue for a slot. Same contract as <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>.</summary>
    /// <remarks>
    /// UNORDERED: the caller takes whatever position in the queue the thread it happens to run on gets it. A
    /// caller that queues from inside a detached <c>Task.Run</c> — i.e. every dispatch — wants
    /// <see cref="WaitAsync(Ticket, CancellationToken)"/> instead, and since T1-3 this overload has NO production
    /// caller at all (only the pool's own tests). That is deliberate and worth keeping true: on a pool whose
    /// chain is live, mixing the two is not a graceful degrade but a HANG — a dispatch that switched back to this
    /// overload would leave its ticket unsignalled and stall every later ticket of the pool (measured: doing
    /// exactly that hangs the resume path's fact in HeadlessRunLauncherTests).
    /// </remarks>
    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    /// <summary>
    /// A place in this pool's admission chain, taken on the caller's own thread so that the ORDER dispatches
    /// were created in survives being handed to the thread pool. Opaque: everything about it is internal, so a
    /// ticket can only ever be handed back to <see cref="WaitAsync(Ticket, CancellationToken)"/>.
    /// </summary>
    public sealed class Ticket
    {
        /// <summary>
        /// Completed once this ticket's wait has been ENQUEUED (or has given up). <c>RunContinuationsAsynchronously</c>
        /// so the successor's continuation never runs on the predecessor's stack — the signal is raised while the
        /// predecessor is between its semaphore call and its own await, and inlining a successor there would let
        /// the whole chain unwind on one thread inside one <c>WaitAsync</c> frame.
        /// </summary>
        private readonly TaskCompletionSource _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Ticket(RunSlotPool owner, Task predecessorEnqueued)
        {
            Owner = owner;
            PredecessorEnqueued = predecessorEnqueued;
        }

        /// <summary>The issuing pool. Checked on use: tickets are PER-POOL, and the parent and child pools are
        /// deliberately separate numbers, so a ticket from one must never order a wait on the other.</summary>
        internal RunSlotPool Owner { get; }

        internal Task PredecessorEnqueued { get; }

        internal Task Enqueued => _enqueued.Task;

        /// <summary>Idempotent — every path through the ticketed wait calls it, at most one of them first.</summary>
        internal void MarkEnqueued() => _enqueued.TrySetResult();
    }

    /// <summary>
    /// Claim the next place in the admission chain. Synchronous and allocation-cheap, because the whole point is
    /// that it runs on the thread that DECIDED the order (the scheduler tick's awaited launch call), not on the
    /// thread that later performs the wait.
    /// </summary>
    /// <remarks>
    /// CALLER CONTRACT, and the one way this can go wrong: a ticket that is taken and never handed to
    /// <see cref="WaitAsync(Ticket, CancellationToken)"/> stalls every LATER ticket of this pool forever —
    /// nothing else signals its <see cref="Ticket.Enqueued"/>. So take it in the statement immediately before
    /// the <c>Task.Run</c> that will use it, with nothing throwable in between, and make the ticketed wait the
    /// first statement inside that body's <c>try</c>. Deliberately NOT defended with a timeout: a bounded
    /// predecessor wait would trade a deterministic ordering guarantee for a timing-dependent one, in
    /// production, to cover a case the placement rule already excludes.
    /// </remarks>
    public Ticket TakeTicket()
    {
        lock (_lock)
        {
            var ticket = new Ticket(this, _lastEnqueued);
            _lastEnqueued = ticket.Enqueued;
            return ticket;
        }
    }

    /// <summary>
    /// Queue for a slot IN TICKET ORDER: wait until the predecessor has been enqueued, enqueue, then release the
    /// successor. Same completion/cancellation contract as <see cref="WaitAsync(CancellationToken)"/> otherwise —
    /// a normal return means a permit is held and must be given back with <see cref="Release"/>.
    /// </summary>
    /// <remarks>
    /// The successor is released when this wait is ENQUEUED, never when it is ADMITTED. MEASURED, because the
    /// obvious claim here is wrong and was written down as fact once: signalling on admission does NOT serialize
    /// the pool (both waiters of a 2-wide pool still run at once — a mutation that moves the signal after the
    /// await passes every ordering fact in RunSlotPoolTests). What it actually costs is that the chain hop then
    /// happens on the ADMISSION path, so a waiter that sits in the queue for the length of somebody's run keeps
    /// its successors out of the semaphore's queue for that whole time — the queue would only ever hold about
    /// <see cref="Width"/> waiters, the ordering would live entirely in this chain rather than in the queue the
    /// chain exists to feed, and the <c>finally</c> below would become load-bearing on every path instead of
    /// being pure defence. Enqueueing first keeps the hand-off latency a few microseconds and independent of both
    /// run duration and queue depth.
    /// <para>
    /// The signal is in a <c>finally</c>, so cancellation and faults hand the chain on too: a run cancelled at
    /// shutdown, or one whose token was already cancelled when it got here, must not strand the runs behind it.
    /// </para>
    /// </remarks>
    public async Task WaitAsync(Ticket ticket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ReferenceEquals(ticket.Owner, this))
        {
            // Hand the chain on even while rejecting it: the ticket belongs to the OTHER pool's chain, and a
            // programming error here must not also wedge that pool for the lifetime of the process.
            ticket.MarkEnqueued();
            throw new ArgumentException("A run-slot ticket may only be used with the pool that issued it.", nameof(ticket));
        }

        try
        {
            await ticket.PredecessorEnqueued.WaitAsync(ct).ConfigureAwait(false);
            // Enqueue FIRST, signal SECOND, await THIRD. SemaphoreSlim.WaitAsync appends to its internal queue
            // synchronously before it returns the task, so this ordering is what makes "predecessor enqueued
            // before successor enqueued" a fact rather than a hope.
            var wait = _semaphore.WaitAsync(ct);
            ticket.MarkEnqueued();
            await wait.ConfigureAwait(false);
        }
        finally
        {
            ticket.MarkEnqueued();
        }
    }

    /// <summary>
    /// Give a slot back. Consumes outstanding <see cref="_debt"/> first — that is how a narrowing resize
    /// finally lands on a pool whose permits were all in use when it was applied.
    /// </summary>
    public void Release()
    {
        lock (_lock)
        {
            if (_debt > 0) { _debt--; return; }
        }

        _semaphore.Release();
    }

    /// <summary>
    /// Change the effective width. Widening admits queued waiters IMMEDIATELY (that is the point: a run
    /// sitting in the queue when the user raises the cap must start, not wait for an unrelated run to end);
    /// narrowing never preempts an in-flight run and takes effect as those runs finish.
    /// </summary>
    /// <param name="width">Requested width, clamped into <c>[1, <see cref="HardCap"/>]</c>. Clamped against
    /// the INSTANCE's cap, deliberately, not against a global constant — that is what makes a pool
    /// constructed with a smaller hard cap actually un-widenable.</param>
    public void Resize(int width)
    {
        var toRelease = 0;
        var narrowed = false;

        lock (_lock)
        {
            var target = Math.Clamp(width, 1, HardCap);
            if (target == _width) return; // hot path: every launch calls this with the unchanged width

            var delta = target - _width;
            _width = target;

            if (delta > 0)
            {
                // Cancel debt BEFORE releasing. A lower-then-raise pair must net to zero; releasing the full
                // delta here would hand out permits the pool never took back, i.e. permanently raise the real
                // cap above the configured one.
                var cancelled = Math.Min(_debt, delta);
                _debt -= cancelled;
                toRelease = delta - cancelled;
            }
            else
            {
                _debt += -delta;
                narrowed = true;
            }
        }

        if (toRelease > 0) _semaphore.Release(toRelease);
        else if (narrowed) AbsorbFreePermits();
    }

    /// <summary>
    /// Take back whatever is free RIGHT NOW, so a narrowing on an idle pool takes effect at once instead of
    /// waiting for a run that may never come.
    /// </summary>
    private void AbsorbFreePermits()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_debt == 0) return;
            }

            // Non-blocking, so this never waits on a run. Failing here is the NORMAL case (the permits are in
            // use) and it is not a partial-drain bug: whatever debt is left is absorbed by the next
            // Release() calls, one per call, which is the same total.
            if (!_semaphore.Wait(0)) return;

            var absorbed = false;
            lock (_lock)
            {
                if (_debt > 0) { _debt--; absorbed = true; }
            }

            // A widening resize raced in and cancelled the debt while this loop held a permit. Hand it back —
            // dropping it here is the silent, unrecoverable direction (a permit lost for the app's lifetime).
            if (!absorbed) { _semaphore.Release(); return; }
        }
    }
}
