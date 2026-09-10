using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Pia.Models;

public class ManagedWindow : IDisposable
{
    public Guid Id { get; }
    public WindowMode Mode { get; }
    public MainWindow Window { get; }
    public IServiceScope Scope { get; }

    /// <summary>The state to come back to. Minimized is transient — the tray hide and the show path both
    /// undo it — and WPF keeps no record of what preceded it, so a maximized window needs this to survive.</summary>
    public WindowState RestoreState { get; set; } = WindowState.Normal;

    public ManagedWindow(WindowMode mode, MainWindow window, IServiceScope scope)
    {
        Id = Guid.NewGuid();
        Mode = mode;
        Window = window;
        Scope = scope;
    }

    public void Dispose()
    {
        Scope.Dispose();
    }
}
