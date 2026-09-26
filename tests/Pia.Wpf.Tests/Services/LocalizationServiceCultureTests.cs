namespace Pia.Tests.Services;

using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Xunit;

// The WpfApplicationStatic collection disables parallelization, so this never runs alongside
// another collection while it mutates process-wide culture state.
[Collection("WpfApplicationStatic")]
public class LocalizationServiceCultureTests
{
    [Fact]
    public void Culture_DefaultsToEnglish()
    {
        var sut = new LocalizationService(NullLogger<LocalizationService>.Instance);

        Assert.Equal("en", sut.Culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void SetLanguage_MovesCultureToTheTargetLanguage()
    {
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        var previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var previousThreadUi = Thread.CurrentThread.CurrentUICulture;
        var previousThreadCulture = Thread.CurrentThread.CurrentCulture;

        var sut = new LocalizationService(NullLogger<LocalizationService>.Instance);
        try
        {
            sut.SetLanguage(TargetLanguage.DE);

            Assert.Equal("de", sut.Culture.TwoLetterISOLanguageName);
        }
        finally
        {
            sut.SetLanguage(TargetLanguage.EN);
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
            CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
            Thread.CurrentThread.CurrentUICulture = previousThreadUi;
            Thread.CurrentThread.CurrentCulture = previousThreadCulture;
        }
    }
}
