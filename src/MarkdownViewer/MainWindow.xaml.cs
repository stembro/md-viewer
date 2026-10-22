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
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
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
    private byte[] _documentBytes = [];
    private RenderedDocument? _renderedDocument;
    private string? _renderedMarkdown;
    private IReadOnlyList<Heading>? _outlineHeadings;
    private bool _readerHasDocument;
    private bool _pendingPageDarkMode;
    private static Task<CoreWebView2Environment>? _environment;
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

    public MainWindow(string? initialPath, bool restoreWorkspace = true, string[]? additionalPaths = null)
    {
        _initialPath = initialPath;
        _restoreWorkspace = restoreWorkspace;
        _additionalPaths = additionalPaths ?? [];
        InitializeComponent();
        TabList.ItemsSource = _tabs;
        ApplyTheme();
        ApplySidebars();
        InitializeFolderBrowser();
        UpdateZoom();
        _reloadTimer.Tick += async (_, _) =>
        {
            _reloadTimer.Stop();
            await ReloadActiveDocumentAsync();
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
        StartupMetrics.Mark("windowLoadedMs");
        try
        {
            await RestoreWorkspaceAsync();
            if (_initialPath is not null && Directory.Exists(_initialPath)) await Task.WhenAll(SetFolderAsync(_initialPath), EnsureReaderAsync());
            else if (_initialPath is not null) await OpenDocumentAsync(_initialPath);
            else await EnsureReaderAsync();
            foreach (var path in _additionalPaths) await OpenDocumentAsync(path);
            if (_folderRoot is null && _settings.LastFolder is { } folder && StartupMetrics.ReportPath is null)
                _ = SetFolderAsync(folder, restore: true);
            SaveWorkspace();
        }
        catch (Exception ex) { ShowNotice("The reading engine could not start. Install or repair Microsoft Edge WebView2 Runtime. " + ex.Message); }
    }

    private Task EnsureReaderAsync() => _initialization ??= InitializeReaderAsync();

    private async Task InitializeReaderAsync()
    {
        var environment = await (_environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(UserSettings.DataDirectory, "WebView2")));
        if (_closed) return;
        await Reader.EnsureCoreWebView2Async(environment);
        if (_closed) return;
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
        StartupMetrics.Mark("browserReadyMs");
    }

    private async void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.Scheme == "https" && uri.Host == MarkdownRenderer.LocalHost)
            {
                if (uri.AbsolutePath == "/__viewer_document__.html")
                {
                    e.Response = Response(_documentBytes, "text/html; charset=utf-8");
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
        if (uri.Scheme != "https" || uri.Host != MarkdownRenderer.LocalHost) return false;
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
        if (e.Uri == _navigationUri) { _pageNavigationId = e.NavigationId; return; }
        e.Cancel = true;
        if (e.IsUserInitiated) _ = FollowLinkAsync(e.Uri);
    }

    private async void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId != _pageNavigationId || _closed) return;
        if (!e.IsSuccess) { _pageReady?.TrySetException(new IOException("The document could not be displayed.")); return; }
        _readerHasDocument = true;
        if (_pendingPageDarkMode != _settings.DarkMode)
            await ScriptAsync($"document.documentElement.dataset.theme='{(_settings.DarkMode ? "dark" : "light")}'");
        if (!string.IsNullOrEmpty(_pendingFragment)) await ScrollToAsync(_pendingFragment);
        else if (_pendingScroll > 0) await ScriptAsync($"window.scrollTo(0,{_pendingScroll.ToString(CultureInfo.InvariantCulture)})");
        _pendingFragment = null;
        _pendingScroll = 0;
        _pageReady?.TrySetResult();
        if (StartupMetrics.ReportPath is not null)
        {
            StartupMetrics.Complete();
            Close();
        }
    }

    private async Task FollowLinkAsync(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;
        // Native WebView2 drops arrive as file navigations. NavigationStarting always cancels
        // the browser navigation; only Markdown is opened through the normal file reader.
        if (uri.IsFile)
        {
            if (Directory.Exists(uri.LocalPath)) await SetFolderAsync(uri.LocalPath);
            else await OpenDocumentAsync(uri.LocalPath);
        }
        else if (uri.Host == MarkdownRenderer.LocalHost)
        {
            var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            if (TryLocalPath(uri, out var path))
            {
                // A fragment-only href resolves against the directory base.
                if (Directory.Exists(path) && fragment.Length > 0) await ScrollToAsync(fragment);
                else if (Directory.Exists(path)) await SetFolderAsync(path);
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

    private async Task<bool> OpenDocumentCoreAsync(string path, bool addHistory = true, string? fragment = null, bool preserveScroll = false, bool skipUnchanged = false, bool reusePage = false, double? restoreScroll = null)
    {
        var version = ++_openVersion;
        try
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Copy this document to a local drive before opening it. Network shares are not supported.");
            if (!MarkdownExtensions.Contains(Path.GetExtension(path))) throw new IOException("Choose a .md, .markdown, .mdown, or .mkd file.");
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The file no longer exists or could not be found.");
            if (info.Length > 10 * 1024 * 1024) throw new IOException("This file is larger than the 10 MB reading limit.");
            ModeButton.IsEnabled = false;
            Welcome.Visibility = Visibility.Collapsed;
            Reader.Visibility = Visibility.Visible;
            var samePath = string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);
            var priorMarkdown = _currentMarkdown;
            var cachedMarkdown = _renderedMarkdown;
            var cachedDocument = _renderedDocument;
            var darkMode = _settings.DarkMode;
            var canUpdatePage = _readerHasDocument;
            var sameReader = string.Equals(_readerPath, path, StringComparison.OrdinalIgnoreCase);
            // File decoding, parsing, HTML encoding and browser startup run concurrently.
            var prepare = Task.Run(async () =>
            {
                var source = await MarkdownTextFile.ReadAsync(path);
                StartupMetrics.Mark("fileReadMs");
                if (skipUnchanged && samePath && source.Text == priorMarkdown)
                    return new PreparedDocument(source, null, [], null);
                var rendered = cachedDocument is not null && source.Text == cachedMarkdown
                    ? cachedDocument : MarkdownRenderer.Render(source.Text, darkMode);
                StartupMetrics.Mark("markdownRenderedMs");
                var directory = Path.GetDirectoryName(path)!;
                var directoryUri = new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri.Replace("file:///", MarkdownRenderer.LocalOrigin, StringComparison.Ordinal);
                var html = MarkdownRenderer.SetPageTheme(rendered.Html, darkMode)
                    .Replace($"<base href=\"{MarkdownRenderer.LocalOrigin}\">", $"<base href=\"{WebUtility.HtmlEncode(directoryUri)}\">", StringComparison.Ordinal);
                string? updateScript = null;
                if (canUpdatePage && rendered.BodyHtml.Length <= 512 * 1024)
                {
                    // Only the host executes this script. Markdown HTML and scripts remain disabled.
                    updateScript = $$"""
                        (() => {
                          const main = document.getElementById('document');
                          if (!main) return false;
                          const y = {{(restoreScroll?.ToString(CultureInfo.InvariantCulture) ?? (preserveScroll && sameReader ? "window.scrollY" : "0"))}};
                          document.querySelector('base').href = {{JsonSerializer.Serialize(directoryUri)}};
                          main.innerHTML = {{JsonSerializer.Serialize(rendered.BodyHtml)}};
                          document.documentElement.dataset.theme = '{{(darkMode ? "dark" : "light")}}';
                          const id = {{JsonSerializer.Serialize(fragment)}};
                          if (id) document.getElementById(id)?.scrollIntoView({block:'start'});
                          else window.scrollTo(0, y);
                          return true;
                        })()
                        """;
                }
                return new PreparedDocument(source, rendered, Encoding.UTF8.GetBytes(html), updateScript,
                    reusePage && sameReader && canUpdatePage && ReferenceEquals(cachedDocument, rendered));
            });
            var initialize = EnsureReaderAsync();
            await Task.WhenAll(prepare, initialize);
            if (version != _openVersion || _closed) return false;
            var prepared = await prepare;
            var sourceFile = prepared.Source;
            var rendered = prepared.Rendered;
            if (rendered is null)
            {
                _sourceFile = sourceFile;
                return true;
            }
            _documentBytes = prepared.Bytes;
            _currentPath = path;
            _sourceFile = sourceFile;
            _currentMarkdown = sourceFile.Text;
            _renderedMarkdown = sourceFile.Text;
            _renderedDocument = rendered;
            _isDirty = false;
            _externalConflict = false;
            var updatedPage = prepared.ReusePage || (prepared.UpdateScript is not null && await ScriptAsync(prepared.UpdateScript) == "true");
            if (version != _openVersion || _closed) return false;
            if (!updatedPage)
            {
                double scroll = 0;
                if (preserveScroll && samePath) double.TryParse(await ScriptAsync("window.scrollY"), CultureInfo.InvariantCulture, out scroll);
                if (version != _openVersion || _closed) return false;
                _pendingFragment = fragment;
                _pendingScroll = restoreScroll ?? scroll;
                _readerHasDocument = false;
                _pendingPageDarkMode = darkMode;
                _navigationUri = $"{MarkdownRenderer.LocalOrigin}__viewer_document__.html?v={version}";
                _pageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Reader.CoreWebView2.Navigate(_navigationUri);
                await _pageReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (version != _openVersion || _closed) return false;
            }
            else if (darkMode != _settings.DarkMode)
                await ScriptAsync($"document.documentElement.dataset.theme='{(_settings.DarkMode ? "dark" : "light")}'");
            Reader.Visibility = Visibility.Visible;
            Welcome.Visibility = Visibility.Collapsed;
            DocumentName.Text = Path.GetFileName(path);
            _readerPath = path;
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
            if (addHistory || !samePath)
            {
                _settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                _settings.RecentFiles.Insert(0, path);
                _settings.RecentFiles = _settings.RecentFiles.Take(12).ToList();
                _settings.Save();
            }
            WatchFile(path);
            _ = RevealCurrentDocumentAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or COMException or WebView2RuntimeNotFoundException or TimeoutException)
        {
            if (version == _openVersion)
            {
                if (_currentPath is null) { Reader.Visibility = Visibility.Collapsed; Welcome.Visibility = Visibility.Visible; }
                ShowNotice($"Could not open “{Path.GetFileName(path)}”. {ex.Message}");
            }
            return false;
        }
        finally
        {
            if (version == _openVersion && !_closed) ModeButton.IsEnabled = _currentPath is not null;
        }
    }

    private sealed record PreparedDocument(MarkdownTextFile Source, RenderedDocument? Rendered, byte[] Bytes, string? UpdateScript, bool ReusePage = false);
    private sealed record OutlineItem(string Id, string Text, Thickness Indent);

    private void PopulateOutline(IReadOnlyList<Heading> headings)
    {
        if (ReferenceEquals(headings, _outlineHeadings)) return;
        _outlineHeadings = headings;
        var minimum = headings.Count == 0 ? 1 : headings.Min(h => h.Level);
        OutlineList.ItemsSource = headings.Select(h => new OutlineItem(h.Id, h.Text, new Thickness((h.Level - minimum) * 12, 0, 0, 0))).ToArray();
        OutlineEmpty.Text = "This document has no headings.";
        OutlineEmpty.Visibility = headings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingDocuments || _closingWorkspace) return;
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

        _ = CaptureActiveTabAsync();
        _isEditing = true;
        _externalConflict = false;
        if (_activeTab is not { } tab) return;
        if (tab.EditorDocument is not null && !MarkdownTextFile.ContentEquals(tab.EditorDocument.Text, _currentMarkdown)) tab.EditorDocument = null;
        BindTabEditor(tab);
        ShowEditor();
        UpdateTabStatus();
        SaveWorkspace();
    }

    private void ShowEditor()
    {
        ModeButton.IsEnabled = true;
        EditorPanel.Visibility = Visibility.Visible;
        Reader.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Collapsed;
        ModeButton.Content = "Read";
        ModeButton.ToolTip = "Return to the rendered document";
        AutomationProperties.SetName(ModeButton, "Return to reading mode");
        SaveButton.IsEnabled = true;
        OutlineList.IsEnabled = false;
        DocumentName.Text = Path.GetFileName(_currentPath) + " · Editing";
        Title = $"{Path.GetFileName(_currentPath)} — Markdown Viewer";
        DocumentStats.Text = "Markdown source   ·   Auto-saves after a short pause";
        StatusText.Text = _externalConflict ? "The file changed elsewhere · your draft is kept in this tab" : _isDirty ? "Unsaved changes · saving shortly…" : "Editing · changes save automatically while you type";
        StatusText.ToolTip = _currentPath;
        FindResult.Text = "";
        FindBar.Visibility = Visibility.Collapsed;
        FocusActiveDocument();
        if (_externalConflict) ShowExternalConflict("This file changed elsewhere. Your edits are still here.");
    }

    private async Task ReturnToReadingModeAsync()
    {
        await _documentGate.WaitAsync();
        try
        {
            if (_currentPath is null || !await SaveEditorAsync()) return;
            var path = _currentPath;
            LeaveEditMode();
            if (!await OpenDocumentCoreAsync(path, addHistory: false, preserveScroll: true, reusePage: true, restoreScroll: _activeTab?.Position.ReadScroll))
            {
                _isEditing = true;
                ShowEditor();
            }
            UpdateTabStatus(); SaveWorkspace();
        }
        finally { _documentGate.Release(); }
    }

    private void LeaveEditMode()
    {
        CaptureActiveMetadata();
        _autoSaveTimer.Stop();
        _isEditing = false;
        _isDirty = false;
        _externalConflict = false;
        ShowReader();
        UpdateTabStatus();
        SaveWorkspace();
    }

    private void ShowReader()
    {
        EditorPanel.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        ModeButton.Content = "Edit";
        ModeButton.ToolTip = "Edit this Markdown document";
        AutomationProperties.SetName(ModeButton, "Edit document");
        SaveButton.IsEnabled = false;
        OutlineList.IsEnabled = true;
        if (_currentPath is not null)
        {
            DocumentName.Text = Path.GetFileName(_currentPath);
            StatusText.Text = _currentPath;
            StatusText.ToolTip = _currentPath;
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (!_isEditing || _isLoadingEditor) return;
        _editorRevision++;
        // Text materializes the entire buffer. Inspect it once at save time,
        // rather than copying and normalizing a large document on every keystroke.
        _isDirty = true;
        UpdateTabStatus();
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
        if (_savingTask is not null) await _savingTask;
        var task = SaveEditorCoreAsync(force);
        _savingTask = task;
        try { return await task; }
        finally { if (ReferenceEquals(_savingTask, task)) _savingTask = null; UpdateTabStatus(); }
    }

    private async Task<bool> SaveEditorCoreAsync(bool force = false)
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
        var sourceFile = _sourceFile;
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        _isSaving = true;
        _autoSaveTimer.Stop();
        try
        {
            if (await Task.Run(() => MarkdownTextFile.ContentEquals(editorText, sourceFile.Text)))
            {
                _isDirty = revision != _editorRevision;
                if (_isDirty) _autoSaveTimer.Start();
                else StatusText.Text = "All changes saved";
                return !_isDirty;
            }
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

            var updatedSource = await Task.Run(async () =>
            {
                var bytes = sourceFile.Encode(editorText);
                await File.WriteAllBytesAsync(temporaryPath, bytes);
                return WithEditorLineEndings(editorText, sourceFile.NewLine);
            });
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
            // The reader is hidden. Defer Markdown/HTML work until Read is selected.
            if (revision == _editorRevision)
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
            return !_isDirty;
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
        UpdateTabStatus();
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_switchingDocuments && !_closingWorkspace) await SaveEditorAsync(force: true);
    }

    private void WatchFile(string path)
    {
        if (_activeTab is not { } tab) return;
        if (tab.Watcher is not null) { _watcher = tab.Watcher; return; }
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            tab.Watcher = _watcher;
            void Changed(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
            {
                if (!_closed && ReferenceEquals(tab, _activeTab)) FileChanged(sender, e);
            });
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += Changed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { DocumentStats.Text = DocumentStats.Text.Replace("Auto-reload on", "F5 to reload"); }
        catch (ArgumentException) { DocumentStats.Text = DocumentStats.Text.Replace("Auto-reload on", "F5 to reload"); }
    }

    private void FileChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed || !string.Equals(e.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
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
        var dialog = new OpenFileDialog { Title = "Open Markdown", Filter = "Markdown documents|*.md;*.markdown;*.mdown;*.mkd|All files|*.*", CheckFileExists = true, Multiselect = true };
        if (_currentPath is not null) dialog.InitialDirectory = Path.GetDirectoryName(_currentPath);
        if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FileNames) await OpenDocumentAsync(path);
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
        Item("Browse this file's folder", () => { if (_currentPath is not null) _ = SetFolderAsync(Path.GetDirectoryName(_currentPath)!); }, _currentPath is not null);
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
        _historyIndex = target;
        await OpenDocumentAsync(targetPath, false);
        if (!string.Equals(_currentPath, targetPath, StringComparison.OrdinalIgnoreCase)) _historyIndex = prior;
        UpdateHistoryButtons();
    }
    private void UpdateHistoryButtons() { BackButton.IsEnabled = _historyIndex > 0; ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1; }
    private async void Outline_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (OutlineList.SelectedItem is OutlineItem item && !_isEditing) await ScrollToAsync(item.Id); }
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
            var start = backwards ? Editor.SelectionStart : Editor.SelectionStart + Editor.SelectionLength;
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
                Editor.ScrollToLine(Editor.Document.GetLineByOffset(match).LineNumber);
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
        if (_editor is not null) ApplyEditorTheme(_editor);
        UpdateSidebarButtons();
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
    private void UpdateZoom() { ZoomButton.Content = $"{_settings.Zoom:P0}"; if (Reader.CoreWebView2 is not null) Reader.ZoomFactor = _settings.Zoom; if (_editor is not null) _editor.FontSize = 14 * _settings.Zoom; }
    private void ShowNotice(string text) { NoticeText.Text = text; Notice.Visibility = Visibility.Visible; }
    private void NoticeClose_Click(object sender, RoutedEventArgs e) => Notice.Visibility = Visibility.Collapsed;

    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            foreach (var path in files)
                if (Directory.Exists(path)) await SetFolderAsync(path);
                else await OpenDocumentAsync(path);
        }
        e.Handled = true;
    }
    private static bool IsAppShortcut(Key key, ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Control) && key is Key.O or Key.F or Key.B or Key.P or Key.S or Key.W or Key.Tab or Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus or Key.D0 or Key.NumPad0)
        || (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift) && key == Key.L)
        || (modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right)
        || key is Key.F5 or Key.F3 or Key.Escape;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleEditorShortcut(key, Keyboard.Modifiers)) { e.Handled = true; return; }
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
                case Key.O: if (modifiers.HasFlag(ModifierKeys.Shift)) await ChooseFolderAsync(); else await ChooseFileAsync(); break;
                case Key.F: ShowFind(); break;
                case Key.B: Files_Click(this, new RoutedEventArgs()); break;
                case Key.L: Outline_Click(this, new RoutedEventArgs()); break;
                case Key.P: Print(); break;
                case Key.S: if (_isEditing && !_switchingDocuments && !_closingWorkspace) await SaveEditorAsync(force: true); break;
                case Key.W: if (_activeTab is not null) await CloseTabAsync(_activeTab); break;
                case Key.Tab:
                    if (_tabs.Count > 1)
                    {
                        var index = (_tabs.IndexOf(_activeTab!) + (modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1) + _tabs.Count) % _tabs.Count;
                        await OpenDocumentAsync(_tabs[index].Path, false);
                    }
                    break;
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
        if (!_allowClose && StartupMetrics.ReportPath is null)
        {
            e.Cancel = true;
            if (_closingWorkspace) return;
            _closingWorkspace = true;
            DocumentSurface.IsEnabled = false;
            try
            {
                if (!await PrepareWorkspaceCloseAsync()) return;
                _allowClose = true;
                Close();
            }
            finally { _closingWorkspace = false; DocumentSurface.IsEnabled = true; }
            return;
        }
        _closed = true;
        _openVersion++;
        _reloadTimer.Stop();
        _autoSaveTimer.Stop();
        foreach (var tab in _tabs) tab.Watcher?.Dispose();
        DisposeFolderBrowser();
        _settings.Save();
        Reader.Dispose();
    }
}
