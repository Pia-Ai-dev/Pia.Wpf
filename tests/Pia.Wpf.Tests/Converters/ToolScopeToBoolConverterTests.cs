using System.Globalization;
using Pia.Converters;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Converters;

/// <summary>The Chat/Agent lever binds its <c>IsEnabled</c> here: a <c>None</c>-scope persona can never plan.</summary>
public class ToolScopeToBoolConverterTests
{
    private static bool Convert(object? value) =>
        (bool)new ToolScopeToBoolConverter().Convert(value, typeof(bool), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void None_ReturnsFalse()
    {
        Assert.False(Convert(PersonaToolScope.None));
    }

    [Theory]
    [InlineData(PersonaToolScope.Full)]
    [InlineData(PersonaToolScope.ReadOnly)]
    public void NonNone_ReturnsTrue(PersonaToolScope scope)
    {
        Assert.True(Convert(scope));
    }

    [Fact]
    public void NonScopeValue_ReturnsFalse()
    {
        // Defensive: a null/unbound ActivePersona.ToolScope must not throw and must read disabled.
        Assert.False(Convert(null));
    }

    [Fact]
    public void ConvertBack_NotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            new ToolScopeToBoolConverter().ConvertBack(true, typeof(PersonaToolScope), null!, CultureInfo.InvariantCulture));
    }
}
