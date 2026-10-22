using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Editing;

internal sealed record MarkdownEdit(int Offset, int Length, string Text, int SelectionStart, int SelectionLength);

internal static partial class MarkdownFormatting
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>\#{1,6}[ \t]+|[-+*][ \t]+(?:\[[ xX]\][ \t]+)?|\d+[.)][ \t]+|>[ \t]?)?")]
    private static partial Regex BlockPrefix();

    internal static MarkdownEdit Apply(string text, int start, int length, string command, string newline)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > text.Length - length) throw new ArgumentOutOfRangeException(nameof(length));
        var selected = text.Substring(start, length);
        return command switch
        {
            "bold" => Wrap(text, start, length, "**", "bold text"),
            "italic" => Wrap(text, start, length, "*", "italic text"),
            "strike" => Wrap(text, start, length, "~~", "text"),
            "code" => Wrap(text, start, length, new string('`', LongestRun(selected, '`') + 1), "code"),
            "link" => Link(start, length, selected, false),
            "image" => Link(start, length, selected, true),
            "codeblock" => Block(text, start, length, new string('`', Math.Max(3, LongestRun(selected, '`') + 1)), selected.Length > 0 ? selected : "code", newline),
            "rule" => InsertBlock(text, start, length, "---", newline),
            "table" => InsertBlock(text, start, length, $"| Column 1 | Column 2 |{newline}| --- | --- |{newline}| Text | Text |", newline),
            _ => Lines(text, start, length, command, newline)
        };
    }

    private static MarkdownEdit Wrap(string text, int start, int length, string marker, string placeholder)
    {
        var selected = text.Substring(start, length);
        if (length >= marker.Length * 2 && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal))
        {
            var inner = selected[marker.Length..^marker.Length];
            return new(start, length, inner, start, inner.Length);
        }
        if (start >= marker.Length && start + length + marker.Length <= text.Length
            && text.AsSpan(start - marker.Length, marker.Length).SequenceEqual(marker)
            && text.AsSpan(start + length, marker.Length).SequenceEqual(marker))
            return new(start - marker.Length, length + marker.Length * 2, selected, start - marker.Length, length);
        var content = length == 0 ? placeholder : selected;
        var padding = marker[0] == '`' && (content.StartsWith('`') || content.EndsWith('`')) ? " " : "";
        return new(start, length, marker + padding + content + padding + marker, start + marker.Length + padding.Length, content.Length);
    }

    private static MarkdownEdit Link(int start, int length, string selected, bool image)
    {
        var label = length == 0 ? (image ? "alt text" : "link text") : selected;
        label = label.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        var prefix = (image ? "![" : "[") + label + "](";
        var destination = image ? "image.png" : "https://example.com";
        return new(start, length, prefix + destination + ")", start + prefix.Length, destination.Length);
    }

    private static MarkdownEdit Block(string text, int start, int length, string fence, string content, string newline)
    {
        var before = start > 0 && text[start - 1] is not ('\r' or '\n') ? newline : "";
        var after = start + length < text.Length && text[start + length] is not ('\r' or '\n') ? newline : "";
        var endOfContent = content.EndsWith('\n') || content.EndsWith('\r') ? "" : newline;
        return new(start, length, before + fence + newline + content + endOfContent + fence + after,
            start + before.Length + fence.Length + newline.Length, content.Length);
    }

    private static MarkdownEdit InsertBlock(string text, int start, int length, string content, string newline)
    {
        var before = start > 0 && text[start - 1] is not ('\r' or '\n') ? newline + newline : "";
        var after = start + length < text.Length ? newline + newline : newline;
        return new(start, length, before + content + after, start + before.Length, content.Length);
    }

    private static MarkdownEdit Lines(string text, int start, int length, string command, string newline)
    {
        var offset = start;
        while (offset > 0 && text[offset - 1] is not ('\r' or '\n')) offset--;
        var end = start + length;
        // A selection ending at the beginning of a line doesn't include that line.
        if (length > 0 && end > offset && text[end - 1] == '\n')
        {
            end--;
            if (end > offset && text[end - 1] == '\r') end--;
        }
        else if (length > 0 && end > offset && text[end - 1] == '\r') end--;
        else while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        var source = text[offset..end];
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var prefix = command switch
        {
            "h1" => "# ", "h2" => "## ", "h3" => "### ", "h4" => "#### ", "h5" => "##### ", "h6" => "###### ",
            "bullet" => "- ", "ordered" => "1. ", "task" => "- [ ] ", "quote" => "> ", "paragraph" => "",
            _ => throw new ArgumentException("Unknown Markdown action.", nameof(command))
        };
        bool Matches(string marker) => command switch
        {
            "ordered" => marker.Length > 0 && char.IsAsciiDigit(marker[0]),
            "bullet" => marker.Trim() is "-" or "+" or "*",
            "task" => marker.Contains('[', StringComparison.Ordinal),
            _ => marker == prefix
        };
        var remove = command != "paragraph" && lines.Where(l => l.Length > 0).All(l => Matches(BlockPrefix().Match(l).Groups["marker"].Value));
        // An empty line receives an editable example rather than an empty marker.
        if (source.Length == 0)
        {
            var placeholder = command.StartsWith('h') ? "Heading" : command == "quote" ? "Quote" : command == "task" ? "Task" : command == "paragraph" ? "" : "List item";
            return new(offset, 0, prefix + placeholder, offset + prefix.Length, placeholder.Length);
        }
        var result = new StringBuilder();
        var number = 1;
        var firstDelta = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.Append(newline);
            var line = lines[i];
            if (line.Length == 0) continue;
            var match = BlockPrefix().Match(line);
            var indent = match.Groups["indent"].Value;
            var marker = remove ? "" : command == "ordered" ? $"{number++}. " : prefix;
            if (i == 0) firstDelta = indent.Length + marker.Length - match.Length;
            result.Append(indent).Append(marker).Append(line.AsSpan(match.Length));
        }
        var replacement = result.ToString();
        return length == 0
            ? new(offset, end - offset, replacement, Math.Clamp(start + firstDelta, offset, offset + replacement.Length), 0)
            : new(offset, end - offset, replacement, offset, replacement.Length);
    }

    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            current = c == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }
}
