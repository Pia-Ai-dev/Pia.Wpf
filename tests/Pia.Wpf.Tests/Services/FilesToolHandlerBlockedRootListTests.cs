using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Paths;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The one listing fact that needs a directory inside a root the LIVE <c>SensitivePathGuard</c> blocks, which is
/// why it sits in its own class: it needs the redirected profile, and the redirect has to be serialized against
/// every other test that resolves a Pia path. It used to reach into the developer's real
/// <c>%LOCALAPPDATA%\Pia</c> instead — the guard's roots were frozen at type load, so an override could not
/// reach it — and that was the gate's last footprint on the real profile.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class FilesToolHandlerBlockedRootListTests : IClassFixture<RedirectedProfileFixture>
{
    public FilesToolHandlerBlockedRootListTests(RedirectedProfileFixture profile) => _ = profile;

    [Fact]
    public void ListRelativeFiles_NegationCannotResurfaceSensitivePathGuardBlockedPath()
    {
        // SensitivePathGuard is applied independently of the ignore matcher, so a broad "!**" negation must NOT
        // surface a guard-blocked path — and the Pia data directory is a blocked root outside the workdir
        // carve-out, redirected or not.
        var blockedRoot = Path.Combine(PiaPaths.LocalDataDirectory, "pia-guard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blockedRoot);
        try
        {
            // Non-vacuity: the guard has to be the thing that hides the file, so assert it says so first.
            // Without this the test also passes against a root the handler simply cannot read.
            Assert.True(Pia.Infrastructure.SensitivePathGuard.IsBlocked(
                Path.Combine(blockedRoot, "secret.txt"), out _));

            File.WriteAllText(Path.Combine(blockedRoot, "secret.txt"), "x");
            File.WriteAllText(Path.Combine(blockedRoot, ".piaignore"), "!**\n"); // try to re-include everything

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = blockedRoot });
            var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

            var result = handler.ListRelativeFiles(filter: null, max: 50);

            Assert.DoesNotContain("secret.txt", result); // guard wins over the negation
        }
        finally
        {
            try { Directory.Delete(blockedRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
