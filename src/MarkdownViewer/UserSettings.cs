using System.IO;
using System.Text.Json;

namespace MarkdownViewer;

internal sealed class UserSettings
{
    private string? _savedFolder;
    private bool _savedShowFiles;
    private double _savedSidebarWidth = 260;
    public bool DarkMode { get; set; }
    public bool ShowOutline { get; set; } = true;
    public bool ShowFiles { get; set; }
    public string? LastFolder { get; set; }
    public double SidebarWidth { get; set; } = 260;
    public double Zoom { get; set; } = 1;
    public List<string> RecentFiles { get; set; } = [];
    public static string DataDirectory => StartupMetrics.ProfileDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Zoom = double.IsFinite(settings.Zoom) ? Math.Clamp(settings.Zoom, 0.5, 2.5) : 1;
            settings.SidebarWidth = double.IsFinite(settings.SidebarWidth) ? Math.Clamp(settings.SidebarWidth, 200, 480) : 260;
            settings.RecentFiles = (settings.RecentFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(12).ToList();
            settings.RememberFolderState();
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            // A window that hasn't changed its folder preferences must not overwrite
            // a newer choice made in a second window.
            var snapshot = (UserSettings)MemberwiseClone();
            var latest = Load();
            if (LastFolder == _savedFolder) snapshot.LastFolder = latest.LastFolder;
            if (ShowFiles == _savedShowFiles) snapshot.ShowFiles = latest.ShowFiles;
            if (SidebarWidth == _savedSidebarWidth) snapshot.SidebarWidth = latest.SidebarWidth;
            var temporary = Path.Combine(DataDirectory, $"settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, FilePath, true);
            RememberFolderState();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RememberFolderState()
    {
        _savedFolder = LastFolder;
        _savedShowFiles = ShowFiles;
        _savedSidebarWidth = SidebarWidth;
    }
}
