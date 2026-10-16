using MarkdownViewer.Rendering;

var tests = new (string Name, Action Run)[]
{
    ("CommonMark and reading features", MarkdownFeatures),
    ("Outline IDs match rendered headings and remain unique", HeadingIdentifiers),
    ("HTML and attribute injection stay inert", HtmlInjection),
    ("Active URL schemes are removed", UnsafeLinks),
    ("Local images, links and fragments retain their destinations", LocalResources),
    ("CSP blocks scripts and remote image requests", ContentSecurityPolicy),
    ("Unicode text and word counts survive rendering", UnicodeText),
    ("Theme and printing styles are present", ThemeAndPrint),
    ("Empty documents are valid", EmptyDocument),
    ("Documents larger than 2 MiB retain their complete content", LargeDocument)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {name}: {exception.Message}");
    }
}
Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} renderer checks passed.");
return failures == 0 ? 0 : 1;

static void MarkdownFeatures()
{
    var rendered = MarkdownRenderer.Render("""
        # A readable document

        **Strong** and *emphasized* with ~~old text~~ and `inline code`.

        - [x] Finished
        - [ ] Next

        > A quotation.

        | Name | Value |
        | :--- | ---: |
        | One | 1 |

        ```csharp
        var result = 1 < 2;
        ```
        """);
    var body = Body(rendered);
    foreach (var expected in new[] { "<strong>Strong</strong>", "<em>emphasized</em>", "<del>old text</del>", "<code>inline code</code>", "type=\"checkbox\"", "checked", "disabled", "<blockquote>", "<table>", "<th", "<pre><code class=\"language-csharp\">", "1 &lt; 2" })
        Contains(body, expected);
}

static void HeadingIdentifiers()
{
    var result = MarkdownRenderer.Render("# Hello **world**\n\n## Hello world\n\n## Hello world\n\n### A &amp; B\n\n## !!!");
    Equal(5, result.Headings.Count);
    Equal("hello-world", result.Headings[0].Id);
    Equal("hello-world-1", result.Headings[1].Id);
    Equal("hello-world-2", result.Headings[2].Id);
    Equal("Hello world", result.Headings[0].Text);
    Equal("A & B", result.Headings[3].Text);
    Equal(3, result.Headings[3].Level);
    Equal(result.Headings.Count, result.Headings.Select(h => h.Id).Distinct().Count());
    foreach (var heading in result.Headings)
        Contains(Body(result), $"<h{heading.Level} id=\"{heading.Id}\">");
}

static void HtmlInjection()
{
    var result = MarkdownRenderer.Render("""
        <script>alert('unsafe')</script>

        <img src="https://example.com/tracker" onerror="alert(1)">

        <iframe src="https://example.com"></iframe>

        # Heading {onclick="alert(1)"}

        [Link](https://example.com "\" onclick=\"alert(1)")
        """);
    var body = Body(result);
    Excludes(body, "<script");
    Excludes(body, "<img src=\"https://example.com/tracker\"");
    Excludes(body, "<iframe");
    Excludes(body, " onclick=\"");
    Contains(body, "&lt;script&gt;");
    Contains(body, "&lt;img");
}

static void UnsafeLinks()
{
    var result = MarkdownRenderer.Render("[Script](javascript:alert%281%29) [File](file:///C:/Windows/win.ini) [Payload](data:text/html;base64,PHNjcmlwdD4=) [Mail](mailto:reader@example.com)");
    var body = Body(result);
    Excludes(body, "href=\"javascript:");
    Excludes(body, "href=\"file:");
    Excludes(body, "href=\"data:");
    Contains(body, "href=\"mailto:reader@example.com\"");
}

static void LocalResources()
{
    var result = MarkdownRenderer.Render("[Chapter](notes/chapter.md#details) [Jump](#details) ![Diagram](images/diagram.png) [Web](https://example.com/docs)\n\n## Details");
    Contains(result.Html, "<base href=\"https://markdown.local/\">");
    var body = Body(result);
    Contains(body, "href=\"notes/chapter.md#details\"");
    Contains(body, "href=\"#details\"");
    Contains(body, "src=\"images/diagram.png\"");
    Contains(body, "alt=\"Diagram\"");
    Contains(body, "href=\"https://example.com/docs\"");
}

static void ContentSecurityPolicy()
{
    var result = MarkdownRenderer.Render("![Remote](https://tracker.example/pixel.png)");
    Contains(result.Html, "http-equiv=\"Content-Security-Policy\"");
    Contains(result.Html, "default-src 'none'");
    Contains(result.Html, "script-src 'none'");
    Contains(result.Html, "img-src https://markdown.local data:;");
    Contains(result.Html, "base-uri https://markdown.local;");
    Contains(result.Html, "form-action 'none'");
    Contains(result.Html, "name=\"referrer\" content=\"no-referrer\"");
    Excludes(result.Html, "<script");
}

static void UnicodeText()
{
    var result = MarkdownRenderer.Render("# Café 日本語 📝\n\nnaïve résumé — Привет мир");
    Equal("Café 日本語 📝", result.Headings.Single().Text);
    Contains(Body(result), "Café 日本語 📝");
    Equal(6, result.WordCount);
    Equal(5, MarkdownRenderer.Render("# Two words\n\n[Three](https://example.com/this-url-is-not-content) **four** `five`").WordCount);
}

static void ThemeAndPrint()
{
    Contains(MarkdownRenderer.Render("# Light").Html, "data-theme=\"light\"");
    var dark = MarkdownRenderer.Render("# Dark", darkMode: true);
    Contains(dark.Html, "data-theme=\"dark\"");
    Contains(dark.Html, "color-scheme: dark");
    Contains(dark.Html, "@media print");
}

static void EmptyDocument()
{
    var result = MarkdownRenderer.Render(string.Empty);
    Equal(0, result.Headings.Count);
    Equal(0, result.WordCount);
    Equal(string.Empty, Body(result));
}

static void LargeDocument()
{
    const string paragraph = "A large local document keeps every paragraph, including **formatted text** and `inline code`.\n\n";
    var markdown = "# Start\n\n" + string.Concat(Enumerable.Repeat(paragraph, 24_000)) + "## Final section\n\nEND-OF-DOCUMENT";
    var result = MarkdownRenderer.Render(markdown);
    if (System.Text.Encoding.UTF8.GetByteCount(result.Html) <= 2 * 1024 * 1024)
        throw new InvalidOperationException("The size regression fixture must exceed the old 2 MiB navigation limit.");
    Equal(2, result.Headings.Count);
    Equal("Final section", result.Headings[^1].Text);
    Contains(Body(result), "<h2 id=\"final-section\">Final section</h2>");
    Contains(Body(result), "END-OF-DOCUMENT</p>");
    Equal(24_000 * 13 + 4, result.WordCount);
}

static string Body(RenderedDocument document)
{
    const string open = "<main id=\"document\" class=\"markdown-body\">";
    var start = document.Html.IndexOf(open, StringComparison.Ordinal) + open.Length;
    var end = document.Html.IndexOf("</main>", start, StringComparison.Ordinal);
    return document.Html[start..end];
}

static void Contains(string actual, string expected)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected output to contain: {expected}");
}

static void Excludes(string actual, string unexpected)
{
    if (actual.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Output unexpectedly contains: {unexpected}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
