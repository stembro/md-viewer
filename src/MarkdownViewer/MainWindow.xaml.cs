using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownViewer.Rendering;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MarkdownViewer;

public partial class MainWindow : Window
{
    private readonly UserSettings _settings = UserSettings.Load();
    private readonly string? _initialPath;
    private readonly List<string> _history = [];
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private FileSystemWatcher? _watcher;
    private Task? _initialization;
    private string? _currentPath;
    private MarkdownTextFile? _sourceFile;
    private string _currentMarkdown = "";
    private bool _isEditing;
    private bool _isLoadingEditor;
    private bool _isSaving;
    private bool _isDirty;
    private bool _externalConflict;
    private bool _allowClose;
    private int _editorRevision;
    private string _documentHtml = "";
    private string _navigationUri = "";
    private string? _pendingFragment;
    private double _pendingScroll;
    private int _historyIndex = -1;
    private int _openVersion;
    private bool _closed;
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon"
    };

    public MainWindow(string? initialPath)
    {
        _initialPath = initialPath;
        InitializeComponent();
        ApplyTheme();
        ApplyOutline();
        UpdateZoom();
        _reloadTimer.Tick += async (_, _) =>
        {
            _reloadTimer.Stop();
            if (_currentPath is not null && !_isEditing) await OpenDocumentAsync(_currentPath, addHistory: false, preserveScroll: true);
        };
        _autoSaveTimer.Tick += async (_, _) =>
        {
            _autoSaveTimer.Stop();
            await SaveEditorAsync();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTitleBarTheme();
        try
        {
            if (_initialPath is not null) await OpenDocumentAsync(_initialPath);
        }
        catch (Exception ex) { ShowNotice("The reading engine could not start. Install or repair Microsoft Edge WebView2 Runtime. " + ex.Message); }
    }

    private Task EnsureReaderAsync() => _initialization ??= InitializeReaderAsync();

    private async Task InitializeReaderAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(UserSettings.DataDirectory, "WebView2"));
        await Reader.EnsureCoreWebView2Async(environment);
        var core = Reader.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ResourceRequested;
        core.NavigationStarting += NavigationStarting;
        core.NavigationCompleted += NavigationCompleted;
        core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.IsUserInitiated) _ = FollowLinkAsync(e.Uri); };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, _) => ShowNotice("The reading engine stopped. Close and reopen this window to continue.");
        Reader.ZoomFactorChanged += (_, _) =>
        {
            _settings.Zoom = Math.Clamp(Reader.ZoomFactor, 0.5, 2.5);
            if (Math.Abs(Reader.ZoomFactor - _settings.Zoom) > .001) Reader.ZoomFactor = _settings.Zoom;
            ZoomButton.Content = $"{_settings.Zoom:P0}";
        };
        Reader.ZoomFactor = _settings.Zoom;
    }

    private async void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.Scheme == "https" && uri.Host == "markdown.local")
            {
                if (uri.AbsolutePath == "/__viewer_document__.html")
                {
                    e.Response = Response(Encoding.UTF8.GetBytes(_documentHtml), "text/html; charset=utf-8");
                    return;
                }
                if (e.ResourceContext == CoreWebView2WebResourceContext.Image && TryLocalPath(uri, out var path)
                    && ImageTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length <= 20 * 1024 * 1024)
                    {
                        var bytes = await File.ReadAllBytesAsync(path);
                        if (!_closed) e.Response = Response(bytes, contentType);
                        return;
                    }
                }
            }
            e.Response = Response([], "text/plain", 403);
        }
        catch (Exception) { if (!_closed) e.Response = Response([], "text/plain", 404); }
        finally { deferral.Complete(); }
    }

    private CoreWebView2WebResourceResponse Response(byte[] content, string contentType, int status = 200) =>
        Reader.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(content), status,
            status == 200 ? "OK" : "Not available", $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");

    private static bool TryLocalPath(Uri uri, out string path)
    {
        path = "";
        if (uri.Scheme != "https" || uri.Host != "markdown.local") return false;
        try
        {
            var decoded = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', '\\');
            // Only explicit local drive paths; never UNC shares, device paths, or alternate streams.
            if (decoded.Length < 3 || !char.IsAsciiLetter(decoded[0]) || decoded[1] != ':' || decoded[2] != '\\' || decoded[2..].Contains(':')) return false;
            path = Path.GetFullPath(decoded);
            return true;
        }
        catch { return false; }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri == _navigationUri) return;
        e.Cancel = true;
        if (e.IsUserInitiated) _ = FollowLinkAsync(e.Uri);
    }

    private async void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _closed) return;
        if (!string.IsNullOrEmpty(_pendingFragment)) await ScrollToAsync(_pendingFragment);
        else if (_pendingScroll > 0) await ScriptAsync($"window.scrollTo(0,{_pendingScroll.ToString(CultureInfo.InvariantCulture)})");
        _pendingFragment = null;
        _pendingScroll = 0;
    }

    private async Task FollowLinkAsync(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;
        // Native WebView2 drops arrive as file navigations. NavigationStarting always cancels
        // the browser navigation; only Markdown is opened through the normal file reader.
        if (uri.IsFile)
        {
            await OpenDocumentAsync(uri.LocalPath);
        }
        else if (uri.Host == "markdown.local")
        {
            var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            if (TryLocalPath(uri, out var path))
            {
                // A fragment-only href resolves against the directory base.
                if (Directory.Exists(path) && fragment.Length > 0) await ScrollToAsync(fragment);
                else if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase) && fragment.Length > 0) await ScrollToAsync(fragment);
                else if (MarkdownExtensions.Contains(Path.GetExtension(path))) await OpenDocumentAsync(path, fragment: fragment);
                else ShowNotice("This link isn't a Markdown file. Open the file from its folder instead.");
            }
            else if (fragment.Length > 0) await ScrollToAsync(fragment);
        }
        else if (uri.Scheme is "https" or "http" or "mailto")
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { ShowNotice("Could not open this link: " + ex.Message); }
        }
    }

    private async Task OpenDocumentAsync(string path, bool addHistory = true, string? fragment = null, bool preserveScroll = false)
    {
        var version = ++_openVersion;
        try
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Copy this document to a local drive before opening it. Network shares are not supported.");
            if (!MarkdownExtensions.Contains(Path.GetExtension(path))) throw new IOException("Choose a .md, .markdown, .mdown, or .mkd file.");
            if (_isEditing && !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!await SaveEditorAsync()) return;
                LeaveEditMode();
            }
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The file no longer exists or could not be found.");
            if (info.Length > 10 * 1024 * 1024) throw new IOException("This file is larger than the 10 MB reading limit.");
            Welcome.Visibility = Visibility.Collapsed;
            Reader.Visibility = Visibility.Visible;
            await EnsureReaderAsync();
            double scroll = 0;
            if (preserveScroll && _currentPath is not null) double.TryParse(await ScriptAsync("window.scrollY"), CultureInfo.InvariantCulture, out scroll);
            var sourceFile = await MarkdownTextFile.ReadAsync(path);
            var markdown = sourceFile.Text;
            var darkMode = _settings.DarkMode;
            var rendered = await Task.Run(() => MarkdownRenderer.Render(markdown, darkMode));
            if (version != _openVersion || _closed) return;
            var directory = Path.GetDirectoryName(path)!;
            var directoryUri = new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri.Replace("file:///", MarkdownRenderer.LocalOrigin, StringComparison.Ordinal);
            _documentHtml = rendered.Html.Replace($"<base href=\"{MarkdownRenderer.LocalOrigin}\">", $"<base href=\"{WebUtility.HtmlEncode(directoryUri)}\">", StringComparison.Ordinal);
            _currentPath = path;
            _sourceFile = sourceFile;
            _currentMarkdown = markdown;
            _isDirty = false;
            _externalConflict = false;
            _pendingFragment = fragment;
            _pendingScroll = scroll;
            _navigationUri = $"{MarkdownRenderer.LocalOrigin}__viewer_document__.html?v={version}";
            Reader.CoreWebView2.Navigate(_navigationUri);
            Reader.Visibility = Visibility.Visible;
            Welcome.Visibility = Visibility.Collapsed;
            DocumentName.Text = Path.GetFileName(path);
            Title = $"{Path.GetFileName(path)} — Markdown Viewer";
            StatusText.Text = path;
            StatusText.ToolTip = path;
            DocumentStats.Text = $"{rendered.WordCount:N0} words   ·   {Math.Max(1, (int)Math.Ceiling(rendered.WordCount / 220d))} min read   ·   Auto-reload on";
            Notice.Visibility = Visibility.Collapsed;
            FindResult.Text = "";
            PopulateOutline(rendered.Headings);
            ModeButton.IsEnabled = true;
            SaveButton.IsEnabled = false;
            if (addHistory)
            {
                if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(path);
                _historyIndex = _history.Count - 1;
            }
            UpdateHistoryButtons();
            _settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.RecentFiles.Insert(0, path);
            _settings.RecentFiles = _settings.RecentFiles.Take(12).ToList();
            _settings.Save();
            WatchFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or COMException or WebView2RuntimeNotFoundException)
        {
            if (version == _openVersion)
            {
                if (_currentPath is null) { Reader.Visibility = Visibility.Collapsed; Welcome.Visibility = Visibility.Visible; }
                ShowNotice($"Could not open “{Path.GetFileName(path)}”. {ex.Message}");
            }
        }
    }

    private void PopulateOutline(IReadOnlyList<Heading> headings)
    {
        OutlineList.Items.Clear();
        var minimum = headings.Count == 0 ? 1 : headings.Min(h => h.Level);
        foreach (var heading in headings)
            OutlineList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = heading.Text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness((heading.Level - minimum) * 12, 0, 0, 0) },
                Tag = heading.Id, ToolTip = heading.Text
            });
        OutlineEmpty.Text = "This document has no headings.";
        OutlineEmpty.Visibility = headings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing) _ = ReturnToReadingModeAsync();
        else EnterEditMode();
    }

    private void EnterEditMode()
    {
        if (_currentPath is null || _sourceFile is null) return;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_currentPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice("Could not check whether this file can be edited. " + ex.Message);
            return;
        }
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            ShowNotice("This file is read-only. Clear its Read-only setting in File Properties to edit it.");
            return;
        }

        _isEditing = true;
        _externalConflict = false;
        _isLoadingEditor = true;
        Editor.Text = _currentMarkdown;
        _isLoadingEditor = false;
        EditorPanel.Visibility = Visibility.Visible;
        Reader.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Collapsed;
        ModeButton.Content = "Read";
        ModeButton.ToolTip = "Return to the rendered document";
        AutomationProperties.SetName(ModeButton, "Return to reading mode");
        SaveButton.IsEnabled = true;
        DocumentName.Text = Path.GetFileName(_currentPath) + " · Editing";
        DocumentStats.Text = "Markdown source   ·   Auto-saves after a short pause";
        StatusText.Text = "Editing · changes save automatically while you type";
        StatusText.ToolTip = _currentPath;
        FindResult.Text = "";
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
        Editor.CaretIndex = 0;
    }

    private async Task ReturnToReadingModeAsync()
    {
        if (_currentPath is null || !await SaveEditorAsync()) return;
        var path = _currentPath;
        LeaveEditMode();
        await OpenDocumentAsync(path, addHistory: false, preserveScroll: true);
    }

    private void LeaveEditMode()
    {
        _autoSaveTimer.Stop();
        _isEditing = false;
        _isDirty = false;
        _externalConflict = false;
        EditorPanel.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        ModeButton.Content = "Edit";
        ModeButton.ToolTip = "Edit this Markdown document";
        AutomationProperties.SetName(ModeButton, "Edit document");
        SaveButton.IsEnabled = false;
        if (_currentPath is not null)
        {
            DocumentName.Text = Path.GetFileName(_currentPath);
            StatusText.Text = _currentPath;
            StatusText.ToolTip = _currentPath;
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isEditing || _isLoadingEditor) return;
        _editorRevision++;
        _isDirty = _sourceFile is null || !MarkdownTextFile.ContentEquals(Editor.Text, _currentMarkdown);
        if (!_isDirty)
        {
            _autoSaveTimer.Stop();
            if (!_externalConflict) StatusText.Text = "All changes saved";
            return;
        }

        if (_externalConflict)
        {
            StatusText.Text = "The file changed elsewhere · review before saving";
            return;
        }
        StatusText.Text = "Unsaved changes · saving shortly…";
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async Task<bool> SaveEditorAsync(bool force = false)
    {
        if (!_isEditing || !_isDirty) return true;
        if (_isSaving || _sourceFile is null || _currentPath is null) return false;
        if (_externalConflict)
        {
            if (!force) return false;
            var answer = MessageBox.Show(this,
                "This file changed in another window after your copy was opened. Replace that version with your edits?",
                "Save over the newer file?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;
        }

        var path = _currentPath;
        var revision = _editorRevision;
        var editorText = Editor.Text;
        var updatedSource = WithEditorLineEndings(editorText, _sourceFile.NewLine);
        var sourceFile = _sourceFile;
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        _isSaving = true;
        _autoSaveTimer.Stop();
        try
        {
            if (!File.Exists(path))
            {
                _externalConflict = true;
                ShowExternalConflict("The file was removed or renamed. Your edits are still here.");
                return false;
            }
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly)) throw new UnauthorizedAccessException("The file has become read-only.");

            var beforeWrite = await MarkdownTextFile.ReadAsync(path);
            if (!MarkdownTextFile.ContentEquals(beforeWrite.Text, sourceFile.Text)
                && !MarkdownTextFile.ContentEquals(beforeWrite.Text, editorText))
            {
                _externalConflict = true;
                if (!force)
                {
                    _autoSaveTimer.Stop();
                    ShowExternalConflict("This file changed in another window. Your edits are still here.");
                    return false;
                }
                if (MessageBox.Show(this,
                    "This file changed in another window after your copy was opened. Replace that version with your edits?",
                    "Save over the newer file?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    _autoSaveTimer.Stop();
                    ShowExternalConflict("This file changed in another window. Your edits are still here.");
                    return false;
                }
                _externalConflict = false;
            }

            await File.WriteAllBytesAsync(temporaryPath, sourceFile.Encode(editorText));
            if (!File.Exists(path))
            {
                _externalConflict = true;
                ShowExternalConflict("The file was removed while you were editing. Your edits are still here.");
                return false;
            }
            var immediatelyBeforeReplace = await MarkdownTextFile.ReadAsync(path);
            if (!MarkdownTextFile.ContentEquals(immediatelyBeforeReplace.Text, sourceFile.Text)
                && !MarkdownTextFile.ContentEquals(immediatelyBeforeReplace.Text, editorText))
            {
                _externalConflict = true;
                if (!force || MessageBox.Show(this,
                    "The file changed again while saving. Replace the newest version with your edits?",
                    "Save over the newer file?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    _autoSaveTimer.Stop();
                    ShowExternalConflict("The file changed again while saving. Your edits are still here.");
                    return false;
                }
                _externalConflict = false;
            }
            File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);

            _sourceFile = new MarkdownTextFile(updatedSource, sourceFile.Encoding, sourceFile.HasPreamble, sourceFile.NewLine);
            _currentMarkdown = updatedSource;
            _externalConflict = false;
            var updated = await Task.Run(() => MarkdownRenderer.Render(updatedSource, _settings.DarkMode));
            PopulateOutline(updated.Headings);
            DocumentStats.Text = $"{updated.WordCount:N0} words   ·   Editing   ·   Auto-saves after a short pause";
            if (revision == _editorRevision && MarkdownTextFile.ContentEquals(Editor.Text, updatedSource))
            {
                _isDirty = false;
                StatusText.Text = "Saved";
            }
            else
            {
                _isDirty = true;
                StatusText.Text = "Unsaved changes · saving shortly…";
                _autoSaveTimer.Start();
            }
            Notice.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowNotice($"Could not save “{Path.GetFileName(path)}”. {ex.Message} Your edits are still here.");
            StatusText.Text = "Save failed · press Ctrl+S to try again";
            return false;
        }
        finally
        {
            _isSaving = false;
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private static string WithEditorLineEndings(string text, string lineEnding)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return lineEnding == "\n" ? normalized : normalized.Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    private void ShowExternalConflict(string message)
    {
        NoticeText.Text = message + " Press Ctrl+S to review and explicitly save over it.";
        Notice.Visibility = Visibility.Visible;
        StatusText.Text = "The file changed elsewhere · your edits are kept here";
    }

    private async Task CheckExternalChangeAsync()
    {
        if (!_isEditing || _currentPath is null || _sourceFile is null) return;
        var path = _currentPath;
        try
        {
            if (!File.Exists(path))
            {
                _externalConflict = true;
                ShowExternalConflict("The file was removed or renamed. Your edits are still here.");
                _autoSaveTimer.Stop();
                return;
            }
            var latest = await MarkdownTextFile.ReadAsync(path);
            if (!_isEditing || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return;
            if (MarkdownTextFile.ContentEquals(latest.Text, _sourceFile.Text)) return;

            if (!_isDirty || MarkdownTextFile.ContentEquals(latest.Text, Editor.Text))
            {
                _sourceFile = latest;
                _currentMarkdown = latest.Text;
                _isLoadingEditor = true;
                Editor.Text = latest.Text;
                _isLoadingEditor = false;
                _isDirty = false;
                _externalConflict = false;
                var rendered = await Task.Run(() => MarkdownRenderer.Render(latest.Text, _settings.DarkMode));
                PopulateOutline(rendered.Headings);
                DocumentStats.Text = $"{rendered.WordCount:N0} words   ·   Editing   ·   Auto-saves after a short pause";
                StatusText.Text = "Updated from disk";
                return;
            }

            _externalConflict = true;
            _autoSaveTimer.Stop();
            ShowExternalConflict("This file changed in another window. Your edits are still here.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice("Could not check for changes on disk. Your edits are still here. " + ex.Message);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveEditorAsync(force: true);

    private void WatchFile(string path)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += FileChanged;
            _watcher.Created += FileChanged;
            _watcher.Deleted += FileChanged;
            _watcher.Renamed += FileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { DocumentStats.Text = DocumentStats.Text.Replace("Auto-reload on", "F5 to reload"); }
        catch (ArgumentException) { DocumentStats.Text = DocumentStats.Text.Replace("Auto-reload on", "F5 to reload"); }
    }

    private void FileChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed || !string.Equals(e.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _reloadTimer.Stop();
        if (_isEditing) _ = CheckExternalChangeAsync();
        else _reloadTimer.Start();
    });

    private async Task<string> ScriptAsync(string script)
    {
        if (Reader.CoreWebView2 is null || _closed) return "null";
        try { return await Reader.ExecuteScriptAsync(script); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return "null"; }
    }

    private Task<string> ScrollToAsync(string id) => ScriptAsync($"document.getElementById({JsonSerializer.Serialize(id)})?.scrollIntoView({{behavior:'instant',block:'start'}})");

    private async void Open_Click(object sender, RoutedEventArgs e) => await ChooseFileAsync();
    private async Task ChooseFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "Open Markdown", Filter = "Markdown documents|*.md;*.markdown;*.mdown;*.mkd|All files|*.*", CheckFileExists = true };
        if (_currentPath is not null) dialog.InitialDirectory = Path.GetDirectoryName(_currentPath);
        if (dialog.ShowDialog(this) == true) await OpenDocumentAsync(dialog.FileName);
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var path in _settings.RecentFiles.ToArray())
        {
            var item = new MenuItem { Header = Path.GetFileName(path).Replace("_", "__"), ToolTip = path };
            item.Click += async (_, _) => await OpenDocumentAsync(path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent files yet", IsEnabled = false });
        menu.PlacementTarget = RecentButton;
        menu.IsOpen = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Item(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("Reload\tF5", () => { if (_currentPath is not null && !_isEditing) _ = OpenDocumentAsync(_currentPath, false, preserveScroll: true); }, _currentPath is not null && !_isEditing);
        Item("Print…\tCtrl+P", Print, _currentPath is not null && !_isEditing);
        Item("Show in folder", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowNotice(ex.Message); }
        }, _currentPath is not null);
        menu.Items.Add(new Separator());
        Item("Default app settings…", () =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=MarkdownViewer") { UseShellExecute = true }); }
            catch (Exception ex) { ShowNotice(ex.Message); }
        });
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
    }

    private void Print()
    {
        if (_currentPath is null) return;
        try { Reader.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System); }
        catch (Exception ex) { ShowNotice("Could not open printing: " + ex.Message); }
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(-1);
    private async void Forward_Click(object sender, RoutedEventArgs e) => await NavigateHistoryAsync(1);
    private async Task NavigateHistoryAsync(int direction)
    {
        var target = _historyIndex + direction;
        if (target < 0 || target >= _history.Count) return;
        var prior = _historyIndex;
        var targetPath = _history[target];
        var expectedVersion = _openVersion + 1;
        _historyIndex = target;
        await OpenDocumentAsync(targetPath, false);
        if (_openVersion != expectedVersion) return;
        if (!string.Equals(_currentPath, targetPath, StringComparison.OrdinalIgnoreCase)) _historyIndex = prior;
        UpdateHistoryButtons();
    }
    private void UpdateHistoryButtons() { BackButton.IsEnabled = _historyIndex > 0; ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1; }
    private async void Outline_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (OutlineList.SelectedItem is ListBoxItem { Tag: string id }) await ScrollToAsync(id); }
    private void Outline_Click(object sender, RoutedEventArgs e) { _settings.ShowOutline = !_settings.ShowOutline; ApplyOutline(); }
    private void ApplyOutline() { SidebarColumn.Width = new GridLength(_settings.ShowOutline ? 236 : 0); Sidebar.Visibility = _settings.ShowOutline ? Visibility.Visible : Visibility.Collapsed; }
    private void Find_Click(object sender, RoutedEventArgs e) => ShowFind();
    private void ShowFind() { FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); }
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();
    private void CloseFind() { FindBar.Visibility = Visibility.Collapsed; if (_isEditing) Editor.Focus(); else Reader.Focus(); }
    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindAsync(false);
    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindAsync(true);
    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) { if (FindResult is not null) FindResult.Text = ""; }
    private async void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await FindAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
    }
    private async Task FindAsync(bool backwards)
    {
        if (string.IsNullOrEmpty(FindBox.Text) || _currentPath is null) return;
        if (_isEditing)
        {
            var text = Editor.Text;
            var needle = FindBox.Text;
            var start = backwards ? Math.Max(0, Editor.SelectionStart - 1) : Editor.SelectionStart + Editor.SelectionLength;
            var match = backwards
                ? (start > 0 ? text.LastIndexOf(needle, start - 1, StringComparison.OrdinalIgnoreCase) : -1)
                : text.IndexOf(needle, Math.Min(start, text.Length), StringComparison.OrdinalIgnoreCase);
            if (match < 0)
                match = backwards
                    ? text.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase)
                    : text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (match >= 0)
            {
                Editor.Focus();
                Editor.Select(match, needle.Length);
                Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(match));
                FindResult.Text = "Match found";
            }
            else FindResult.Text = "No matches";
            return;
        }
        var result = await ScriptAsync($"window.find({JsonSerializer.Serialize(FindBox.Text)},false,{(backwards ? "true" : "false")},true,false,false,false)");
        FindResult.Text = result == "true" ? "Match found" : "No matches";
    }
    private async void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.DarkMode = !_settings.DarkMode;
        ApplyTheme();
        await ScriptAsync($"document.documentElement.dataset.theme='{(_settings.DarkMode ? "dark" : "light")}'");
    }
    private void ApplyTheme()
    {
        string[] names = ["Surface", "Panel", "Ink", "Muted", "Line", "Hover", "Accent"];
        string[] colors = _settings.DarkMode
            ? ["#202421", "#252B26", "#E5E9E3", "#A0AAA0", "#3A423B", "#334136", "#8DCEB1"]
            : ["#FFFEFA", "#F5F6F3", "#252C29", "#69736C", "#E4E8E2", "#E8EDE7", "#376A54"];
        for (var i = 0; i < names.Length; i++) Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Background = (Brush)Resources["Surface"];
        Foreground = (Brush)Resources["Ink"];
        Reader.DefaultBackgroundColor = _settings.DarkMode ? System.Drawing.Color.FromArgb(32, 36, 33) : System.Drawing.Color.FromArgb(255, 254, 250);
        ThemeButton.Content = _settings.DarkMode ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = _settings.DarkMode ? "Switch to light theme" : "Switch to dark theme";
        ApplyTitleBarTheme();
    }
    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = _settings.DarkMode ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_settings.Zoom + .1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_settings.Zoom - .1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1);
    private void SetZoom(double zoom) { _settings.Zoom = Math.Clamp(Math.Round(zoom, 2), .5, 2.5); UpdateZoom(); }
    private void UpdateZoom() { ZoomButton.Content = $"{_settings.Zoom:P0}"; if (Reader.CoreWebView2 is not null) Reader.ZoomFactor = _settings.Zoom; }
    private void ShowNotice(string text) { NoticeText.Text = text; Notice.Visibility = Visibility.Visible; }
    private void NoticeClose_Click(object sender, RoutedEventArgs e) => Notice.Visibility = Visibility.Collapsed;

    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await OpenDocumentAsync(files[0]);
        e.Handled = true;
    }
    private static bool IsAppShortcut(Key key, ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Control) && key is Key.O or Key.F or Key.B or Key.P or Key.S or Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus or Key.D0 or Key.NumPad0)
        || (modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right)
        || key is Key.F5 or Key.F3 or Key.Escape;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsAppShortcut(key, Keyboard.Modifiers)) return;
        e.Handled = true;
        var modifiers = Keyboard.Modifiers;
        // WebView2 forwards keys synchronously; open modal dialogs after its callback returns.
        Dispatcher.BeginInvoke(() => HandleShortcut(key, modifiers));
    }
    private async void HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (key)
            {
                case Key.O: await ChooseFileAsync(); break;
                case Key.F: ShowFind(); break;
                case Key.B: Outline_Click(this, new RoutedEventArgs()); break;
                case Key.P: Print(); break;
                case Key.S: if (_isEditing) await SaveEditorAsync(force: true); break;
                case Key.Add: case Key.OemPlus: SetZoom(_settings.Zoom + .1); break;
                case Key.Subtract: case Key.OemMinus: SetZoom(_settings.Zoom - .1); break;
                case Key.D0: case Key.NumPad0: SetZoom(1); break;
            }
        }
        else if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            if (key == Key.Left) await NavigateHistoryAsync(-1);
            if (key == Key.Right) await NavigateHistoryAsync(1);
        }
        else if (key == Key.F5 && _currentPath is not null && !_isEditing) await OpenDocumentAsync(_currentPath, false, preserveScroll: true);
        else if (key == Key.F3) { ShowFind(); await FindAsync(modifiers.HasFlag(ModifierKeys.Shift)); }
        else if (key == Key.Escape) CloseFind();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isEditing && _isDirty && !_allowClose)
        {
            var answer = MessageBox.Show(this,
                "Save your Markdown changes before closing?",
                "Unsaved Markdown changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            e.Cancel = true;
            if (answer == MessageBoxResult.Yes && !await SaveEditorAsync(force: true)) return;
            _allowClose = true;
            Close();
            return;
        }
        _closed = true;
        _openVersion++;
        _reloadTimer.Stop();
        _autoSaveTimer.Stop();
        _watcher?.Dispose();
        _settings.Save();
        Reader.Dispose();
    }
}
