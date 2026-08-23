using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Pia.Tests.Views;

/// <summary>
/// One never-shut-down STA thread with a RUNNING dispatcher: App.xaml's merged Wpf.Ui dictionaries are owned
/// by the thread that built them, and an unpumped dispatcher hangs the suite rather than failing a test.
/// </summary>
internal static class WpfStaHost
{
    /// <summary>Generous because the App ctor loads App.xaml's dictionaries; finite so a deadlocked load fails instead of hanging.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Finite so a marshaled body that cannot finish fails instead of blocking the suite.</summary>
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<Dispatcher> LazyDispatcher =
        new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Throws rather than hangs if the host could not be created, and the cached failure makes a second test report the same cause.</summary>
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

                // Set once, so UiThreadViewModel's requireUiThread check cannot depend on frame state.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(created));

                // At most once per process: Application's ctor throws on a second instance and Current can
                // never be nulled again.
                if (Application.Current is null)
                {
                    // Application's ctor POSTS its startup callback, and this host PUMPS - so App.OnStartup
                    // runs without anyone calling Run(), and the real one boots the DI graph, the history
                    // database and the vault indexer against the developer's live profile. Catching that
                    // operation as it is posted and aborting it is the only seam: overriding OnStartup in a
                    // subclass is not one, because LoadComponent resolves App.xaml against the component's
                    // OWN assembly and so refuses any type outside Pia.Wpf.
                    var posted = new List<DispatcherOperation>();
                    void Capture(object? sender, DispatcherHookEventArgs args) => posted.Add(args.Operation);

                    created.Hooks.OperationPosted += Capture;
                    Pia.App app;
                    try
                    {
                        app = new Pia.App();
                    }
                    finally
                    {
                        created.Hooks.OperationPosted -= Capture;
                    }

                    // Before the first pump every abort succeeds, so a false here means WPF stopped posting
                    // startup this way and the boot is live again.
                    if (posted.Count == 0 || posted.Any(o => !o.Abort()))
                    {
                        throw new InvalidOperationException(
                            $"The WPF STA host could not abort Application's queued startup callback " +
                            $"({posted.Count} operation(s) posted). Unaborted, App.OnStartup boots Pia against " +
                            "the developer's real profile from inside the test run.");
                    }

                    // Only loads App.xaml's converters and merged dictionaries.
                    app.InitializeComponent();

                    // OnStartup never runs, so without this one unhandled exception kills the test process.
                    app.DispatcherUnhandledException += (_, e) => e.Handled = true;

                    // The default OnLastWindowClose would run App.OnExit, which dereferences
                    // Bootstrapper.ServiceProvider and throws.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
            }
            catch (Exception ex)
            {
                // FALL THROUGH to the pump: Application.Current is published before InitializeComponent() can
                // throw and can never be nulled, so abandoning this thread leaves a live unpumped Application.
                startupError = ex;
            }

            // Set before pumping, failure path included, or the caller's bounded wait burns its whole timeout.
            ready.Set();

            // Run() can throw if an exception escapes a queued operation, so re-enter rather than let the
            // shared dispatcher die and turn every later Application.Current call into a hang.
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

    /// <summary>WPF objects are thread-affine, so only primitives, enums, strings and collections of those may cross back.</summary>
    internal static T Run<T>(Func<T> func)
    {
        var dispatcher = StaDispatcher;
        if (dispatcher.CheckAccess())
            return func();

        // The Task, not DispatcherOperation.Wait(TimeSpan), which would leave `func`'s exception unobserved;
        // the handle, not Task.Wait, so GetResult() below rethrows it unwrapped.
        var task = dispatcher.InvokeAsync(func).Task;
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(InvokeTimeout))
            throw new TimeoutException(
                $"The WPF STA host did not finish a marshaled test body within {InvokeTimeout.TotalSeconds:0}s. " +
                "Either the body is waiting on something the dispatcher must run, or the host thread died. " +
                "Failing instead of waiting forever is deliberate.");

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drains the queue to <see cref="DispatcherPriority.SystemIdle"/> — binding values do not transfer until
    /// it drains, so without this a test asserts the property default and looks like a pass.
    /// </summary>
    internal static void Pump()
    {
        var dispatcher = StaDispatcher;

        // A hard failure, not a silent re-marshal: from inside a Run body there is no way to drain without
        // nesting a frame.
        if (dispatcher.CheckAccess())
            throw new InvalidOperationException(
                "WpfStaHost.Pump() was called ON the host thread, i.e. from inside a WpfStaHost.Run body. " +
                "That is the nested-frame shape this host no longer supports: it pumps a message loop inside " +
                "an executing DispatcherOperation and intermittently loses the idle-priority request that " +
                "ends it, timing out an unrelated test 60 s later. Split the caller into " +
                "Run(mutate) → Pump() → Run(observe); every step still runs on the host thread.");

        // A statement-bodied lambda so this binds the Action overload, not Func<bool>.
        var drained = dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle).Task;

        // Waiting on the handle rather than Task.Wait keeps the real failure unwrapped.
        if (!((IAsyncResult)drained).AsyncWaitHandle.WaitOne(InvokeTimeout))
            throw new TimeoutException(
                $"The WPF STA host's queue did not drain to SystemIdle within {InvokeTimeout.TotalSeconds:0}s. " +
                "Suspect work that re-queues itself at a priority above SystemIdle, or a host operation that " +
                "is blocking the dispatcher. Failing instead of waiting forever is deliberate.");

        drained.GetAwaiter().GetResult();
    }
}
