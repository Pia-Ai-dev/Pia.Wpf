using Pia.Services.Exceptions;

namespace Pia.Services.Plugins.Sandbox;

/// <summary>A container name must be unique among running containers; a second spawn under one breaks it and leaks ACEs.</summary>
public sealed class SandboxContainerLease : IDisposable
{
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase);

    private int _released;

    private SandboxContainerLease(string name) => Name = name;

    public string Name { get; }

    public static SandboxContainerLease Acquire(string name)
    {
        lock (Active)
        {
            if (!Active.Add(name))
                throw new SandboxStartException($"The sandbox '{name}' is already running.");
        }

        return new SandboxContainerLease(name);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        lock (Active)
            Active.Remove(Name);
    }
}
