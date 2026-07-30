using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Pia.Tests.Views;

/// <summary>
/// The process-wide WPF host every view-parse test runs on: ONE background STA thread with a RUNNING
/// dispatcher, created lazily, never shut down, plus the one and only <see cref="Application"/> this
/// process will ever have.
/// <para>
/// Thread-per-test does not work here — it dies on the SECOND test with "Initialization of
/// Wpf.Ui.Controls.Button threw an exception", because App.xaml's merged Wpf.Ui dictionaries are owned
/// by the thread that built them. The STA plumbing shape is copied from
/// <c>EmojiInlineBuilderTests.RunSta</c> but not its lifetime.
/// </para>
/// <para>
/// <c>Dispatcher.Run()</c> is a correctness requirement, not a convenience: once
/// <c>Application.Current</c> is non-null, <c>WindowManagerService</c> starts awaiting a real
/// <c>DispatcherOperation</c> (WindowManagerServiceTests exercises exactly that branch) and
/// <c>OutputService</c>'s unguarded <c>Application.Current.Dispatcher.Invoke</c> becomes a real
/// cross-thread call. On an unpumped dispatcher those never complete. xunit v3 applies no default
/// per-test timeout, so the run would HANG rather than fail.
/// </para>
/// <para>
/// <b>Every wait in here is bounded, on purpose.</b> A hanging test is worse than a failing one: it
/// blocks the whole suite instead of naming a defect. So the hand-off from the STA thread has a
/// timeout, its startup exception is captured and rethrown on the caller's thread, and marshaled work
/// has a timeout too. Every one of those paths throws with a message that says which stage failed.
/// The same reasoning is why a FAILED start still pumps: <c>Application.Current</c> is published by the
/// <see cref="Application"/> ctor, before anything that can throw, and it can never be unpublished — so
/// this thread must keep pumping even when it has nothing useful to host.
/// </para>
/// <para>
/// This file was authored on macOS, where the test host cannot execute (no
/// <c>Microsoft.WindowsDesktop.App</c> for osx-arm64). It has NEVER been run. The first Windows run is
/// what validates it.
/// </para>
/// </summary>
internal static class WpfStaHost
{
    /// <summary>
    /// How long to wait for the STA thread to hand back its dispatcher. Generous, because the App
    /// ctor loads App.xaml's merged Wpf.Ui + Pia dictionaries; finite, because an unbounded wait here
    /// would hang the suite if that load deadlocked.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for a marshaled test body. The collection is <c>DisableParallelization</c>, so
    /// nothing else competes for the CPU while it runs; a minute is orders of magnitude more than a
    /// XAML parse needs and still fails instead of blocking.
    /// </summary>
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<Dispatcher> LazyDispatcher =
        new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The STA thread's dispatcher: pumping, and alive for the life of the process. Throws — never
    /// hangs — if the host could not be created; <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
    /// caches that failure, so a second test reports the same cause instead of retrying a broken start.
    /// </summary>
    internal static Dispatcher StaDispatcher => LazyDispatcher.Value;

    private static Dispatcher Start()
    {
        var ready = new ManualResetEventSlim(false);
        Dispatcher? created = null;
        Exception? startupError = null;

        var thread = new Thread(() =>
        {
            try
            {
                created = Dispatcher.CurrentDispatcher;

                // Deterministic ambient context for anything constructed here. The dispatcher installs
                // one per operation anyway; setting it once means UiThreadViewModel's
                // base(requireUiThread: true) throw (ChatTitleChipViewModel) cannot depend on frame state.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(created));

                // At most once per process: System.Windows.Application's ctor THROWS if a second one is
                // created in the AppDomain, and Application.Current can never be nulled again.
                if (Application.Current is null)
                {
                    // App.xaml.cs's Main is the precedent. InitializeComponent() only loads App.xaml —
                    // the converters (BooleanToVisibilityConverter) and the merged Wpf.Ui + Pia
                    // dictionaries the view needs. Run() is never called, so OnStartup never runs: no
                    // Bootstrapper.InitializeAsync(), no SQLite open, no SetLanguage() (which would mutate
                    // the process-wide LocalizationSource culture and break the EN text assertion), no window.
                    var app = new Pia.App();
                    app.InitializeComponent();

                    // OnStartup is also what installs App's DispatcherUnhandledException handler, and it
                    // never runs. Without one, a single unhandled exception on this shared pumping
                    // dispatcher would kill the whole test process.
                    app.DispatcherUnhandledException += (_, e) => e.Handled = true;

                    // The default ShutdownMode.OnLastWindowClose would run App.OnExit, an async void
                    // whose first statement dereferences Bootstrapper.ServiceProvider and throws
                    // InvalidOperationException. No window is ever opened here, but pin it anyway.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
            }
            catch (Exception ex)
            {
                // Record the cause and FALL THROUGH to the pump below. Do NOT return: the
                // System.Windows.Application base ctor publishes Application.Current before
                // InitializeComponent() can throw, and Application.Current can never be nulled again.
                // Abandoning this thread would leave the whole suite with a live Application whose
                // dispatcher nobody pumps, so every unbounded Application.Current.Dispatcher call
                // outside this file — WindowManagerService's awaited InvokeAsync, OutputService's
                // blocking Invoke, the two notification surfaces — would block forever. One failing
                // test (AssistantViewParseTests, via the throw in Start below) is the outcome we want;
                // a suite that hangs with no name attached is the one thing worse than that.
                startupError = ex;
            }

            // Set the signal before pumping — including on the failure path, or the caller's bounded
            // wait would burn its whole timeout to report a cause we already hold.
            ready.Set();

            // Pump for the life of the process. Run() returns only on InvokeShutdown (never called
            // here); it can THROW if an exception escapes a queued operation, which is reachable on the
            // failure path above because App's DispatcherUnhandledException net is installed AFTER
            // InitializeComponent(). Re-entering keeps the shared dispatcher alive instead of turning
            // that into the dead-dispatcher hang this whole block exists to prevent.
            while (true)
            {
                try
                {
                    Dispatcher.Run();
                    return;
                }
                catch
                {
                    if (created is null || created.HasShutdownStarted)
                        return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Pia.Tests WPF STA host",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(StartupTimeout))
            throw new TimeoutException(
                $"The WPF STA host did not hand back its dispatcher within {StartupTimeout.TotalSeconds:0}s. " +
                "Creating the process-wide Application (App.xaml's merged Wpf.Ui + Pia dictionaries) is the " +
                "only thing that runs before the signal, so suspect that load. Failing instead of waiting " +
                "forever is deliberate.");

        if (startupError is not null)
            throw new InvalidOperationException(
                "The WPF STA host could not create the process-wide Application. Parsing a View needs " +
                "App.xaml's resources, so every view-parse test depends on this succeeding.",
                startupError);

        return created!;
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the STA thread and returns its value. WPF objects are
    /// thread-affine, so only primitives, enums, strings and collections of those may cross back.
    /// Bounded by <see cref="InvokeTimeout"/>.
    /// </summary>
    internal static T Run<T>(Func<T> func)
    {
        var dispatcher = StaDispatcher;
        if (dispatcher.CheckAccess())
            return func();

        // Wait on the operation's Task, not on DispatcherOperation.Wait(TimeSpan): the latter returns a
        // status and leaves an exception thrown by `func` sitting unobserved in the operation, which
        // would surface as a default-valued result and a baffling assertion failure. Waiting on the
        // handle (rather than Task.Wait) keeps the timeout without wrapping the real failure in an
        // AggregateException — GetResult() below rethrows it as itself, with its original stack.
        var task = dispatcher.InvokeAsync(func).Task;
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(InvokeTimeout))
            throw new TimeoutException(
                $"The WPF STA host did not finish a marshaled test body within {InvokeTimeout.TotalSeconds:0}s. " +
                "Either the body is waiting on something the dispatcher must run, or the host thread died. " +
                "Failing instead of waiting forever is deliberate.");

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drains the dispatcher queue down to <see cref="DispatcherPriority.SystemIdle"/>. Binding values
    /// do not transfer until the queue drains; without this a test asserts the property default and
    /// looks like a pass — the single most likely way for a view-parse test to be silently vacuous.
    /// Callable from either thread.
    /// <para>
    /// Deliberately NOT bounded by a timer that would set <c>frame.Continue = false</c> early: a frame
    /// that cannot drain must surface as the <see cref="TimeoutException"/> from <see cref="Run{T}"/>,
    /// not as a partially drained queue. A partial pump is the vacuous-pass failure mode this method
    /// exists to prevent, so giving up quietly would defeat it.
    /// </para>
    /// </summary>
    internal static void Pump()
    {
        // Dispatcher.PushFrame is STATIC and pumps the CALLING thread, so this has to be on the host
        // thread before the frame is pushed.
        if (!StaDispatcher.CheckAccess())
        {
            Run<object?>(() =>
            {
                Pump();
                return null;
            });
            return;
        }

        var frame = new DispatcherFrame();

        // SystemIdle is the lowest non-inactive priority, so everything already queued — DataBind,
        // Normal, Input (AssistantView's ScrollToBottom uses Input), Loaded, Render — runs before the
        // frame exits. Never use ApplicationIdle or higher for the exit. A statement-bodied lambda so
        // this binds to InvokeAsync(Action, DispatcherPriority) with no Func<bool> ambiguity.
        StaDispatcher.InvokeAsync(() => { frame.Continue = false; }, DispatcherPriority.SystemIdle);
        Dispatcher.PushFrame(frame);
    }
}
