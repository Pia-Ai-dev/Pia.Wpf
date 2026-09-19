using Microsoft.Extensions.Logging;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary>
/// String-in/string-out over injected roots  like LogRedactorTests, nothing here reads the machine it runs on,
/// so a fixture account name is enough to prove no real one is needed.
/// </summary>
public class PathTokenisingLoggerProviderTests
{
    private static readonly IReadOnlyList<ProfileRootToken> Roots = PathTokenisingLoggerProvider.Ordered(
    [
        new(@"C:\Users\lovelace\AppData\Roaming", "%APPDATA%"),
        new(@"C:\Users\lovelace\AppData\Local", "%LOCALAPPDATA%"),
        new(@"C:\Users\lovelace", "%USERPROFILE%"),
    ]);

    private static string Run(string message) => PathTokenisingLoggerProvider.Tokenise(message, Roots);

    // The real Bootstrapper line: LogInformation, not #if DEBUG, so it reaches every release log  and it is the
    // single commonest way an account name gets into one.
    [Fact]
    public void TheProfileRootLine_LosesTheAccountName()
    {
        var output = Run(
            @"Data directories: Roaming=C:\Users\lovelace\AppData\Roaming\Pia, "
            + @"Local=C:\Users\lovelace\AppData\Local\Pia, Overridden=False");

        Assert.Equal(
            @"Data directories: Roaming=%APPDATA%\Pia, Local=%LOCALAPPDATA%\Pia, Overridden=False", output);
    }

    [Theory]
    [InlineData(@"C:\Users\lovelace\AppData\Local\Pia\Models\x.onnx", @"%LOCALAPPDATA%\Pia\Models\x.onnx")]
    [InlineData(@"C:\Users\lovelace\AppData\Roaming\Pia\settings.json", @"%APPDATA%\Pia\settings.json")]
    [InlineData(@"C:\Users\lovelace\Documents\Pia Assistant\Vault", @"%USERPROFILE%\Documents\Pia Assistant\Vault")]
    [InlineData(@"C:\Users\lovelace\AppData\Local\Microsoft\WinGet\uv.exe", @"%LOCALAPPDATA%\Microsoft\WinGet\uv.exe")]
    public void EveryProfileRoot_BecomesItsEnvironmentVariable(string path, string expected) =>
        Assert.Equal(expected, Run(path));

    /// <summary>Without it every AppData path would degrade to the user-profile token.</summary>
    [Fact]
    public void TheLongestRootWins() =>
        Assert.Equal(@"%LOCALAPPDATA%\Pia", Run(@"C:\Users\lovelace\AppData\Local\Pia"));

    [Fact]
    public void AForwardSlashPath_IsTokenisedToo() =>
        Assert.Equal("%USERPROFILE%/Downloads/a.png", Run("C:/Users/lovelace/Downloads/a.png"));

    /// <summary>A tool argument reaches the log JSON-serialised, with every separator doubled.</summary>
    [Fact]
    public void AJsonEscapedRoot_IsTokenisedToo() =>
        Assert.Equal(
            @"{""path"":""%APPDATA%\\Pia""}",
            Run(@"{""path"":""C:\\Users\\lovelace\\AppData\\Roaming\\Pia""}"));

    [Fact]
    public void TheMatchIgnoresCase() =>
        Assert.Equal(@"%APPDATA%\Pia", Run(@"c:\users\LOVELACE\appdata\roaming\Pia"));

    [Fact]
    public void APathOutsideEveryRoot_IsLeftAlone() =>
        Assert.Equal(@"D:\Shared\build.log", Run(@"D:\Shared\build.log"));

    [Fact]
    public void NoRoots_ChangesNothing() =>
        Assert.Equal(
            @"C:\Users\lovelace\x", PathTokenisingLoggerProvider.Tokenise(@"C:\Users\lovelace\x", []));

    /// <summary>A blank root would match at every position and splice its token through the whole line.</summary>
    [Fact]
    public void ABlankRoot_IsDropped() =>
        Assert.Empty(PathTokenisingLoggerProvider.Ordered([new("  ", "%NOPE%")]));

    /// <summary>A sibling account whose name merely starts with this one must survive intact.</summary>
    [Fact]
    public void ARootThatPrefixesAnotherAccount_IsNotMatched() =>
        Assert.Equal(@"C:\Users\lovelaceXL\notes.md", Run(@"C:\Users\lovelaceXL\notes.md"));

    [Fact]
    public void ARootEndingAQuotedValue_IsStillTokenised() =>
        Assert.Equal(@"path=""%APPDATA%""", Run(@"path=""C:\Users\lovelace\AppData\Roaming"""));

}
