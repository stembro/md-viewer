using System.IO;

namespace MarkdownViewer.Browsing;

internal sealed record FolderEntry(string FullPath, string Name, bool IsDirectory);

internal static class FolderListing
{
    internal static bool IsMarkdown(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";

    internal static IReadOnlyList<FolderEntry> Read(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        return new DirectoryInfo(path).EnumerateFileSystemInfos("*", options)
            .Where(info => !info.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            .Select(info => new FolderEntry(info.FullName, info.Name, info.Attributes.HasFlag(FileAttributes.Directory)))
            .Where(entry => entry.IsDirectory || IsMarkdown(entry.FullPath))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool Contains(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
