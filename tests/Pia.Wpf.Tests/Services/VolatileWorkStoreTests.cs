using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class VolatileWorkStoreTests
{
    private readonly VolatileWorkStore _store = new();

    [Fact]
    public void WithNoReports_NothingIsInFlight()
    {
        Assert.False(_store.HasVolatileWork);
    }

    /// <summary>Two windows report independently, so one saying "nothing here" must not clear the other.</summary>
    [Fact]
    public void OneOwnersFalse_DoesNotClearAnothersTrue()
    {
        var assistant = new object();
        var optimize = new object();

        _store.Report(assistant, true);
        _store.Report(optimize, false);

        Assert.True(_store.HasVolatileWork);
    }

    /// <summary>Two owners that compare equal by value would collapse into one entry.</summary>
    [Fact]
    public void OwnersAreKeyedByReference()
    {
        var first = new string("owner".ToCharArray());
        var second = new string("owner".ToCharArray());

        _store.Report(first, true);
        _store.Report(second, false);

        Assert.True(_store.HasVolatileWork);
    }

    [Fact]
    public void Forget_DropsTheOwnersAnswer()
    {
        var owner = new object();
        _store.Report(owner, true);

        _store.Forget(owner);

        Assert.False(_store.HasVolatileWork);
    }

    [Fact]
    public void ChangedFires_OnlyWhenTheAggregateFlips()
    {
        var first = new object();
        var second = new object();
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        _store.Report(first, true);      // false -> true
        _store.Report(second, true);     // still true
        _store.Report(first, false);     // still true
        _store.Report(second, false);    // true -> false

        Assert.Equal(2, raised);
    }

    [Fact]
    public void ChangedFires_WhenForgettingTheLastTrue()
    {
        var owner = new object();
        _store.Report(owner, true);
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        _store.Forget(owner);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ForgettingAnUnknownOwner_RaisesNothing()
    {
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        _store.Forget(new object());

        Assert.Equal(0, raised);
    }
}
