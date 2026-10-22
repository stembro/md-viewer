using System.IO;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace MarkdownViewer.Editing;

internal static class MarkdownHighlighting
{
    private static readonly Lazy<IHighlightingDefinition> Light = new(() => Create(false));
    private static readonly Lazy<IHighlightingDefinition> Dark = new(() => Create(true));

    internal static IHighlightingDefinition Get(bool dark) => dark ? Dark.Value : Light.Value;

    private static IHighlightingDefinition Create(bool dark)
    {
        using var reader = XmlReader.Create(new StringReader(Definition), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        string[] names = ["Heading", "Bold", "Italic", "Link", "Code", "Marker", "Muted"];
        string[] colors = dark
            ? ["#9CDFBA", "#F4D48A", "#D4B4F8", "#8FC9FF", "#F2AF91", "#A8CBB7", "#94A397"]
            : ["#215F45", "#80561A", "#73509A", "#195BA2", "#9E4228", "#457356", "#637266"];
        for (var i = 0; i < names.Length; i++)
            definition.GetNamedColor(names[i]).Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        return definition;
    }

    private const string Definition = """
        <SyntaxDefinition name="Markdown" extensions=".md;.markdown;.mdown;.mkd" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Heading" fontWeight="bold" />
          <Color name="Bold" fontWeight="bold" />
          <Color name="Italic" fontStyle="italic" />
          <Color name="Link" />
          <Color name="Code" />
          <Color name="Marker" fontWeight="bold" />
          <Color name="Muted" />
          <RuleSet>
            <Span color="Code" multiline="true">
              <Begin>^[ ]{0,3}`{3,}.*$</Begin>
              <End>^[ ]{0,3}`{3,}[ \t]*$</End>
            </Span>
            <Span color="Code" multiline="true">
              <Begin>^[ ]{0,3}~{3,}.*$</Begin>
              <End>^[ ]{0,3}~{3,}[ \t]*$</End>
            </Span>
            <Span color="Muted" multiline="true" begin="&lt;!--" end="--&gt;" />
            <Rule color="Heading">^[ ]{0,3}\#{1,6}(?=\s|$).*$</Rule>
            <Rule color="Marker">^[ ]{0,3}(?:={3,}|-{3,}|\*{3,}|_{3,})[ \t]*$</Rule>
            <Rule color="Code">(?&lt;!`)``[^`\r\n]*(?:`[^`\r\n]+)*``(?!`)|(?&lt;!`)`[^`\r\n]+`(?!`)</Rule>
            <Rule color="Link">!?\[[^\]\r\n]*\]\([^\r\n)]*\)</Rule>
            <Rule color="Bold">\*\*(?=\S)(?:[^*\r\n]|\*(?!\*))+?\*\*|__(?=\S)[^_\r\n]+?__</Rule>
            <Rule color="Italic">(?&lt;!\*)\*(?!\*|\s)[^*\r\n]+?\*(?!\*)|(?&lt;!\w)_(?!_|\s)[^_\r\n]+?_(?!\w)</Rule>
            <Rule color="Muted">~~[^\r\n]+?~~</Rule>
            <Rule color="Marker">^[ \t]*(?:[-+*]|\d+[.)])(?=[ \t])|^[ \t]*&gt;|\[[ xX]\]</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
