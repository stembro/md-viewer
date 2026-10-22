using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using MarkdownViewer.Rendering;

namespace MarkdownViewer;

public partial class MainWindow
{
    private sealed class DocumentTab(string path) : INotifyPropertyChanged
    {
        public string Path { get; private set; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public string Mode => IsEditing ? "Edit" : "";
        public string Indicator => ExternalConflict ? "!" : IsDirty ? "●" : "";
        public string Description => Path + (ExternalConflict ? "\nChanged elsewhere · your draft is kept in this tab" : IsDirty ? "\nUnsaved changes" : "");
        public bool IsEditing, IsDirty, ExternalConflict;
        public int EditorRevision;
        public MarkdownTextFile? Source;
        public string Markdown = "";
        public string? RenderedMarkdown;
        public RenderedDocument? Rendered;
        public TextDocument? EditorDocument;
        public WorkspaceDocument Position = new() { Path = path };
        public FileSystemWatcher? Watcher;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public void Rename(string newPath) { Path = newPath; Position.Path = newPath; Notify(); }
    }

    private readonly ObservableCollection<DocumentTab> _tabs = [];
    private readonly SemaphoreSlim _documentGate = new(1, 1);
    private DocumentTab? _activeTab;
    private bool _selectingTab, _switchingDocuments, _restoringWorkspace, _closingWorkspace;
    private readonly bool _restoreWorkspace;
    private readonly string[] _additionalPaths;
    private Task<bool>? _savingTask;
    private string? _readerPath;
    private TaskCompletionSource? _pageReady;
    private ulong _pageNavigationId;

    private async Task OpenDocumentAsync(string path, bool addHistory = true, string? fragment = null,
        bool preserveScroll = false, bool skipUnchanged = false, bool reusePage = false)
    {
        if (_closingWorkspace || _closed) return;
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { ShowNotice("Could not open this document. " + ex.Message); return; }
        await _documentGate.WaitAsync();
        try
        {
            if (_closed || _closingWorkspace) return;
            var tab = _tabs.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(tab, _activeTab) && tab is not null)
            {
                if (_isEditing) { await CheckExternalChangeAsync(); return; }
                if (addHistory && fragment is null && !preserveScroll && !skipUnchanged && !reusePage) return;
                await OpenDocumentCoreAsync(path, addHistory, fragment, preserveScroll, skipUnchanged, reusePage);
                UpdateTabStatus();
                return;
            }
            if (tab is null)
            {
                var info = new FileInfo(path);
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || !MarkdownExtensions.Contains(info.Extension))
                    throw new IOException("Choose a Markdown file on a local drive.");
                if (!info.Exists) throw new FileNotFoundException("The file no longer exists or could not be found.");
                if (info.Length > 10 * 1024 * 1024) throw new IOException("This file is larger than the 10 MB reading limit.");
                tab = new DocumentTab(path);
                _tabs.Add(tab);
            }
            await ActivateTabCoreAsync(tab, addHistory, fragment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ShowNotice($"Could not open “{Path.GetFileName(path)}”. {ex.Message}"); }
        finally { _documentGate.Release(); }
    }

    private async Task ActivateTabCoreAsync(DocumentTab tab, bool addHistory = false, string? fragment = null)
    {
        if (ReferenceEquals(tab, _activeTab)) return;
        _switchingDocuments = true;
        DocumentSurface.IsEnabled = false;
        try
        {
            // Await in-flight autosave before moving the shared controls. A failed
            // save keeps its buffer and conflict flag with the old tab.
            await SaveEditorAsync();
            await CaptureActiveTabAsync();
            var previous = _activeTab;
            RestoreTabFields(tab);
            SelectActiveTab();
            FindBar.Visibility = Visibility.Collapsed;
            Notice.Visibility = Visibility.Collapsed;
            if (tab.Source is not null && tab.IsEditing)
            {
                BindTabEditor(tab);
                ShowEditor();
                WatchFile(tab.Path);
                await CheckExternalChangeAsync();
                BindTabEditor(tab);
                if (_isDirty && !_externalConflict) _autoSaveTimer.Start();
                if (addHistory) AddTabHistory(tab.Path);
            }
            else
            {
                var editing = tab.IsEditing;
                _isEditing = false;
                if (!await OpenDocumentCoreAsync(tab.Path, addHistory, fragment, restoreScroll: tab.Position.ReadScroll))
                {
                    var message = NoticeText.Text;
                    _tabs.Remove(tab);
                    tab.Watcher?.Dispose();
                    _activeTab = null;
                    ResetDocumentFields();
                    if (previous is not null) await ActivateTabCoreAsync(previous);
                    else ResetEmptyWorkspace();
                    ShowNotice(message);
                    return;
                }
                if (editing) EnterEditMode();
                else ShowReader();
            }
            UpdateTabStatus();
            await RevealCurrentDocumentAsync();
            SaveWorkspace();
        }
        finally
        {
            _switchingDocuments = false;
            DocumentSurface.IsEnabled = !_closingWorkspace;
            if (!_closingWorkspace) FocusActiveDocument();
        }
    }

    private void FocusActiveDocument()
    {
        if (_isEditing && _editor is not null)
        {
            _editor.Focus();
            _editor.UpdateLayout();
            if (_activeTab is { } tab) _editor.ScrollToVerticalOffset(tab.Position.EditorScroll);
        }
        else if (_currentPath is not null) Reader.Focus();
    }

    private void RestoreTabFields(DocumentTab tab)
    {
        _autoSaveTimer.Stop();
        _reloadTimer.Stop();
        _activeTab = tab;
        _currentPath = tab.Source is null ? null : tab.Path;
        _sourceFile = tab.Source;
        _currentMarkdown = tab.Markdown;
        _renderedMarkdown = tab.RenderedMarkdown;
        _renderedDocument = tab.Rendered;
        _isEditing = tab.IsEditing;
        _isDirty = tab.IsDirty;
        _externalConflict = tab.ExternalConflict;
        _editorRevision = tab.EditorRevision;
        _watcher = tab.Watcher;
        _openVersion++;
        _outlineHeadings = null;
        PopulateOutline(tab.Rendered?.Headings ?? []);
    }

    private async Task CaptureActiveTabAsync()
    {
        var tab = _activeTab;
        if (tab is null) return;
        CaptureActiveMetadata();
        if (!_isEditing && _readerHasDocument && string.Equals(_readerPath, tab.Path, StringComparison.OrdinalIgnoreCase)
            && double.TryParse(await ScriptAsync("window.scrollY"), CultureInfo.InvariantCulture, out var scroll))
            tab.Position.ReadScroll = scroll;
    }

    private void CaptureActiveMetadata()
    {
        if (_activeTab is not { } tab) return;
        tab.Source = _sourceFile;
        tab.Markdown = _currentMarkdown;
        tab.RenderedMarkdown = _renderedMarkdown;
        tab.Rendered = _renderedDocument;
        tab.IsEditing = _isEditing;
        tab.IsDirty = _isDirty;
        tab.ExternalConflict = _externalConflict;
        tab.EditorRevision = _editorRevision;
        tab.Position.IsEditing = _isEditing;
        if (_isEditing && _editor is not null)
        {
            tab.EditorDocument = _editor.Document;
            tab.Position.CaretOffset = _editor.CaretOffset;
            tab.Position.SelectionStart = _editor.SelectionStart;
            tab.Position.SelectionLength = _editor.SelectionLength;
            tab.Position.EditorScroll = _editor.VerticalOffset;
        }
        tab.Notify();
    }

    private void UpdateTabStatus() { CaptureActiveMetadata(); }

    private void BindTabEditor(DocumentTab tab)
    {
        _isLoadingEditor = true;
        try
        {
            if (tab.EditorDocument is null)
            {
                tab.EditorDocument = new TextDocument(_currentMarkdown);
                tab.EditorDocument.UndoStack.SizeLimit = 1000;
                tab.EditorDocument.UndoStack.PropertyChanged += (_, _) => UpdateEditorUndo();
            }
            Editor.Document = tab.EditorDocument;
            var length = Editor.Document.TextLength;
            var start = Math.Clamp(tab.Position.SelectionStart, 0, length);
            Editor.Select(start, Math.Clamp(tab.Position.SelectionLength, 0, length - start));
            if (tab.Position.SelectionLength == 0) Editor.CaretOffset = Math.Clamp(tab.Position.CaretOffset, 0, length);
            UpdateEditorUndo();
        }
        finally { _isLoadingEditor = false; }
    }

    private void SelectActiveTab()
    {
        _selectingTab = true;
        try
        {
            TabList.SelectedItem = _activeTab;
            TabBar.Visibility = _tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_activeTab is not null) TabList.ScrollIntoView(_activeTab);
        }
        finally { _selectingTab = false; }
    }

    private async void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingTab || TabList.SelectedItem is not DocumentTab tab) return;
        SelectActiveTab();
        await OpenDocumentAsync(tab.Path, addHistory: false);
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTab tab }) await CloseTabAsync(tab);
        e.Handled = true;
    }

    private async void TabList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right)
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(TabList, source) is not ListBoxItem { DataContext: DocumentTab tab } item) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Middle) await CloseTabAsync(tab);
        else BuildTabMenu(tab, item).IsOpen = true;
    }

    private void TabList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (TabList.SelectedItem is DocumentTab tab) BuildTabMenu(tab, TabList).IsOpen = true;
    }

    private ContextMenu BuildTabMenu(DocumentTab tab, FrameworkElement target)
    {
        var menu = CreateAppMenu(target);
        AddMenuItem(menu, "Close", async () => { await CloseTabAsync(tab); }, shortcut: "Ctrl+W");
        AddMenuItem(menu, "Close other tabs", () => CloseTabsAsync(_tabs.Where(t => !ReferenceEquals(t, tab)).ToArray()), _tabs.Count > 1);
        var index = _tabs.IndexOf(tab);
        AddMenuItem(menu, "Close tabs to the right", () => CloseTabsAsync(_tabs.Skip(_tabs.IndexOf(tab) + 1).ToArray()), index < _tabs.Count - 1);
        AddMenuSeparator(menu);
        AddMenuItem(menu, "Close all tabs", () => CloseTabsAsync(_tabs.ToArray()));
        return menu;
    }

    private async Task CloseTabsAsync(IEnumerable<DocumentTab> tabs)
    {
        foreach (var tab in tabs.ToArray()) if (!await CloseTabAsync(tab)) break;
    }

    private async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (_closingWorkspace || _closed) return false;
        await _documentGate.WaitAsync();
        try
        {
            if (!_tabs.Contains(tab)) return true;
            var prior = _activeTab;
            if (tab.IsDirty && !ReferenceEquals(tab, _activeTab)) await ActivateTabCoreAsync(tab);
            if (ReferenceEquals(tab, _activeTab))
            {
                await SaveEditorAsync();
                if (!await ConfirmDraftCloseAsync()) return false;
            }
            await RemoveTabCoreAsync(tab, prior);
            SaveWorkspace();
            return true;
        }
        finally { _documentGate.Release(); }
    }

    private async Task RemoveTabCoreAsync(DocumentTab tab, DocumentTab? preferred = null)
    {
        if (ReferenceEquals(tab, _activeTab))
        {
            var index = _tabs.IndexOf(tab);
            await CaptureActiveTabAsync();
            _activeTab = null;
            _tabs.Remove(tab);
            tab.Watcher?.Dispose();
            ResetDocumentFields();
            if (_tabs.Count > 0)
                await ActivateTabCoreAsync(preferred is not null && _tabs.Contains(preferred) ? preferred : _tabs[Math.Min(index, _tabs.Count - 1)]);
            else ResetEmptyWorkspace();
        }
        else { _tabs.Remove(tab); tab.Watcher?.Dispose(); SelectActiveTab(); }
    }

    private async Task<bool> ConfirmDraftCloseAsync()
    {
        if (!_isDirty) return true;
        var answer = MessageBox.Show(this, $"Save changes to “{Path.GetFileName(_currentPath)}” before closing?",
            "Unsaved Markdown changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return await SaveEditorAsync(force: true);
        _isDirty = false;
        return true;
    }

    private void ResetDocumentFields()
    {
        _autoSaveTimer.Stop(); _reloadTimer.Stop();
        _currentPath = null; _sourceFile = null; _currentMarkdown = "";
        _renderedDocument = null; _renderedMarkdown = null;
        _isEditing = _isDirty = _externalConflict = false;
        _watcher = null;
        _openVersion++;
    }

    private void ResetEmptyWorkspace()
    {
        ResetDocumentFields();
        SelectActiveTab();
        Reader.Visibility = EditorPanel.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Visible;
        ModeButton.IsEnabled = SaveButton.IsEnabled = false;
        ModeButton.Content = "Edit";
        DocumentName.Text = Title = "Markdown Viewer";
        DocumentStats.Text = ""; StatusText.Text = "Ready to read"; StatusText.ToolTip = null;
        Notice.Visibility = FindBar.Visibility = Visibility.Collapsed;
        _outlineHeadings = null; PopulateOutline([]);
        _ = RevealCurrentDocumentAsync();
    }

    private void AddTabHistory(string path)
    {
        if (_historyIndex >= 0 && _history[_historyIndex].Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(path); _historyIndex = _history.Count - 1;
        UpdateHistoryButtons();
    }

    private void SaveWorkspace()
    {
        if (!_restoreWorkspace || _restoringWorkspace || StartupMetrics.ReportPath is not null) return;
        CaptureActiveMetadata();
        new WorkspaceSession { Documents = _tabs.Select(t => t.Position).ToList(), ActivePath = _activeTab?.Path }.Save();
    }

    private async Task RestoreWorkspaceAsync()
    {
        if (!_restoreWorkspace || StartupMetrics.ReportPath is not null) return;
        _restoringWorkspace = true;
        try
        {
            var session = WorkspaceSession.Load();
            foreach (var saved in session.Documents)
                _tabs.Add(new DocumentTab(saved.Path) { Position = saved, IsEditing = saved.IsEditing });
            SelectActiveTab();
            if ((_initialPath is null || Directory.Exists(_initialPath)) && _tabs.Count > 0)
                await OpenDocumentAsync((_tabs.FirstOrDefault(t => t.Path.Equals(session.ActivePath, StringComparison.OrdinalIgnoreCase)) ?? _tabs[0]).Path, false);
        }
        finally { _restoringWorkspace = false; }
    }

    private async Task ReloadActiveDocumentAsync()
    {
        var tab = _activeTab;
        await _documentGate.WaitAsync();
        try
        {
            if (_closed || !ReferenceEquals(tab, _activeTab)) return;
            if (_isEditing) await CheckExternalChangeAsync();
            else if (_currentPath is not null) await OpenDocumentCoreAsync(_currentPath, false, preserveScroll: true, skipUnchanged: true);
            UpdateTabStatus();
        }
        finally { _documentGate.Release(); }
    }

    private async Task<bool> PrepareWorkspaceCloseAsync()
    {
        await _documentGate.WaitAsync();
        try
        {
            await SaveEditorAsync();
            await CaptureActiveTabAsync();
            var original = _activeTab;
            foreach (var tab in _tabs.Where(t => t.IsDirty).ToArray())
            {
                if (!ReferenceEquals(tab, _activeTab)) await ActivateTabCoreAsync(tab);
                if (!await ConfirmDraftCloseAsync()) return false;
                CaptureActiveMetadata();
            }
            if (original is not null && !ReferenceEquals(original, _activeTab)) await ActivateTabCoreAsync(original);
            await CaptureActiveTabAsync();
            SaveWorkspace();
            return true;
        }
        finally { _documentGate.Release(); }
    }
}
