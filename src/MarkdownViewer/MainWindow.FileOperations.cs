using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MarkdownViewer.Browsing;
using Microsoft.VisualBasic.FileIO;

namespace MarkdownViewer;

public partial class MainWindow
{
    private string? SelectedFolder => FolderTree.SelectedItem is FolderNode { IsPlaceholder: false } node
        ? (node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath)) : _folderRoot?.FullPath;

    private async void NewDocument_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is { } directory) await PromptNewEntryAsync(directory, false);
    }

    private async Task PromptNewEntryAsync(string directory, bool folder)
    {
        var initial = FileNames.Suggest(directory, folder ? "New folder" : "Untitled", folder ? "" : ".md");
        var dialog = new NameDialog(this, folder ? "New folder" : "New Markdown file", directory, initial, "Create",
            name => ValidateEntryName(directory, name, folder));
        if (dialog.ShowDialog() == true)
            if (folder) await CreateFolderAsync(directory, dialog.Value);
            else await CreateDocumentAsync(directory, dialog.Value);
    }

    private static string? ValidateEntryName(string directory, string name, bool folder, string? existing = null)
    {
        try
        {
            var path = FileNames.Resolve(directory, name, !folder, existing is null ? ".md" : Path.GetExtension(existing));
            if ((File.Exists(path) || Directory.Exists(path)) && !string.Equals(path, existing, StringComparison.OrdinalIgnoreCase))
                return "A file or folder with that name already exists.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ex.Message; }
    }

    private async Task CreateDocumentAsync(string directory, string name)
    {
        try
        {
            var path = FileNames.Resolve(directory, name);
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            await RefreshFolderAsync();
            await OpenDocumentAsync(path);
            if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) EnterEditMode();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ShowNotice("Could not create the Markdown file. " + ex.Message); }
    }

    private async Task CreateFolderAsync(string directory, string name)
    {
        try
        {
            var path = FileNames.Resolve(directory, name, false);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A file or folder with that name already exists.");
            Directory.CreateDirectory(path);
            await RefreshFolderAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ShowNotice("Could not create the folder. " + ex.Message); }
    }

    private async Task PromptRenameAsync(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var dialog = new NameDialog(this, "Rename Markdown file", directory, Path.GetFileName(path), "Rename",
            name => ValidateEntryName(directory, name, false, path));
        if (dialog.ShowDialog() == true) await RenameDocumentAsync(path, dialog.Value);
    }

    private async Task<bool> RenameDocumentAsync(string path, string name)
    {
        await _documentGate.WaitAsync();
        try
        {
            if (_closed || _closingWorkspace) return false;
            if (_savingTask is not null) await _savingTask;
            path = Path.GetFullPath(path);
            var renamed = FileNames.Resolve(Path.GetDirectoryName(path)!, name, extension: Path.GetExtension(path));
            if (string.Equals(path, renamed, StringComparison.Ordinal)) return true;
            File.Move(path, renamed);
            var tab = _tabs.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (tab is not null)
            {
                tab.Watcher?.Dispose(); tab.Watcher = null; tab.Rename(renamed);
                if (ReferenceEquals(tab, _activeTab))
                {
                    _currentPath = renamed; _watcher = null;
                    DocumentName.Text = Path.GetFileName(renamed) + (_isEditing ? " · Editing" : "");
                    Title = $"{Path.GetFileName(renamed)} — Markdown Viewer";
                    if (!_isEditing) StatusText.Text = renamed;
                    StatusText.ToolTip = renamed;
                    WatchFile(renamed);
                }
            }
            if (string.Equals(_readerPath, path, StringComparison.OrdinalIgnoreCase)) _readerPath = renamed;
            for (var i = 0; i < _history.Count; i++) if (_history[i].Equals(path, StringComparison.OrdinalIgnoreCase)) _history[i] = renamed;
            for (var i = 0; i < _settings.RecentFiles.Count; i++) if (_settings.RecentFiles[i].Equals(path, StringComparison.OrdinalIgnoreCase)) _settings.RecentFiles[i] = renamed;
            SaveWorkspace(); _settings.Save();
            await RefreshFolderAsync();
            await RevealCurrentDocumentAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ShowNotice("Could not rename the file. " + ex.Message); return false; }
        finally { _documentGate.Release(); }
    }

    private async Task ConfirmDeleteAsync(string path)
    {
        if (MessageBox.Show(this, $"Move “{Path.GetFileName(path)}” to the Recycle Bin?", "Delete Markdown file",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await DeleteDocumentAsync(path);
    }

    private async Task<bool> DeleteDocumentAsync(string path)
    {
        await _documentGate.WaitAsync();
        try
        {
            if (_closed || _closingWorkspace) return false;
            if (_savingTask is not null) await _savingTask;
            path = Path.GetFullPath(path);
            var tab = _tabs.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(tab, _activeTab)) UpdateTabStatus();
            if (tab?.IsDirty == true)
            {
                if (!ReferenceEquals(tab, _activeTab)) await ActivateTabCoreAsync(tab);
                if (!await SaveEditorAsync())
                {
                    ShowNotice("This tab has unsaved changes. Save or resolve them, or close the tab and discard them, before deleting the file.");
                    return false;
                }
            }
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            if (tab is not null) await RemoveTabCoreAsync(tab);
            _settings.RecentFiles.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            for (var i = _history.Count - 1; i >= 0; i--)
                if (_history[i].Equals(path, StringComparison.OrdinalIgnoreCase)) { _history.RemoveAt(i); if (i <= _historyIndex) _historyIndex--; }
            UpdateHistoryButtons(); SaveWorkspace(); _settings.Save();
            await RefreshFolderAsync();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { ShowNotice("Could not delete the file. " + ex.Message); return false; }
        finally { _documentGate.Release(); }
    }

    private async void FolderTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode { IsDirectory: false, IsPlaceholder: false } node) return;
        if (e.Key == Key.F2) { e.Handled = true; await PromptRenameAsync(node.FullPath); }
        else if (e.Key == Key.Delete) { e.Handled = true; await ConfirmDeleteAsync(node.FullPath); }
    }
}
