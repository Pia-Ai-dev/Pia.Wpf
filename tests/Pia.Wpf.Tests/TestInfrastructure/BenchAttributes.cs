using System.Runtime.CompilerServices;
using Xunit;

namespace Pia.Tests.TestInfrastructure;

// Marks a measurement harness rather than a test: it wants a real recording, a real model and
// minutes of CPU. Explicit keeps it out of the gate with no caller-side filter, exactly as
// LiveApiFact does for provider calls; run it with the runner's `-explicit on`.
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
        => Explicit = true;
}
