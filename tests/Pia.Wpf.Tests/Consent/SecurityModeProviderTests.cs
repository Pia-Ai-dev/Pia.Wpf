using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;
using Xunit;
using SecurityMode = Pia.Models.SecurityMode;

namespace Pia.Wpf.Tests.Consent;

public sealed class SecurityModeProviderTests
{
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public int SaveCount { get; private set; }
        public event EventHandler<AppSettings>? SettingsChanged;
        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings)
        {
            SaveCount++;
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task SetModeAsync_PersistsMode_AndRaisesProfileChanged()
    {
        var fake = new FakeSettingsService();
        fake.Settings.SecurityMode = SecurityMode.Standard;
        var sut = new SecurityModeProvider(fake, NullLogger<SecurityModeProvider>.Instance);

        SecurityProfileChangedEventArgs? observed = null;
        sut.ProfileChanged += (_, e) => observed = e;

        await sut.SetModeAsync(SecurityMode.Strict, TestContext.Current.CancellationToken);

        Assert.Equal(SecurityMode.Strict, fake.Settings.SecurityMode);
        Assert.Equal(1, fake.SaveCount);
        Assert.NotNull(observed);
        Assert.Equal(SecurityMode.Strict, observed!.NewProfile.Mode);
        Assert.Equal(SecurityMode.Strict, sut.Current.Mode);
    }

    [Fact]
    public async Task SetModeAsync_NoOp_WhenAlreadyAtMode()
    {
        var fake = new FakeSettingsService();
        fake.Settings.SecurityMode = SecurityMode.Permissive;
        var sut = new SecurityModeProvider(fake, NullLogger<SecurityModeProvider>.Instance);
        // Drain the fire-and-forget initialisation kicked off in the constructor.
        for (var i = 0; i < 20 && sut.Current.Mode != SecurityMode.Permissive; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        var raised = 0;
        sut.ProfileChanged += (_, _) => raised++;

        await sut.SetModeAsync(SecurityMode.Permissive, TestContext.Current.CancellationToken);

        Assert.Equal(0, fake.SaveCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void DefaultProfile_IsStandard()
    {
        var fake = new FakeSettingsService();
        var sut = new SecurityModeProvider(fake, NullLogger<SecurityModeProvider>.Instance);
        Assert.Equal(SecurityMode.Standard, sut.Current.Mode);
    }
}
