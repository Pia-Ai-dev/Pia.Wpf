using System.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pia.Tests.TestInfrastructure;

public static class PdfDocuments
{
    /// <summary>One page per entry; a null entry is a page with no text on it, i.e. a scan.</summary>
    public static void Create(string path, params string?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var text in pages)
        {
            var page = builder.AddPage(595, 842);
            if (text is not null)
                page.AddText(text, 12, new PdfPoint(50, 780), font);
        }

        File.WriteAllBytes(path, builder.Build());
    }
}
