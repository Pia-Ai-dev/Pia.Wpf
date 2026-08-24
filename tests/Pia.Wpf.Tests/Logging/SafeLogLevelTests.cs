using System.Reflection;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary>
/// The diagnostics export drops every DBUG/TRCE message body with one level test instead of a deny-list of
/// call sites. That only works while the whole Sensitive* family emits at Debug or Trace — SensitiveWarning
/// forwarding to LogWarning would put user content at WARN, where nothing would catch it.
/// </summary>
public class SafeLogLevelTests
{
    /// <summary>Reflection bypasses [Conditional], so this arm runs in Release too.</summary>
    [Fact]
    public void EverySafeLogHelperEmitsAtDebugOrTrace()
    {
        var helpers = typeof(SafeLog)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters() is [{ } first, ..] && first.ParameterType == typeof(ILogger))
            .ToArray();

        Assert.Equal(4, helpers.Length);

        foreach (var helper in helpers)
        {
            var logger = new CapturingLogger<SafeLogLevelTests>();
            helper.Invoke(null, [logger, "probe {Value}", new object?[] { 1 }]);

            var entry = Assert.Single(logger.Entries);
            Assert.True(
                entry.Level is LogLevel.Debug or LogLevel.Trace,
                $"{helper.Name} emits at {entry.Level}, so its payload would survive the export's level gate");
        }
    }

    /// <summary>
    /// The compile-time erasure is the mechanism, not the level: in Release the calls are not there at all.
    /// </summary>
    [Fact]
    public void TheHelpersAreErasedInRelease_AndPresentInDebug()
    {
        var logger = new CapturingLogger<SafeLogLevelTests>();

        logger.SensitiveTrace("t");
        logger.SensitiveDebug("d");
        logger.SensitiveInformation("i");
        logger.SensitiveWarning("w");

        Assert.Equal(SafeLog.SensitiveLoggingCompiledIn ? 4 : 0, logger.Entries.Count);
    }
}
