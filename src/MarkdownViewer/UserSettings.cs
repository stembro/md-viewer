using System.IO;
using System.Text.Json;

namespace MarkdownViewer;

internal sealed class UserSettings
{
    public bool DarkMode { get; set; }
    public bool ShowOutline { get; set; } = true;
    public double Zoom { get; set; } = 1;
    public List<string> RecentFiles { get; set; } = [];
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Zoom = double.IsFinite(settings.Zoom) ? Math.Clamp(settings.Zoom, 0.5, 2.5) : 1;
            settings.RecentFiles = (settings.RecentFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(12).ToList();
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporary = Path.Combine(DataDirectory, $"settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
