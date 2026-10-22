using System.IO;
using System.Text.Json;

namespace MarkdownViewer;

internal sealed class WorkspaceSession
{
    public List<WorkspaceDocument> Documents { get; set; } = [];
    public string? ActivePath { get; set; }
    private static string FilePath => Path.Combine(UserSettings.DataDirectory, "workspace.json");

    public static WorkspaceSession Load()
    {
        try
        {
            var session = JsonSerializer.Deserialize<WorkspaceSession>(File.ReadAllText(FilePath)) ?? new();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            session.Documents = (session.Documents ?? []).Where(d => d is not null && ValidPath(d.Path))
                .Where(d => seen.Add(d.Path = Path.GetFullPath(d.Path))).ToList();
            foreach (var document in session.Documents)
            {
                document.ReadScroll = FiniteOffset(document.ReadScroll);
                document.EditorScroll = FiniteOffset(document.EditorScroll);
                document.CaretOffset = Math.Max(0, document.CaretOffset);
                document.SelectionStart = Math.Max(0, document.SelectionStart);
                document.SelectionLength = Math.Max(0, document.SelectionLength);
            }
            return session;
        }
        catch { return new(); }
    }

    public void Save()
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(UserSettings.DataDirectory);
            temporary = Path.Combine(UserSettings.DataDirectory, $"workspace-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static double FiniteOffset(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100_000_000) : 0;
    private static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && System.IO.Path.IsPathFullyQualified(path)
        && new[] { ".md", ".markdown", ".mdown", ".mkd" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && File.Exists(path);
}

internal sealed class WorkspaceDocument
{
    public string Path { get; set; } = "";
    public bool IsEditing { get; set; }
    public double ReadScroll { get; set; }
    public double EditorScroll { get; set; }
    public int CaretOffset { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
}
