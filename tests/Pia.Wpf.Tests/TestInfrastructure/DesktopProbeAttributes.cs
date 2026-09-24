using System.Runtime.CompilerServices;
using Xunit;

namespace Pia.Tests.TestInfrastructure;

// Marks a test that needs a human at an unlocked desktop with real apps open. Explicit keeps it out of a default
// run; trigger it with the runner's `-explicit only`.
public sealed class DesktopProbeFactAttribute : FactAttribute
{
    public DesktopProbeFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
        => Explicit = true;
}
