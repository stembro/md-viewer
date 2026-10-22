using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MarkdownViewer;
using Microsoft.Web.WebView2.Wpf;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Document;

internal static class IntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        typeof(MainWindow).Assembly.GetType("MarkdownViewer.StartupMetrics")!
            .GetProperty("ProfileDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, Path.Combine(directory, "profile"));
        var path = Path.Combine(directory, "editing.md");
        File.WriteAllText(path, "# First title\n\nA **bold** paragraph with *emphasis*, `inline code`, and [a link](https://example.com).\n\n## A smaller heading\n\n- A bullet item\n- [ ] A task to do\n\n> A useful quotation.\n\n```csharp\nvar answer = 42;\n```\n\n" + string.Concat(Enumerable.Repeat("A paragraph to keep the page scrollable.\n\n", 100)));
        var app = new Application();
        var projectRoot = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        var xaml = XDocument.Load(Path.Combine(projectRoot, "src", "MarkdownViewer", "App.xaml"));
        var dictionary = new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), xaml.Root!.Elements().Single().Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var editWindow = new MainWindow(path);
        var viewWindow = new MainWindow(path);
        var started = false;
        editWindow.ContentRendered += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                await Ready(editWindow);
                viewWindow.Show();
                await Ready(viewWindow);
                var reader = (WebView2)editWindow.FindName("Reader");
                var preview = (WebView2)viewWindow.FindName("Reader");
                var source = reader.CoreWebView2.Source;
                var previewSource = preview.CoreWebView2.Source;
                Equal<object?>(null, Field(editWindow, "_editor"), "Reading must not create an editor.");
                editWindow.Activate();
                Call(editWindow, "EnterEditMode");
                var editor = (TextEditor)editWindow.FindName("Editor");
                editor.Select(2, 11);
                Call(editWindow, "ApplyFormatting", "bold");
                Equal("# **First title**", editor.Document.GetText(editor.Document.GetLineByNumber(1)), "Toolbar must format the selection.");
                editor.Undo();
                Equal("# First title", editor.Document.GetText(editor.Document.GetLineByNumber(1)), "Toolbar formatting must undo in one step.");
                editor.Redo();
                Call(editWindow, "ApplyFormatting", "bold");
                Equal("# First title", editor.Document.GetText(editor.Document.GetLineByNumber(1)), "Toolbar formatting must toggle off.");
                using (var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting))
                    Equal(true, highlighter.HighlightLine(1).Sections.Any(s => s.Color.Foreground is not null), "Markdown headings must be highlighted.");
                using (var highlighter = new DocumentHighlighter(new TextDocument("# Heading\n**bold** and *italic* and `code`\n```\n# inside code\n```\n## Outside code"), editor.SyntaxHighlighting))
                {
                    Equal(true, highlighter.HighlightLine(2).Sections.Any(s => s.Color.Name == "Bold"), "Bold syntax must be highlighted.");
                    Equal(true, highlighter.HighlightLine(4).Sections.Any(s => s.Color.Name == "Code"), "Fenced code must keep its state across lines.");
                    Equal(true, highlighter.HighlightLine(6).Sections.Any(s => s.Color.Name == "Heading"), "Highlighting must recover after a code fence.");
                }
                ((TextBox)editWindow.FindName("FindBox")).Text = "i";
                editor.Select(4, 0);
                await (Task)Call(editWindow, "FindAsync", true)!;
                Equal(3, editor.SelectionStart, "Backward search must include the character immediately before the caret.");
                var settings = Field(editWindow, "_settings")!;
                settings.GetType().GetProperty("DarkMode")!.SetValue(settings, false);
                Call(editWindow, "ApplyTheme");
                editor.Select(13, 0);
                await Task.Delay(150);
                Snapshot(editWindow, Path.Combine(directory, "editor-light.png"));
                settings.GetType().GetProperty("DarkMode")!.SetValue(settings, true);
                Call(editWindow, "ApplyTheme");
                Equal("#FFFFFFFF", editor.TextArea.Caret.CaretBrush.ToString(), "Dark-mode caret must have explicit high contrast.");
                Equal(true, editor.Options.HighlightCurrentLine, "The current line must be visible.");
                await Task.Delay(150);
                Snapshot(editWindow, Path.Combine(directory, "editor-dark.png"));
                Console.WriteLine("PASS  Native highlighting, dark caret, formatting, and single-step undo/redo.");
                editor.AppendText("\r\n## Saved from editor\r\n");
                var timer = Stopwatch.StartNew();
                await Until(async () => await preview.ExecuteScriptAsync("!!document.getElementById('saved-from-editor')") == "true");
                Console.WriteLine($"PASS  Autosave reaches the second window in {timer.Elapsed.TotalMilliseconds:F0} ms (includes the 650 ms save delay).");
                Equal(previewSource, preview.CoreWebView2.Source, "Live reload should update the existing page.");
                Equal(false, ((string?)Field(editWindow, "_renderedMarkdown"))?.Contains("Saved from editor") == true, "Autosave must defer hidden rendering.");
                await (Task)Call(editWindow, "ReturnToReadingModeAsync")!;
                Equal("true", await reader.ExecuteScriptAsync("!!document.getElementById('saved-from-editor')"), "Returning to Read must show edits.");
                Equal(source, reader.CoreWebView2.Source, "Mode switching should reuse the browser document.");
                Console.WriteLine("PASS  Edit/Read applies saved content without navigation.");

                await reader.ExecuteScriptAsync("window.__first = document.getElementById('document').firstElementChild");
                Call(editWindow, "EnterEditMode");
                await (Task)Call(editWindow, "ReturnToReadingModeAsync")!;
                Equal("true", await reader.ExecuteScriptAsync("window.__first === document.getElementById('document').firstElementChild"), "Unchanged mode switches must reuse the DOM.");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                await Task.Delay(500);
                Equal("true", await reader.ExecuteScriptAsync("window.__first === document.getElementById('document').firstElementChild"), "Duplicate file events must not rebuild the DOM.");
                Console.WriteLine("PASS  Unchanged mode switches and file events skip rendering.");

                await reader.ExecuteScriptAsync("window.scrollTo(0,500)");
                var scroll = await reader.ExecuteScriptAsync("window.scrollY");
                File.AppendAllText(path, "\n## External update\n");
                await Until(async () => await reader.ExecuteScriptAsync("!!document.getElementById('external-update')") == "true");
                Equal(scroll, await reader.ExecuteScriptAsync("window.scrollY"), "Live updates must keep the reading position.");
                Console.WriteLine("PASS  External changes preserve scroll position.");

                Call(editWindow, "EnterEditMode");
                editor.AppendText("\r\nLocal unsaved text\r\n");
                File.WriteAllText(path, "# Newer external content\n");
                await Until(() => Task.FromResult(Field(editWindow, "_externalConflict") is true));
                await Task.Delay(800);
                Equal("# Newer external content\n", File.ReadAllText(path), "Conflicting autosave must not overwrite the external file.");
                Equal(true, editor.Text.Contains("Local unsaved text"), "Conflicting edits must remain in the editor.");
                Console.WriteLine("PASS  Conflicting edits remain protected.");
                Close(editWindow);
                Close(viewWindow);
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Close(editWindow);
                Close(viewWindow);
                app.Shutdown(1);
            }
        };
        return app.Run(editWindow);
    }

    private static object? Call(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static object? Field(MainWindow window, string field) => typeof(MainWindow).GetField(field, Private)!.GetValue(window);
    private static void Snapshot(MainWindow window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void Close(MainWindow window)
    {
        typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
        window.Close();
    }
    private static Task Ready(MainWindow window) => Until(async () =>
    {
        var reader = (WebView2)window.FindName("Reader");
        return reader.CoreWebView2 is not null && await reader.ExecuteScriptAsync("!!document.getElementById('document')") == "true";
    });
    private static async Task Until(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Viewer did not reach the expected state.");
            await Task.Delay(50);
        }
    }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message);
    }
}
