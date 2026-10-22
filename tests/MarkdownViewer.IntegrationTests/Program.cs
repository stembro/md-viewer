using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MarkdownViewer;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Document;
using MarkdownViewer.Browsing;

internal static class IntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        var workspacePath = Path.Combine(directory, "profile", "workspace.json");
        if (File.Exists(workspacePath)) File.Delete(workspacePath);
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
        var viewWindow = new MainWindow(path, restoreWorkspace: false);
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

                // Discard only this harness's deliberately conflicting draft.
                typeof(MainWindow).GetField("_isDirty", Private)!.SetValue(editWindow, false);
                Call(editWindow, "LeaveEditMode");
                var library = Path.Combine(directory, "library-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(library, "how-to"));
                Directory.CreateDirectory(Path.Combine(library, ".git"));
                var indexPath = Path.Combine(library, "README.md");
                var nestedPath = Path.Combine(library, "how-to", "nested.md");
                File.WriteAllText(indexPath, "# Folder index\n\n" + string.Concat(Enumerable.Repeat("A paragraph in the folder index.\n\n", 30)) + "## Second section\n\nA place to jump to from the outline.\n\n" + string.Concat(Enumerable.Repeat("A paragraph after the second heading.\n\n", 20)));
                File.WriteAllText(nestedPath, "# Nested document\n");
                File.WriteAllText(Path.Combine(library, "image.png"), "not a note");
                settings.GetType().GetProperty("ShowOutline")!.SetValue(settings, false);
                await (Task)Call(editWindow, "SetFolderAsync", library, false)!;
                var root = (FolderNode)Field(editWindow, "_folderRoot")!;
                Equal("how-to|README.md", string.Join('|', root.Children.Select(n => n.Name)), "Tree must show folders and Markdown, with .git and other files omitted.");
                var nestedFolder = root.Children.Single(n => n.IsDirectory);
                Equal(false, nestedFolder.IsLoaded, "Collapsed folders must stay lazy.");
                var tree = (TreeView)editWindow.FindName("FolderTree");
                tree.UpdateLayout();
                nestedFolder.IsExpanded = true;
                await Until(() => Task.FromResult(nestedFolder.IsLoaded));
                nestedFolder.Children.Single().IsSelected = true;
                await Until(async () => await reader.ExecuteScriptAsync("!!document.getElementById('nested-document')") == "true");
                Equal(nestedPath, Field(editWindow, "_currentPath") as string, "Selecting a tree file must open it.");
                Call(editWindow, "EnterEditMode");
                editor.AppendText("\n## Pending tree edit\n");
                root.Children.Single(n => n.Name == "README.md").IsSelected = true;
                await Until(async () => await reader.ExecuteScriptAsync("!!document.getElementById('folder-index')") == "true");
                Equal(true, File.ReadAllText(nestedPath).Contains("Pending tree edit"), "Tree navigation must save pending edits before switching files.");
                var addedPath = Path.Combine(library, "added.md");
                var renamedPath = Path.Combine(library, "renamed.md");
                nestedFolder.IsSelected = true;
                File.WriteAllText(addedPath, "# Added\n");
                await Until(() => Task.FromResult(root.Children.Any(n => n.Name == "added.md")));
                Equal(nestedFolder.FullPath, (tree.SelectedItem as FolderNode)?.FullPath, "Refreshing must retain a folder selection while browsing.");
                root.Children.Single(n => n.Name == "README.md").IsSelected = true;
                File.Move(addedPath, renamedPath);
                await Until(() => Task.FromResult(root.Children.Any(n => n.Name == "renamed.md") && root.Children.All(n => n.Name != "added.md")));
                File.Delete(renamedPath);
                await Until(() => Task.FromResult(root.Children.All(n => n.Name != "renamed.md")));
                Equal(true, nestedFolder.IsExpanded, "Folder refresh must retain expansion.");
                Equal(indexPath, (tree.SelectedItem as FolderNode)?.FullPath, "Folder refresh must retain the current selection.");
                Console.WriteLine("PASS  Folder expansion, file selection, safe edit navigation, and live additions/renames.");
                settings.GetType().GetProperty("SidebarWidth")!.SetValue(settings, 260d);
                settings.GetType().GetProperty("OutlineWidth")!.SetValue(settings, 240d);
                Call(editWindow, "ApplySidebars");
                var filesSidebar = (FrameworkElement)editWindow.FindName("FilesSidebar");
                var outlineSidebar = (FrameworkElement)editWindow.FindName("OutlineSidebar");
                var filesToggle = (Button)editWindow.FindName("FilesToggleButton");
                var outlineToggle = (Button)editWindow.FindName("OutlineToggleButton");
                var filesColumn = (ColumnDefinition)editWindow.FindName("FilesColumn");
                var outlineColumn = (ColumnDefinition)editWindow.FindName("OutlineColumn");
                Equal(Visibility.Visible, filesSidebar.Visibility, "Opening a folder must show Files.");
                Equal(Visibility.Collapsed, outlineSidebar.Visibility, "Files must not force Outline open.");
                outlineToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editWindow.UpdateLayout();
                Equal(Visibility.Visible, filesSidebar.Visibility, "Opening Outline must retain Files.");
                Equal(Visibility.Visible, outlineSidebar.Visibility, "Both panels must be visible together.");
                var content = (FrameworkElement)editWindow.Content;
                var readerLeft = reader.TransformToAncestor(content).Transform(new Point()).X;
                var outlineLeft = outlineSidebar.TransformToAncestor(content).Transform(new Point()).X;
                Equal(true, readerLeft >= filesSidebar.ActualWidth, "Files must sit to the left of the document.");
                Equal(true, outlineLeft >= readerLeft + reader.ActualWidth, "Outline must sit to the right of the document.");
                filesToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(Visibility.Visible, outlineSidebar.Visibility, "Hiding Files must retain Outline.");
                outlineToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editWindow.UpdateLayout();
                Equal(0d, filesColumn.ActualWidth, "Hidden Files must release its width.");
                Equal(0d, outlineColumn.ActualWidth, "Hidden Outline must release its width.");
                filesToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Call(editWindow, "HandleShortcut", Key.L, ModifierKeys.Control | ModifierKeys.Shift);
                Call(editWindow, "EnterEditMode");
                var beforeToggle = editor.Text;
                Equal(false, (bool)Call(editWindow, "HandleEditorShortcut", Key.B, ModifierKeys.Control | ModifierKeys.Shift)!, "The editor must pass through the Files shortcut.");
                Equal(false, (bool)Call(editWindow, "HandleEditorShortcut", Key.L, ModifierKeys.Control | ModifierKeys.Shift)!, "The editor must pass through the Outline shortcut.");
                Call(editWindow, "HandleShortcut", Key.B, ModifierKeys.Control | ModifierKeys.Shift);
                Equal(Visibility.Collapsed, filesSidebar.Visibility, "The Files shortcut must work in Edit mode.");
                Equal(Visibility.Visible, outlineSidebar.Visibility, "The Files shortcut must leave Outline visible.");
                Equal(beforeToggle, editor.Text, "Panel shortcuts must leave the editor text unchanged.");
                Call(editWindow, "HandleShortcut", Key.B, ModifierKeys.Control | ModifierKeys.Shift);
                await (Task)Call(editWindow, "ReturnToReadingModeAsync")!;
                DragSplitter(editWindow, "FilesSplitter", 60);
                DragSplitter(editWindow, "OutlineSplitter", -20);
                Equal(320d, (double)settings.GetType().GetProperty("SidebarWidth")!.GetValue(settings)!, "Files width must be remembered independently.");
                Equal(260d, (double)settings.GetType().GetProperty("OutlineWidth")!.GetValue(settings)!, "Outline width must be remembered independently.");
                editWindow.Width = 800;
                await Task.Delay(150);
                editWindow.UpdateLayout();
                Equal(true, reader.ActualWidth >= 359, "A narrow window must retain document space with both panels open.");
                await SnapshotDocument(editWindow, Path.Combine(directory, "panels-narrow.png"));
                Equal(true, outlineSidebar.TransformToAncestor(content).Transform(new Point()).X + outlineSidebar.ActualWidth <= content.ActualWidth + 1, $"Both panels must fit inside a narrow window: content={content.ActualWidth}, layout={((Grid)editWindow.FindName("NavigationLayout")).ActualWidth}, files={filesColumn.ActualWidth}, reader={reader.ActualWidth}, outline={outlineColumn.ActualWidth}, right={outlineSidebar.TransformToAncestor(content).Transform(new Point()).X + outlineSidebar.ActualWidth}.");
                editWindow.Width = 1180;
                await Task.Delay(150);
                editWindow.UpdateLayout();
                Equal(320d, filesColumn.ActualWidth, "Files must regain its preferred width when the window expands.");
                Equal(260d, outlineColumn.ActualWidth, "Outline must regain its preferred width when the window expands.");
                var outlineList = (ListBox)editWindow.FindName("OutlineList");
                outlineList.SelectedIndex = 1;
                await Until(async () => double.TryParse(await reader.ExecuteScriptAsync("window.scrollY"), out var offset) && offset > 200);
                Equal(indexPath, Field(editWindow, "_currentPath") as string, "Outline navigation must stay in the current document.");
                await reader.ExecuteScriptAsync("window.scrollTo(0,0)");
                await Task.Delay(100);
                await SnapshotDocument(editWindow, Path.Combine(directory, "folder-tree-dark.png"));
                settings.GetType().GetProperty("DarkMode")!.SetValue(settings, false);
                Call(editWindow, "ApplyTheme");
                await reader.ExecuteScriptAsync("document.documentElement.dataset.theme='light'");
                await Task.Delay(150);
                await SnapshotDocument(editWindow, Path.Combine(directory, "folder-tree-light.png"));
                Equal(library, settings.GetType().GetProperty("LastFolder")!.GetValue(settings) as string, "The folder choice must be remembered.");
                Console.WriteLine("PASS  Independent panels, editor shortcuts, outline navigation, and remembered widths at narrow/wide sizes.");
                var reopened = new MainWindow(null, restoreWorkspace: false);
                reopened.Show();
                await Ready(reopened);
                await Until(() => Task.FromResult((Field(reopened, "_folderRoot") as FolderNode)?.FullPath == library));
                Equal(indexPath, Field(reopened, "_currentPath") as string, "Reopening must restore the folder and its README index.");
                reopened.UpdateLayout();
                Equal(Visibility.Visible, ((FrameworkElement)reopened.FindName("FilesSidebar")).Visibility, "Reopening must restore Files visibility.");
                Equal(Visibility.Visible, ((FrameworkElement)reopened.FindName("OutlineSidebar")).Visibility, "Reopening must restore Outline visibility.");
                Equal(320d, ((ColumnDefinition)reopened.FindName("FilesColumn")).ActualWidth, "Reopening must restore Files width.");
                Equal(260d, ((ColumnDefinition)reopened.FindName("OutlineColumn")).ActualWidth, "Reopening must restore Outline width.");
                Close(reopened);
                editWindow.Activate();
                Call(editWindow, "EnterEditMode");
                editor.AppendText("\nLocal pending tree conflict\n");
                File.WriteAllText(indexPath, "# External index version\n");
                await Until(() => Task.FromResult(Field(editWindow, "_externalConflict") is true));
                nestedFolder.Children.Single().IsSelected = true;
                await Until(() => Task.FromResult(Field(editWindow, "_currentPath") as string == nestedPath && Field(editWindow, "_switchingDocuments") is false));
                Equal(true, Field(editWindow, "_isEditing") is true, "The other tab must retain its Edit mode.");
                root.Children.Single(n => n.Name == "README.md").IsSelected = true;
                await Until(() => Task.FromResult(Field(editWindow, "_currentPath") as string == indexPath && Field(editWindow, "_switchingDocuments") is false));
                Equal(true, editor.Text.Contains("Local pending tree conflict"), "A conflicting draft must survive switching tabs.");
                Equal(true, Field(editWindow, "_externalConflict") is true, "The original tab must retain its conflict protection.");
                Equal("# External index version\n", File.ReadAllText(indexPath), "Tab switching must not overwrite an external edit.");
                Console.WriteLine("PASS  Folder restore and conflict drafts survive tab switching.");
                // Discard only the harness's intentionally conflicting draft.
                typeof(MainWindow).GetField("_isDirty", Private)!.SetValue(editWindow, false);
                Call(editWindow, "LeaveEditMode");
                await CheckTabs(editWindow, directory, workspacePath);
                await CheckTabMouseAndMenus(editWindow, directory);
                await CheckFileOperations(editWindow, directory);
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

    private static void DragSplitter(MainWindow window, string name, double delta)
    {
        var splitter = (GridSplitter)window.FindName(name);
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        // Deliberately finish before layout catches up: this reproduced the bug.
        splitter.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        window.UpdateLayout();
    }

    private static async Task CheckTabMouseAndMenus(MainWindow window, string directory)
    {
        var list = (ListBox)window.FindName("TabList");
        var files = Enumerable.Range(1, 3).Select(i => Path.Combine(directory, $"mouse-tab-{i}.md")).ToArray();
        foreach (var file in files) { File.WriteAllText(file, "# Mouse tab\n"); await Open(file); }
        window.UpdateLayout();
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var middle = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        first.RaiseEvent(middle);
        await Until(() => Task.FromResult(list.Items.Count == 2 && Field(window, "_switchingDocuments") is false));
        Equal(true, middle.Handled, "Middle click anywhere on the tab container must close the tab.");
        Equal(files[2], Field(window, "_currentPath") as string, "Closing an inactive tab must not switch documents.");
        window.UpdateLayout();
        var other = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        other.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        await Until(() => Task.FromResult(Field(window, "_openContextMenu") is ContextMenu { IsOpen: true }));
        var menu = (ContextMenu)Field(window, "_openContextMenu")!;
        Equal(true, menu.IsOpen, "Right click must open a tab context menu.");
        Menu(menu, "Close other tabs").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        await Until(() => Task.FromResult(list.Items.Count == 1 && Field(window, "_switchingDocuments") is false));
        Equal(files[1], Field(window, "_currentPath") as string, "Close other tabs must retain the right-clicked document.");
        await Open(files[0]); await Open(files[2]);
        menu = (ContextMenu)Call(window, "BuildTabMenu", list.Items[0], list)!;
        Menu(menu, "Close tabs to the right").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => Task.FromResult(list.Items.Count == 1 && Field(window, "_switchingDocuments") is false));
        menu = (ContextMenu)Call(window, "BuildTabMenu", list.Items[0], list)!;
        Menu(menu, "Close all tabs").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => Task.FromResult(list.Items.Count == 0));
        Console.WriteLine("PASS  Routed middle/right mouse events and tab close-other/close-right/close-all menus.");
        async Task Open(string path) => await (Task)Call(window, "OpenDocumentAsync", path, true, null, false, false, false)!;
    }

    private static MenuItem Menu(ContextMenu menu, string header) => menu.Items.OfType<MenuItem>().Single(i => i.Header as string == header);

    private static async Task CheckFileOperations(MainWindow window, string directory)
    {
        var folder = Path.Combine(directory, "file-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await (Task)Call(window, "SetFolderAsync", folder, false)!;
        await (Task)Call(window, "CreateDocumentAsync", folder, "First note")!;
        var original = Path.Combine(folder, "First note.md");
        var renamed = Path.Combine(folder, "Renamed note.md");
        Equal(true, File.Exists(original), "New note must create a Markdown file.");
        Equal(true, Field(window, "_isEditing") is true, "A new note must open in Edit mode.");
        var editor = (TextEditor)window.FindName("Editor");
        editor.AppendText("A draft before renaming.\n"); editor.Select(2, 3);
        ((System.Windows.Threading.DispatcherTimer)Field(window, "_autoSaveTimer")!).Stop();
        var buffer = editor.Document;
        Equal(true, await (Task<bool>)Call(window, "RenameDocumentAsync", original, "Renamed note")!, "Rename must succeed.");
        Equal(false, File.Exists(original), "Rename must move the original path.");
        Equal(renamed, Field(window, "_currentPath") as string, "Rename must update the active document path.");
        Equal(true, ReferenceEquals(buffer, editor.Document), "Rename must retain the editor buffer and undo history.");
        Equal(2, editor.SelectionStart, "Rename must retain the selection.");
        Equal(true, await (Task<bool>)Call(window, "SaveEditorAsync", false)!, "Saving after rename must succeed at the new path.");
        Equal("A draft before renaming.\n", File.ReadAllText(renamed).Replace("\r\n", "\n"), "Renamed edits must save to the new file.");
        Equal(false, File.Exists(original), "Autosave must not recreate the old path.");
        var caseRename = Path.Combine(folder, "RENAMED NOTE.md");
        Equal(true, await (Task<bool>)Call(window, "RenameDocumentAsync", renamed, "RENAMED NOTE.md")!, "Case-only rename must work.");
        renamed = caseRename;
        var existing = Path.Combine(folder, "Existing.md"); File.WriteAllText(existing, "# Keep this file\n");
        Equal(false, await (Task<bool>)Call(window, "RenameDocumentAsync", renamed, "Existing.md")!, "Rename must not overwrite an existing file.");
        Equal("# Keep this file\n", File.ReadAllText(existing), "A conflicting rename must preserve the destination.");
        await (Task)Call(window, "CreateDocumentAsync", folder, "Existing.md")!;
        Equal("# Keep this file\n", File.ReadAllText(existing), "New note must not overwrite an existing file.");
        await (Task)Call(window, "CreateFolderAsync", folder, "Subfolder")!;
        var childFolder = Path.Combine(folder, "Subfolder");
        await (Task)Call(window, "CreateDocumentAsync", childFolder, "Child.md")!;
        var child = Path.Combine(childFolder, "Child.md");
        Equal(true, File.Exists(child), "New notes must support a selected subfolder.");
        Equal(true, await (Task<bool>)Call(window, "RenameDocumentAsync", renamed, "Archived note.md")!, "Inactive tabs must support rename.");
        renamed = Path.Combine(folder, "Archived note.md");
        Equal(child, Field(window, "_currentPath") as string, "Renaming another tab must not switch documents.");
        var root = (FolderNode)Field(window, "_folderRoot")!;
        var childNode = root.Children.Single(n => n.IsDirectory).Children.Single(n => n.Name == "Child.md");
        var tree = (TreeView)window.FindName("FolderTree");
        tree.UpdateLayout();
        var folderItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(root.Children.Single(n => n.IsDirectory));
        folderItem.UpdateLayout();
        var childItem = (TreeViewItem)folderItem.ItemContainerGenerator.ContainerFromItem(childNode);
        var settings = Field(window, "_settings")!;
        foreach (var dark in new[] { true, false })
        {
            settings.GetType().GetProperty("DarkMode")!.SetValue(settings, dark); Call(window, "ApplyTheme");
            childItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Until(() => Task.FromResult(Field(window, "_openContextMenu") is ContextMenu { IsOpen: true }));
            var menu = (ContextMenu)Field(window, "_openContextMenu")!;
            Equal(true, Menu(menu, "Rename…").IsEnabled, "File menu must offer Rename.");
            Equal(true, Menu(menu, "Delete…").IsEnabled, "File menu must offer Delete.");
            menu.IsOpen = true; await Task.Delay(100); menu.UpdateLayout();
            SnapshotElement(menu, Path.Combine(directory, dark ? "file-menu-dark.png" : "file-menu-light.png"));
            menu.IsOpen = false;
        }
        Equal(true, await (Task<bool>)Call(window, "DeleteDocumentAsync", child)!, "Delete must move a generated note to the Recycle Bin.");
        Equal(false, File.Exists(child), "Deleted files must leave the folder.");
        Equal(false, ((ListBox)window.FindName("TabList")).Items.Cast<object>().Any(t => t.GetType().GetProperty("Path")!.GetValue(t) as string == child), "Delete must close its tab.");
        await (Task)Call(window, "OpenDocumentAsync", renamed, true, null, false, false, false)!;
        editor.AppendText("An unsaved conflict draft.\n"); File.WriteAllText(renamed, "# External version\n");
        await Until(() => Task.FromResult(Field(window, "_externalConflict") is true));
        Equal(false, await (Task<bool>)Call(window, "DeleteDocumentAsync", renamed)!, "Delete must stop when a draft cannot be saved.");
        Equal(true, File.Exists(renamed), "A conflict must leave the file intact.");
        Equal(true, editor.Text.Contains("An unsaved conflict draft."), "A blocked deletion must keep the draft.");
        typeof(MainWindow).GetField("_isDirty", Private)!.SetValue(window, false); Call(window, "LeaveEditMode");
        Equal(true, await (Task<bool>)Call(window, "DeleteDocumentAsync", renamed)!, "A clean tab can be deleted.");
        Console.WriteLine("PASS  New notes/folders, rename with preserved editor state, collision protection, themed menus, recycling, and conflict-safe deletion.");
    }

    private static void SnapshotElement(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); SaveImage(bitmap, path);
    }

    private static async Task CheckTabs(MainWindow window, string directory, string workspacePath)
    {
        var list = (ListBox)window.FindName("TabList");
        foreach (var oldTab in list.Items.Cast<object>().ToArray()) await (Task)Call(window, "CloseTabAsync", oldTab)!;
        Equal(Visibility.Visible, ((FrameworkElement)window.FindName("Welcome")).Visibility, "Closing the last tab must show Welcome.");
        var firstFolder = Path.Combine(directory, "tabs", "reading");
        var secondFolder = Path.Combine(directory, "tabs", "editing");
        Directory.CreateDirectory(firstFolder); Directory.CreateDirectory(secondFolder);
        var first = Path.Combine(firstFolder, "notes.md");
        var second = Path.Combine(secondFolder, "notes.md");
        var firstText = "# Reading tab\n\n[Relative note](relative.md)\n\n" + string.Concat(Enumerable.Repeat("A paragraph for a reading position.\n\n", 80));
        File.WriteAllText(first, firstText);
        File.WriteAllText(second, "# Editing tab\n\n" + string.Concat(Enumerable.Repeat("An editable line.\n", 80)));
        var reader = (WebView2)window.FindName("Reader");
        await Open(first);
        await reader.ExecuteScriptAsync("window.scrollTo(0,420)");
        var scroll = await reader.ExecuteScriptAsync("window.scrollY");
        await Open(second);
        Call(window, "EnterEditMode");
        var editor = (TextEditor)window.FindName("Editor");
        editor.AppendText("\nA pending edit in its own tab.\n");
        editor.Select(15, 7);
        editor.ScrollToVerticalOffset(200);
        await Task.Delay(100);
        var editorScroll = editor.VerticalOffset;
        Equal(true, editorScroll > 100, "The editor fixture must be scrolled before switching tabs.");
        var buffer = editor.Document;
        var save = (Task<bool>)Call(window, "SaveEditorAsync", false)!;
        await Open(first);
        await save;
        Equal(false, Field(window, "_isEditing") is true, "The reading tab must stay in Read mode.");
        Equal(true, File.ReadAllText(second).Contains("A pending edit in its own tab."), "Switching must flush a pending edit to its own file.");
        Equal(firstText, File.ReadAllText(first), "A save racing a switch must not write to the other document.");
        Equal(scroll, await reader.ExecuteScriptAsync("window.scrollY"), "Reading position must survive tab switches.");
        Equal(2, list.Items.Count, "Same-named files in different folders must have separate tabs.");
        var tab = list.Items[1];
        list.SelectedItem = tab;
        await Until(() => Task.FromResult(Field(window, "_currentPath") as string == second && Field(window, "_switchingDocuments") is false));
        Equal(true, Field(window, "_isEditing") is true, "Selecting a tab must restore its Edit mode.");
        Equal(true, ReferenceEquals(buffer, editor.Document), "Each tab must retain its editor document and undo stack.");
        Equal(true, editor.IsKeyboardFocusWithin, "An editor tab must receive typing focus after selection.");
        Equal(15, editor.SelectionStart, "Editor selection start must survive switching.");
        Equal(7, editor.SelectionLength, "Editor selection length must survive switching.");
        Equal(true, Math.Abs(editorScroll - editor.VerticalOffset) < 2, "Editor scroll position must survive switching.");
        editor.Undo();
        Equal(false, editor.Text.Contains("A pending edit in its own tab."), "Undo must still reach edits made before switching tabs.");
        await Open(second.ToUpperInvariant());
        Equal(2, list.Items.Count, "Reopening a path must activate its existing tab, ignoring path case.");
        editor.AppendText("\nDraft protected while another tab is active.\n");
        File.WriteAllText(second, "# Editing tab\n\nExternal content.\n");
        await Until(() => Task.FromResult(Field(window, "_externalConflict") is true));
        await Open(first);
        Equal("!", tab.GetType().GetProperty("Indicator")!.GetValue(tab) as string, "An inactive conflicting draft must remain visibly marked.");
        await Open(second);
        Equal(true, editor.Text.Contains("Draft protected while another tab is active."), "Conflict drafts must survive tab switches.");
        Equal("# Editing tab\n\nExternal content.\n", File.ReadAllText(second), "A conflict draft must not overwrite disk on a tab switch.");
        // Discard only this generated conflict draft, then reload the disk version.
        typeof(MainWindow).GetField("_isDirty", Private)!.SetValue(window, false);
        Call(window, "LeaveEditMode");
        await (Task)Call(window, "OpenDocumentAsync", second, false, null, false, false, false)!;
        Call(window, "EnterEditMode");
        editor.Select(2, 4);
        await (Task)Call(window, "CaptureActiveTabAsync")!;
        Call(window, "SaveWorkspace");
        Equal(true, File.Exists(workspacePath), "The workspace must be persisted.");
        var restored = new MainWindow(null);
        restored.Show();
        await Ready(restored);
        await Until(() => Task.FromResult(Field(restored, "_currentPath") as string == second && Field(restored, "_switchingDocuments") is false && Field(restored, "_isEditing") is true));
        var restoredList = (ListBox)restored.FindName("TabList");
        Equal(2, restoredList.Items.Count, "Startup must restore the open tab set.");
        Equal<object?>(null, restoredList.Items[0].GetType().GetField("Source")!.GetValue(restoredList.Items[0]), "Inactive restored tabs must remain unloaded.");
        var restoredEditor = (TextEditor)restored.FindName("Editor");
        Equal(2, restoredEditor.SelectionStart, "Startup must restore the active editor selection.");
        Equal(4, restoredEditor.SelectionLength, "Startup must restore the active editor selection length.");
        await (Task)Call(restored, "OpenDocumentAsync", first, true, null, false, false, false)!;
        Equal(false, Field(restored, "_isEditing") is true, "Startup must restore the other tab's Read mode.");
        Equal(scroll, await ((WebView2)restored.FindName("Reader")).ExecuteScriptAsync("window.scrollY"), "Startup must restore the reading position.");
        restored.Close();
        await Until(() => Task.FromResult(Field(restored, "_closed") is true));
        window.Activate();
        await Open(first);
        Equal(true, (await reader.ExecuteScriptAsync("document.baseURI")).Contains("/tabs/reading/"), "Switching must update the base directory used for relative links and images.");
        await Open(Path.Combine(directory, "tabs", "missing.md"));
        Equal(first, Field(window, "_currentPath") as string, "An invalid file must leave the active tab intact.");
        Equal(2, list.Items.Count, "An invalid file must not create a tab.");
        Call(window, "NoticeClose_Click", window, new RoutedEventArgs());
        await reader.ExecuteScriptAsync("window.scrollTo(0,0)");
        var settings = Field(window, "_settings")!;
        foreach (var dark in new[] { true, false })
        {
            settings.GetType().GetProperty("DarkMode")!.SetValue(settings, dark);
            Call(window, "ApplyTheme");
            await reader.ExecuteScriptAsync($"document.documentElement.dataset.theme='{(dark ? "dark" : "light")}'");
            Equal(dark ? "\"#202421\"" : "\"#fffefa\"", await reader.ExecuteScriptAsync("getComputedStyle(document.documentElement).getPropertyValue('--paper').trim()"), "Rendered document theme must match the native shell.");
            await Task.Delay(500);
            await SnapshotDocument(window, Path.Combine(directory, dark ? "workspace-dark.png" : "workspace-light.png"));
        }
        Call(window, "HandleShortcut", Key.Tab, ModifierKeys.Control);
        await Until(() => Task.FromResult(Field(window, "_currentPath") as string == second && Field(window, "_switchingDocuments") is false));
        Call(window, "HandleShortcut", Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        await Until(() => Task.FromResult(Field(window, "_currentPath") as string == first && Field(window, "_switchingDocuments") is false));
        Call(window, "HandleShortcut", Key.W, ModifierKeys.Control);
        await Until(() => Task.FromResult(list.Items.Count == 1 && Field(window, "_switchingDocuments") is false));
        await (Task)Call(window, "CloseTabAsync", list.Items[0])!;
        Equal(Visibility.Visible, ((FrameworkElement)window.FindName("Welcome")).Visibility, "Closing all tabs must restore Welcome.");
        Equal(false, ((Button)window.FindName("ModeButton")).IsEnabled, "An empty workspace must disable Edit.");
        Console.WriteLine("PASS  Tab state, undo, selection, scroll, concurrent saving, conflict drafts, lazy session restore, relative paths, shortcuts, and closing all tabs.");

        async Task Open(string file) => await (Task)Call(window, "OpenDocumentAsync", file, true, null, false, false, false)!;
    }

    private static object? Call(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static object? Field(MainWindow window, string field) => typeof(MainWindow).GetField(field, Private)!.GetValue(window);
    private static void Snapshot(MainWindow window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        SaveImage(bitmap, path);
    }
    private static async Task SnapshotDocument(MainWindow window, string path)
    {
        var reader = (WebView2)window.FindName("Reader");
        using var stream = new MemoryStream();
        await reader.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Position = 0;
        var browser = new BitmapImage();
        browser.BeginInit();
        browser.CacheOption = BitmapCacheOption.OnLoad;
        browser.StreamSource = stream;
        browser.EndInit();
        var content = (FrameworkElement)window.Content;
        var shell = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        shell.Render(content);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            drawing.DrawImage(shell, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            var point = reader.TransformToAncestor(content).Transform(new Point());
            drawing.DrawImage(browser, new Rect(point, new Size(reader.ActualWidth, reader.ActualHeight)));
        }
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SaveImage(bitmap, path);
    }
    private static void SaveImage(BitmapSource bitmap, string path)
    {
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
        return reader.CoreWebView2 is not null && Field(window, "_switchingDocuments") is false
            && await reader.ExecuteScriptAsync("!!document.getElementById('document')") == "true";
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
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }
}
