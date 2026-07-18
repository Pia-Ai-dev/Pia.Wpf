using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Covers the pure file-type gate <see cref="VsCodeLauncher.IsSupportedFile"/> — the only part of the
/// launcher that is deterministic (detection and icon extraction depend on the machine's VS Code install).
/// </summary>
public sealed class VsCodeLauncherTests
{
    [Theory]
    [InlineData("C:\\proj\\Program.cs")]
    [InlineData("C:\\proj\\app.ts")]
    [InlineData("C:\\proj\\notes.md")]
    [InlineData("C:\\proj\\build.ps1")] // deliberately supported: opens as text, never runs
    [InlineData("C:\\proj\\config.yaml")]
    [InlineData("relative/path/script.PY")] // case-insensitive extension match
    public void IsSupportedFile_TrueForCommonCodeAndTextTypes(string path)
        => Assert.True(VsCodeLauncher.IsSupportedFile(path));

    [Theory]
    [InlineData("C:\\proj\\image.png")]
    [InlineData("C:\\proj\\report.docx")]
    [InlineData("C:\\proj\\sheet.xlsx")]
    [InlineData("C:\\proj\\photo.jpeg")]
    [InlineData("C:\\proj\\noextension")]
    public void IsSupportedFile_FalseForBinaryOrExtensionlessTypes(string path)
        => Assert.False(VsCodeLauncher.IsSupportedFile(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSupportedFile_FalseForNullOrBlank(string? path)
        => Assert.False(VsCodeLauncher.IsSupportedFile(path));
}
