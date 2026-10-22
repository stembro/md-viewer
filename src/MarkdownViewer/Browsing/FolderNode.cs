using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MarkdownViewer.Browsing;

public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    internal FolderNode(string path, bool isDirectory, bool placeholder = false, string? label = null)
    {
        FullPath = path;
        Name = label ?? new DirectoryInfo(path).Name;
        IsDirectory = isDirectory;
        IsPlaceholder = placeholder;
        if (isDirectory) Children.Add(Placeholder("Loading…"));
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; }
    public string ToolTip => Error is null ? FullPath : FullPath + "\n" + Error;
    public string? Error { get; private set; }
    public bool IsLoaded { get; private set; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; Changed(); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal async Task<bool> LoadAsync(bool refresh = false)
    {
        if (!IsDirectory) return true;
        if (IsLoaded && !refresh) return true;
        await _loadGate.WaitAsync();
        try
        {
            if (IsLoaded && !refresh) return true;
            return await LoadCoreAsync();
        }
        finally { _loadGate.Release(); }
    }

    private async Task<bool> LoadCoreAsync()
    {
        try
        {
            var entries = await Task.Run(() => FolderListing.Read(FullPath));
            var wanted = entries.Select(e => e.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Children.Count - 1; i >= 0; i--)
                if (Children[i].IsPlaceholder || !wanted.Contains(Children[i].FullPath)) Children.RemoveAt(i);
            var existing = Children.ToDictionary(n => n.FullPath, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!existing.TryGetValue(entry.FullPath, out var node) || node.IsDirectory != entry.IsDirectory
                    || !string.Equals(node.FullPath, entry.FullPath, StringComparison.Ordinal))
                {
                    if (node is not null) Children.Remove(node);
                    node = new FolderNode(entry.FullPath, entry.IsDirectory);
                    Children.Insert(i, node);
                }
                else if (i >= Children.Count || !ReferenceEquals(Children[i], node))
                    Children.Move(Children.IndexOf(node), i);
            }
            IsLoaded = true;
            Error = null;
            Changed(nameof(ToolTip));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = ex.Message;
            Changed(nameof(ToolTip));
            if (!IsLoaded)
            {
                Children.Clear();
                Children.Add(Placeholder("Cannot read this folder"));
            }
            return false;
        }
    }

    private static FolderNode Placeholder(string name) => new("", false, true, name);
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
