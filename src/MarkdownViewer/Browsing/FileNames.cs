using System.IO;

namespace MarkdownViewer.Browsing;

internal static class FileNames
{
    internal static string Resolve(string directory, string name, bool markdown = true, string extension = ".md")
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            throw new ArgumentException("Enter a valid file name without slashes or a trailing dot.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
            throw new ArgumentException("This name is reserved by Windows. Choose another name.");
        if (markdown)
        {
            if (!Path.HasExtension(name)) name += extension;
            if (!FolderListing.IsMarkdown(name) || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
                throw new ArgumentException("Use a Markdown extension: .md, .markdown, .mdown, or .mkd.");
        }
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The destination folder no longer exists.");
        return Path.Combine(directory, name);
    }

    internal static string Suggest(string directory, string name, string extension)
    {
        for (var number = 1; ; number++)
        {
            var candidate = name + (number == 1 ? "" : $" {number}") + extension;
            var path = Path.Combine(directory, candidate);
            if (!File.Exists(path) && !Directory.Exists(path)) return candidate;
        }
    }
}
