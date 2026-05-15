using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ColorCode.Common;

namespace Pia.Controls.Markdown;

internal static class CodeBlockPalette
{
    private static readonly Dictionary<string, string> _scopeToBrushKey = new(System.StringComparer.OrdinalIgnoreCase)
    {
        [ScopeName.Keyword] = "CodeBlockKeywordBrush",
        [ScopeName.ControlKeyword] = "CodeBlockControlKeywordBrush",
        [ScopeName.PseudoKeyword] = "CodeBlockKeywordBrush",
        [ScopeName.PreprocessorKeyword] = "CodeBlockPreprocessorBrush",
        [ScopeName.String] = "CodeBlockStringBrush",
        [ScopeName.StringCSharpVerbatim] = "CodeBlockStringBrush",
        [ScopeName.StringEscape] = "CodeBlockStringBrush",
        [ScopeName.JsonString] = "CodeBlockStringBrush",
        [ScopeName.Comment] = "CodeBlockCommentBrush",
        [ScopeName.XmlComment] = "CodeBlockCommentBrush",
        [ScopeName.XmlDocComment] = "CodeBlockCommentBrush",
        [ScopeName.HtmlComment] = "CodeBlockCommentBrush",
        [ScopeName.Number] = "CodeBlockNumberBrush",
        [ScopeName.JsonNumber] = "CodeBlockNumberBrush",
        [ScopeName.JsonConst] = "CodeBlockNumberBrush",
        [ScopeName.Type] = "CodeBlockTypeBrush",
        [ScopeName.TypeVariable] = "CodeBlockTypeBrush",
        [ScopeName.ClassName] = "CodeBlockTypeBrush",
        [ScopeName.Constructor] = "CodeBlockTypeBrush",
        [ScopeName.NameSpace] = "CodeBlockTypeBrush",
        [ScopeName.Predefined] = "CodeBlockTypeBrush",
        [ScopeName.Operator] = "CodeBlockOperatorBrush",
        [ScopeName.Delimiter] = "CodeBlockPunctuationBrush",
        [ScopeName.Brackets] = "CodeBlockPunctuationBrush",
        [ScopeName.Continuation] = "CodeBlockPunctuationBrush",
        [ScopeName.XmlDelimiter] = "CodeBlockPunctuationBrush",
        [ScopeName.XmlName] = "CodeBlockKeywordBrush",
        [ScopeName.XmlAttribute] = "CodeBlockNameBrush",
        [ScopeName.XmlAttributeQuotes] = "CodeBlockStringBrush",
        [ScopeName.XmlAttributeValue] = "CodeBlockStringBrush",
        [ScopeName.XmlCDataSection] = "CodeBlockCommentBrush",
        [ScopeName.HtmlTagDelimiter] = "CodeBlockPunctuationBrush",
        [ScopeName.HtmlElementName] = "CodeBlockKeywordBrush",
        [ScopeName.HtmlAttributeName] = "CodeBlockNameBrush",
        [ScopeName.HtmlAttributeValue] = "CodeBlockStringBrush",
        [ScopeName.HtmlEntity] = "CodeBlockNumberBrush",
        [ScopeName.HtmlOperator] = "CodeBlockOperatorBrush",
        [ScopeName.CssSelector] = "CodeBlockKeywordBrush",
        [ScopeName.CssPropertyName] = "CodeBlockNameBrush",
        [ScopeName.CssPropertyValue] = "CodeBlockStringBrush",
        [ScopeName.JsonKey] = "CodeBlockNameBrush",
        [ScopeName.PowerShellAttribute] = "CodeBlockTypeBrush",
        [ScopeName.PowerShellCommand] = "CodeBlockKeywordBrush",
        [ScopeName.PowerShellOperator] = "CodeBlockOperatorBrush",
        [ScopeName.PowerShellParameter] = "CodeBlockNameBrush",
        [ScopeName.PowerShellType] = "CodeBlockTypeBrush",
        [ScopeName.PowerShellVariable] = "CodeBlockNameBrush",
        [ScopeName.SqlSystemFunction] = "CodeBlockTypeBrush",
        [ScopeName.MarkdownHeader] = "CodeBlockKeywordBrush",
        [ScopeName.MarkdownCode] = "CodeBlockStringBrush",
        [ScopeName.MarkdownListItem] = "CodeBlockNumberBrush",
        [ScopeName.MarkdownEmph] = "CodeBlockTypeBrush",
        [ScopeName.MarkdownBold] = "CodeBlockKeywordBrush",
        [ScopeName.BuiltinFunction] = "CodeBlockTypeBrush",
        [ScopeName.BuiltinValue] = "CodeBlockKeywordBrush",
        [ScopeName.Attribute] = "CodeBlockTypeBrush",
        [ScopeName.SpecialCharacter] = "CodeBlockStringBrush",
        [ScopeName.Intrinsic] = "CodeBlockTypeBrush",
        [ScopeName.LanguagePrefix] = "CodeBlockPreprocessorBrush",
    };

    public static Brush ResolveBrush(string scopeName)
    {
        if (!string.IsNullOrEmpty(scopeName) &&
            _scopeToBrushKey.TryGetValue(scopeName, out var key) &&
            Application.Current?.TryFindResource(key) is Brush themed)
        {
            return themed;
        }

        return Application.Current?.TryFindResource("CodeBlockPlainBrush") as Brush
            ?? Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as Brush
            ?? Brushes.Black;
    }
}
