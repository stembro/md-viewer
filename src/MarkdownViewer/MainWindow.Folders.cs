using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownViewer.Browsing;
using Microsoft.Win32;

namespace MarkdownViewer;

public partial class MainWindow
{
    private FolderNode? _folderRoot;
    private FileSystemWatcher? _folderWatcher;
    private readonly DispatcherTimer _folderRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HashSet<string> _changedFolders = new(StringComparer.OrdinalIgnoreCase);
    private int _folderVersion;
    private int _folderOpenVersion;
    private HashSet<string> _loadedFolderPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownFolderPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressFolderNavigation;

    private void InitializeFolderBrowser()
    {
        _folderRefreshTimer.Tick += async (_, _) =>
        {
            _folderRefreshTimer.Stop();
            var changed = _changedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _changedFolders.Clear();
            var root = _folderRoot;
            foreach (var node in LoadedFolders().Where(n => changed.Contains(n.FullPath)).ToArray())
            {
                if (_closed || !ReferenceEquals(root, _folderRoot)) return;
                await node.LoadAsync(refresh: true);
            }
            UpdateFolderWatchPaths();
            UpdateFolderEmpty();
            if (FolderTree.SelectedItem is null) await RevealCurrentDocumentAsync();
        };
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e) => await ChooseFolderAsync();

    private async Task ChooseFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Open a folder of Markdown files", Multiselect = false };
        var initial = _folderRoot?.FullPath ?? (_currentPath is null ? null : Path.GetDirectoryName(_currentPath));
        if (initial is not null && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) == true) await SetFolderAsync(dialog.FolderName);
    }

    private async Task SetFolderAsync(string path, bool restore = false)
    {
        var request = ++_folderOpenVersion;
        try
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Choose a folder on a local drive. Network shares are not supported.");
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The folder no longer exists or could not be found.");
            if (_folderRoot is null)
            {
                FolderEmptyText.Text = "Loading folder…";
                FolderEmpty.Visibility = Visibility.Visible;
            }
            var root = new FolderNode(path, true);
            if (!await root.LoadAsync()) throw new IOException(root.Error);
            if (_closed || request != _folderOpenVersion) return;
            var version = ++_folderVersion;
            _folderWatcher?.Dispose();
            _folderWatcher = null;
            _folderRefreshTimer.Stop();
            _changedFolders.Clear();
            _folderRoot = root;
            UpdateFolderWatchPaths();
            _suppressFolderNavigation = true;
            try { FolderTree.ItemsSource = root.Children; }
            finally { _suppressFolderNavigation = false; }
            FolderNameText.Text = root.Name;
            FolderNameText.ToolTip = path;
            NewDocumentButton.IsEnabled = RefreshFolderButton.IsEnabled = CloseFolderButton.IsEnabled = true;
            _settings.LastFolder = path;
            if (!restore) _settings.ShowFiles = true;
            ApplySidebars();
            UpdateFolderEmpty();
            _settings.Save();
            WatchFolder(root, version);
            await RevealCurrentDocumentAsync();
            // An index is a useful starting page when opening a folder on its own.
            if (_currentPath is null && !_closed && version == _folderVersion)
            {
                var index = root.Children.FirstOrDefault(n => !n.IsDirectory && n.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase));
                if (index is not null) await OpenDocumentAsync(index.FullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_closed && request == _folderOpenVersion)
            {
                UpdateFolderEmpty();
                ShowNotice($"Could not open folder “{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}”. {ex.Message}");
            }
        }
    }

    private async void FolderTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FolderNode node } && node.IsDirectory)
        {
            await node.LoadAsync();
            UpdateFolderWatchPaths();
        }
    }

    private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressFolderNavigation || e.NewValue is not FolderNode { IsDirectory: false, IsPlaceholder: false } node
            || string.Equals(node.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        await OpenDocumentAsync(node.FullPath);
        if (!string.Equals(node.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _suppressFolderNavigation = true;
            try { node.IsSelected = false; }
            finally { _suppressFolderNavigation = false; }
            await RevealCurrentDocumentAsync();
        }
    }

    private async Task RevealCurrentDocumentAsync()
    {
        var root = _folderRoot;
        var path = _currentPath;
        var version = _folderVersion;
        var documentVersion = _openVersion;
        if (root is null) return;
        if (path is null || !FolderListing.Contains(root.FullPath, path))
        {
            _suppressFolderNavigation = true;
            try { if (FolderTree.SelectedItem is FolderNode selected) selected.IsSelected = false; }
            finally { _suppressFolderNavigation = false; }
            return;
        }
        var node = root;
        var parts = Path.GetRelativePath(root.FullPath, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!await node.LoadAsync()) return;
            if (_closed || version != _folderVersion || documentVersion != _openVersion) return;
            var next = node.Children.FirstOrDefault(n => n.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (next is null) return;
            if (next.IsDirectory) { next.IsExpanded = true; node = next; }
            else if (i == parts.Length - 1)
            {
                _suppressFolderNavigation = true;
                try { next.IsSelected = true; }
                finally { _suppressFolderNavigation = false; }
            }
        }
        UpdateFolderWatchPaths();
    }

    private void WatchFolder(FolderNode root, int version)
    {
        try
        {
            _folderWatcher = new FileSystemWatcher(root.FullPath)
            { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            void Changed(object sender, FileSystemEventArgs e)
            {
                var relative = Path.GetRelativePath(root.FullPath, e.FullPath);
                if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase))) return;
                if (Path.GetFileName(e.FullPath).StartsWith('.') && Path.GetExtension(e.FullPath) == ".tmp") return;
                var parent = Path.GetDirectoryName(e.FullPath);
                var oldPath = (e as RenamedEventArgs)?.OldFullPath;
                var oldParent = oldPath is null ? null : Path.GetDirectoryName(oldPath);
                var loaded = Volatile.Read(ref _loadedFolderPaths);
                if ((parent is null || !loaded.Contains(parent)) && (oldParent is null || !loaded.Contains(oldParent))) return;
                var directory = Directory.Exists(e.FullPath);
                var known = Volatile.Read(ref _knownFolderPaths);
                if (!directory && !known.Contains(e.FullPath) && (oldPath is null || !known.Contains(oldPath))
                    && !FolderListing.IsMarkdown(e.FullPath) && (oldPath is null || !FolderListing.IsMarkdown(oldPath))) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closed || version != _folderVersion) return;
                    if (parent is not null) _changedFolders.Add(parent);
                    if (oldParent is not null) _changedFolders.Add(oldParent);
                    _folderRefreshTimer.Stop();
                    _folderRefreshTimer.Start();
                });
            }
            _folderWatcher.Created += Changed;
            _folderWatcher.Deleted += Changed;
            _folderWatcher.Renamed += Changed;
            _folderWatcher.Error += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_closed || version != _folderVersion) return;
                foreach (var node in LoadedFolders()) _changedFolders.Add(node.FullPath);
                _folderRefreshTimer.Stop();
                _folderRefreshTimer.Start();
            });
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowNotice("The folder is open, but automatic folder updates are unavailable. Use the folder's Refresh button. " + ex.Message); }
    }

    private void UpdateFolderWatchPaths()
    {
        var loaded = LoadedFolders().ToArray();
        Volatile.Write(ref _loadedFolderPaths, loaded.Select(n => n.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Volatile.Write(ref _knownFolderPaths, loaded.SelectMany(n => n.Children.Where(c => c.IsDirectory).Select(c => c.FullPath).Append(n.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private IEnumerable<FolderNode> LoadedFolders()
    {
        if (_folderRoot is null) yield break;
        var pending = new Stack<FolderNode>();
        pending.Push(_folderRoot);
        while (pending.TryPop(out var node))
        {
            if (!node.IsLoaded && !node.IsExpanded) continue;
            yield return node;
            foreach (var child in node.Children.Where(n => n.IsDirectory)) pending.Push(child);
        }
    }

    private async void RefreshFolder_Click(object sender, RoutedEventArgs e) => await RefreshFolderAsync();
    private async Task RefreshFolderAsync()
    {
        var root = _folderRoot;
        foreach (var node in LoadedFolders().ToArray())
        {
            if (_closed || !ReferenceEquals(root, _folderRoot)) return;
            if (!await node.LoadAsync(refresh: true)) ShowNotice($"Could not refresh “{node.Name}”. {node.Error}");
        }
        UpdateFolderWatchPaths();
        UpdateFolderEmpty();
        if (FolderTree.SelectedItem is null) await RevealCurrentDocumentAsync();
    }

    private void UpdateFolderEmpty()
    {
        FolderEmptyText.Text = _folderRoot is null
            ? "Keep your notes within reach. Open a folder to browse its Markdown files."
            : "There are no Markdown files or subfolders here.";
        FolderEmpty.Visibility = _folderRoot is null || _folderRoot.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseFolder_Click(object sender, RoutedEventArgs e)
    {
        DisposeFolderBrowser();
        _folderRoot = null;
        UpdateFolderWatchPaths();
        FolderTree.ItemsSource = null;
        _settings.LastFolder = null;
        FolderNameText.Text = "No folder open";
        FolderNameText.ToolTip = null;
        NewDocumentButton.IsEnabled = RefreshFolderButton.IsEnabled = CloseFolderButton.IsEnabled = false;
        UpdateFolderEmpty();
        _settings.Save();
    }

    private void DisposeFolderBrowser()
    {
        _folderVersion++;
        _folderOpenVersion++;
        _folderRefreshTimer.Stop();
        _changedFolders.Clear();
        _folderWatcher?.Dispose();
        _folderWatcher = null;
    }

    private void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (FolderTree.SelectedItem is FolderNode { IsPlaceholder: false } node) BuildFolderMenu(node, FolderTree).IsOpen = true;
        else if (_folderRoot is not null) BuildFolderMenu(_folderRoot, FolderTree).IsOpen = true;
    }

    private void FolderTree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || e.OriginalSource is not DependencyObject source) return;
        var item = ItemsControl.ContainerFromElement(null, source) as TreeViewItem;
        var node = item?.DataContext as FolderNode ?? _folderRoot;
        if (node is null || node.IsPlaceholder) { e.Handled = true; return; }
        e.Handled = true;
        BuildFolderMenu(node, item ?? (FrameworkElement)FolderTree).IsOpen = true;
    }

    private ContextMenu BuildFolderMenu(FolderNode node, FrameworkElement target)
    {
        var menu = CreateAppMenu(target);
        if (node.IsDirectory) AddMenuItem(menu, "Open this folder", () => SetFolderAsync(node.FullPath));
        else
        {
            AddMenuItem(menu, "Open", () => OpenDocumentAsync(node.FullPath));
            AddMenuItem(menu, "Open in new window", () => { new MainWindow(node.FullPath, restoreWorkspace: false).Show(); return Task.CompletedTask; });
        }
        AddMenuSeparator(menu);
        var directory = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath)!;
        AddMenuItem(menu, "New Markdown file…", () => PromptNewEntryAsync(directory, false));
        AddMenuItem(menu, "New folder…", () => PromptNewEntryAsync(directory, true));
        if (!node.IsDirectory)
        {
            AddMenuItem(menu, "Rename…", () => PromptRenameAsync(node.FullPath), shortcut: "F2");
            AddMenuItem(menu, "Delete…", () => ConfirmDeleteAsync(node.FullPath), shortcut: "Del");
        }
        AddMenuSeparator(menu);
        AddMenuItem(menu, "Show in File Explorer", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowNotice(ex.Message); }
            return Task.CompletedTask;
        });
        return menu;
    }
}
