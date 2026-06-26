namespace Pia.Models.Flow;

/// <summary>
/// In-session behaviour of a Flow item. Transient items auto-expire after <see cref="Duration"/>;
/// persistent items stay until dismissed or auto-retracted. Independent of durability
/// (whether the item survives a restart — see <see cref="FlowItem.Durable"/>).
/// </summary>
public readonly struct FlowLifetime : IEquatable<FlowLifetime>
{
    private FlowLifetime(bool isPersistent, TimeSpan? duration)
    {
        IsPersistent = isPersistent;
        Duration = duration;
    }

    /// <summary>True for a persistent item; false for a transient one.</summary>
    public bool IsPersistent { get; }

    /// <summary>For a transient item, how long it lives before auto-expiring. Null when persistent.</summary>
    public TimeSpan? Duration { get; }

    public static FlowLifetime Persistent { get; } = new(true, null);

    public static FlowLifetime Transient(TimeSpan duration) => new(false, duration);

    public bool Equals(FlowLifetime other) => IsPersistent == other.IsPersistent && Duration == other.Duration;

    public override bool Equals(object? obj) => obj is FlowLifetime other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(IsPersistent, Duration);

    public override string ToString() => IsPersistent ? "Persistent" : $"Transient({Duration})";
}
