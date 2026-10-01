using System.Globalization;
using Pia.Resources.Strings;
using Xunit;

namespace Pia.Tests.Services;

public class KnowledgeBaseCardTitleTextTests
{
    private static string Title(string culture, string actionKey, string categoryKey)
    {
        var ci = CultureInfo.GetCultureInfo(culture);
        var resources = ViewStrings.ResourceManager;
        return string.Format(
            ci,
            resources.GetString("ActionCard_Title_Format", ci)!,
            resources.GetString(actionKey, ci),
            resources.GetString(categoryKey, ci));
    }

    [Theory]
    [InlineData("en", "Add to Knowledge base")]
    [InlineData("de", "Wissensdatenbank ergänzen")]
    [InlineData("fr", "Ajouter à la Base de connaissances")]
    public void TheUploadTitle_ReadsAsAPhrase(string culture, string expected)
    {
        Assert.Equal(expected, Title(culture, "ActionCard_Action_Upload", "ActionCard_Category_KnowledgeBase"));
    }

    [Theory]
    [InlineData("en", "Delete Document")]
    [InlineData("de", "Dokument löschen")]
    [InlineData("fr", "Supprimer Document")]
    public void TheDeleteTitle_NamesTheDocument_NotTheWholeKnowledgeBase(string culture, string expected)
    {
        Assert.Equal(expected, Title(culture, "ActionCard_Action_Delete", "ActionCard_Category_KbDocument"));
    }
}
