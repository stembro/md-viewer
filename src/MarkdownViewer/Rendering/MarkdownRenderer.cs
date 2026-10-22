using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MarkdownViewer.Rendering;

public sealed record Heading(string Id, string Text, int Level);

public sealed record RenderedDocument(string Html, IReadOnlyList<Heading> Headings, int WordCount)
{
    public string BodyHtml { get; init; } = "";
}

/// <summary>Produces a self-contained, script-free reading surface for a local Markdown document.</summary>
public static partial class MarkdownRenderer
{
    // .local triggers mDNS resolution delays in WebView2, even for intercepted requests.
    public const string LocalHost = "markdown.invalid";
    public const string LocalOrigin = "https://" + LocalHost + "/";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .DisableHtml()
        .Build();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[’'-][\p{L}\p{N}]+)*")]
    private static partial Regex Words();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex Scheme();

    internal static string SetPageTheme(string html, bool darkMode) => html.Replace(
        $"<html lang=\"en\" data-theme=\"{(darkMode ? "light" : "dark")}\">",
        $"<html lang=\"en\" data-theme=\"{(darkMode ? "dark" : "light")}\">", StringComparison.Ordinal);

    public static RenderedDocument Render(string markdown, bool darkMode = false)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        // The outline and rendered headings share this tree, including Markdig's duplicate-ID handling.
        var document = Markdown.Parse(markdown, Pipeline);
        var headings = document.Descendants<HeadingBlock>()
            .Select(heading => new Heading(
                heading.GetAttributes().Id ?? string.Empty,
                heading.Inline is null ? string.Empty : PlainText(heading.Inline).Trim(),
                heading.Level))
            .ToList()
            .AsReadOnly();

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer) { LinkRewriter = SafeUrl };
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        var body = writer.ToString();
        var html = $$"""
            <!doctype html>
            <html lang="en" data-theme="{{(darkMode ? "dark" : "light")}}">
            <head>
              <meta charset="utf-8">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'none'; img-src https://{{LocalHost}} data:; style-src 'unsafe-inline'; base-uri https://{{LocalHost}}; form-action 'none'; object-src 'none'">
              <meta name="referrer" content="no-referrer">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <base href="{{LocalOrigin}}">
              <title>Markdown Viewer</title>
              <style>{{DocumentStyles.Css}}</style>
            </head>
            <body><main id="document" class="markdown-body">{{body}}</main></body>
            </html>
            """;

        return new RenderedDocument(html, headings, Words().Count(PlainText(document))) { BodyHtml = body };
    }

    private static string PlainText(MarkdownObject node)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer)
        {
            EnableHtmlForBlock = false,
            EnableHtmlForInline = false,
            EnableHtmlEscape = false
        };
        Pipeline.Setup(renderer);
        renderer.Render(node);
        writer.Flush();
        return writer.ToString();
    }

    // The host decides how to open normal links. Active schemes never reach that boundary.
    private static string SafeUrl(string url)
    {
        if (url.Any(char.IsControl))
            return "#";

        var trimmed = url.Trim();
        if (!Scheme().IsMatch(trimmed))
            return trimmed;

        if (trimmed.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // Embedded raster images are useful in exported notes and require no network access.
        if (trimmed.StartsWith("data:image/png;", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:image/jpeg;", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:image/gif;", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:image/webp;", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return "#";
    }
}
