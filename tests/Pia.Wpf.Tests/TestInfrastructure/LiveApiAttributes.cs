using System.Runtime.CompilerServices;
using Xunit;

namespace Pia.Tests.TestInfrastructure;

// Marks a test that talks to a real provider API. Explicit keeps it out of a default run, so the
// suite needs no caller-supplied filter to stay offline; run these with the runner's `-explicit on`.
public sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
        => Explicit = true;
}

public sealed class LiveApiTheoryAttribute : TheoryAttribute
{
    public LiveApiTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
        => Explicit = true;
}
