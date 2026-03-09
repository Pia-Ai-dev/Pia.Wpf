using Microsoft.Extensions.DependencyInjection;

namespace Pia.Models;

public class ManagedWindow : IDisposable
{
    public Guid Id { get; }
    public WindowMode Mode { get; }
    public MainWindow Window { get; }
    public IServiceScope Scope { get; }

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
