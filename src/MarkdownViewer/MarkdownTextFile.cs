using System.IO;
using System.Text;

namespace MarkdownViewer;

internal sealed record MarkdownTextFile(string Text, Encoding Encoding, bool HasPreamble, string NewLine)
{
    private static readonly (byte[] Preamble, Encoding Encoding)[] Encodings =
    [
        (new byte[] { 0x00, 0x00, 0xFE, 0xFF }, new UTF32Encoding(true, true)),
        (new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, new UTF32Encoding(false, true)),
        (new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(true)),
        (new byte[] { 0xFE, 0xFF }, new UnicodeEncoding(true, true)),
        (new byte[] { 0xFF, 0xFE }, new UnicodeEncoding(false, true))
    ];

    public static async Task<MarkdownTextFile> ReadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if (bytes.Length > 10 * 1024 * 1024)
            throw new IOException("This file is larger than the 10 MB reading limit.");

        Encoding encoding = new UTF8Encoding(false);
        var preambleLength = 0;
        foreach (var (preamble, detectedEncoding) in Encodings)
        {
            if (!bytes.AsSpan().StartsWith(preamble)) continue;
            encoding = detectedEncoding;
            preambleLength = preamble.Length;
            break;
        }

        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\r') ? "\r"
            : "\n";
        return new MarkdownTextFile(text, encoding, preambleLength > 0, newLine);
    }

    public byte[] Encode(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (NewLine != "\n") normalized = normalized.Replace("\n", NewLine, StringComparison.Ordinal);
        var content = Encoding.GetBytes(normalized);
        if (!HasPreamble) return content;

        var preamble = Encoding.GetPreamble();
        var result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    public static bool ContentEquals(string left, string right)
    {
        if (left.Equals(right, StringComparison.Ordinal)) return true;
        // Compare CRLF, CR and LF without allocating copies of both documents.
        var l = 0;
        var r = 0;
        while (l < left.Length && r < right.Length)
        {
            var a = left[l++];
            var b = right[r++];
            if (a == '\r')
            {
                a = '\n';
                if (l < left.Length && left[l] == '\n') l++;
            }
            if (b == '\r')
            {
                b = '\n';
                if (r < right.Length && right[r] == '\n') r++;
            }
            if (a != b) return false;
        }
        return l == left.Length && r == right.Length;
    }
}
