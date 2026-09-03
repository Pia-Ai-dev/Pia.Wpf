using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

public sealed class ChatRenameIconTests
{
    /// <summary>A SymbolRegular member above U+FFFF compiles clean and renders a garbage letter, so nothing
    /// but a test catches it — and 2,863 of the 9,235 members are up there.</summary>
    [Fact]
    public void TheRenameIconIsInsideTheBasicMultilingualPlane() =>
        Assert.True((int)SymbolRegular.Rename20 <= 0xFFFF,
            $"SymbolRegular.Rename20 sits at U+{(int)SymbolRegular.Rename20:X}, outside the BMP.");
}
