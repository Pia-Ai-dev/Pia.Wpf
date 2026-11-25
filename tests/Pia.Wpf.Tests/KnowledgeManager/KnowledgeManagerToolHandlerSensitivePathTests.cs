using System.IO;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Paths;
using Pia.Services.KnowledgeManager;
using Pia.Shared.Knowledge;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

/// <summary>A files folder inside a directory the guard protects must not feed a knowledge base or receive a download.</summary>
[Collection("PiaPathsStatic")]
public sealed class KnowledgeManagerToolHandlerSensitivePathTests : KnowledgeManagerToolHandlerTestBase
{
    private static string NewProfile() =>
        Path.Combine(Path.GetTempPath(), $"pia-kbm-profile-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upload_FromAFilesFolderInsideAProtectedDirectory_IsRefusedAndCallsNoApi()
    {
        var profile = NewProfile();
        var root = Directory.CreateDirectory(Path.Combine(profile, "notes")).FullName;
        File.WriteAllText(Path.Combine(root, "a.md"), "secret");
        Files.ResolveToolRoot().Returns(root);
        try
        {
            using (PiaPaths.OverrideForTests(null, profile))
            {
                Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(root, "a.md"), out _));

                var (result, pending) = await CreateSut().HandleToolCallAsync(
                    Call("upload_kb_document", ("kb_id", Kb.ToString()), ("path", "a.md")), Ct);

                Assert.Null(pending);
                Assert.StartsWith("Refusing to use that path", (string)result!, StringComparison.Ordinal);
            }

            await Api.DidNotReceiveWithAnyArgs().ListKnowledgeBasesAsync(Ct);
            await Api.DidNotReceiveWithAnyArgs().UploadAsync(default, default!, Ct);
        }
        finally
        {
            TempPath.Remove(profile);
        }
    }

    [Fact]
    public async Task Download_ToAFilesFolderInsideAProtectedDirectory_WritesNothing()
    {
        var profile = NewProfile();
        var root = Directory.CreateDirectory(Path.Combine(profile, "notes")).FullName;
        Files.ResolveToolRoot().Returns(root);
        Documents(Onboarding());
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
            KbManagerCallStatus.Ok, new KbManagerDocumentContent("theirs", KbManagerLimits.Markdown)));
        try
        {
            using (PiaPaths.OverrideForTests(null, profile))
            {
                var (result, _) = await CreateSut().HandleToolCallAsync(
                    Call("download_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

                Assert.StartsWith("Refusing to use that path", (string)result!, StringComparison.Ordinal);
            }

            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            TempPath.Remove(profile);
        }
    }

    [Fact]
    public async Task Download_ThroughAJunctionIntoAProtectedDirectory_WritesNothing()
    {
        var profile = NewProfile();
        var root = Directory.CreateDirectory(Path.Combine(profile, "files")).FullName;
        var protectedDir = Directory.CreateDirectory(Path.Combine(root, "protected")).FullName;
        var link = Path.Combine(root, "exports");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{protectedDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using (var proc = System.Diagnostics.Process.Start(psi)!) proc.WaitForExit();
        Files.ResolveToolRoot().Returns(root);
        Documents(Onboarding());
        Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
            KbManagerCallStatus.Ok, new KbManagerDocumentContent("theirs", KbManagerLimits.Markdown)));
        try
        {
            Assert.True(Directory.Exists(link), "mklink /J failed");
            using (PiaPaths.OverrideForTests(null, protectedDir))
            {
                var (result, _) = await CreateSut().HandleToolCallAsync(Call("download_kb_document",
                    ("kb_id", Kb.ToString()), ("document_id", Doc.ToString()), ("path", "exports")), Ct);

                Assert.StartsWith("Refusing to use that path", (string)result!, StringComparison.Ordinal);
            }

            Assert.Empty(Directory.GetFileSystemEntries(protectedDir));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            TempPath.Remove(profile);
        }
    }

    [Fact]
    public async Task Download_IntoARunWorkspaceUnderTheRunsCarveOut_IsSaved()
    {
        var profile = NewProfile();
        try
        {
            using (PiaPaths.OverrideForTests(null, profile))
            {
                var root = Directory.CreateDirectory(Path.Combine(profile, "runs", Guid.NewGuid().ToString("N"))).FullName;
                Files.ResolveToolRoot().Returns(root);
                Documents(Onboarding());
                Api.GetContentAsync(Kb, Doc, Arg.Any<CancellationToken>()).Returns(new KbManagerResult<KbManagerDocumentContent>(
                    KbManagerCallStatus.Ok, new KbManagerDocumentContent("theirs", KbManagerLimits.Markdown)));

                var (result, _) = await CreateSut().HandleToolCallAsync(
                    Call("download_kb_document", ("kb_id", Kb.ToString()), ("document_id", Doc.ToString())), Ct);

                Assert.Equal("Onboarding.md", Json(result).GetProperty("path").GetString());
                Assert.Equal("theirs", File.ReadAllText(Path.Combine(root, "Onboarding.md")));
            }
        }
        finally
        {
            TempPath.Remove(profile);
        }
    }
}
