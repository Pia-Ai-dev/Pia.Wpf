using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Tests.TestInfrastructure;

internal static class SettingsStubs
{
    /// <summary>A settings service that hands out a real <see cref="AppSettings"/>, as production always does.</summary>
    public static ISettingsService Returning(AppSettings? settings = null)
    {
        var service = Substitute.For<ISettingsService>();
        service.GetSettingsAsync().Returns(settings ?? new AppSettings());
        return service;
    }
}
