using System.IO;
using System.Text.Json;

namespace MarkdownViewer;

internal sealed class UserSettings
{
    private string? _savedFolder;
    private bool _savedShowFiles;
    private bool _savedShowOutline;
    private double _savedSidebarWidth = 260;
    private double _savedOutlineWidth = 240;
    public int LayoutVersion { get; set; } = 2;
    public bool DarkMode { get; set; }
    public bool ShowOutline { get; set; }
    public bool ShowFiles { get; set; }
    public string? LastFolder { get; set; }
    public double SidebarWidth { get; set; } = 260;
    public double OutlineWidth { get; set; } = 240;
    public double Zoom { get; set; } = 1;
    public List<string> RecentFiles { get; set; } = [];
    public static string DataDirectory => StartupMetrics.ProfileDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var settings = document.Deserialize<UserSettings>() ?? new();
            // Older versions used ShowOutline for the whole sidebar and ShowFiles
            // for its selected tab. Keep the visible pane on its new side.
            if (!document.RootElement.TryGetProperty(nameof(LayoutVersion), out var version) || version.GetInt32() < 2)
            {
                var visible = !document.RootElement.TryGetProperty(nameof(ShowOutline), out var show) || show.GetBoolean();
                var filesTab = settings.ShowFiles;
                settings.ShowFiles = visible && filesTab;
                settings.ShowOutline = visible && !filesTab;
                if (!filesTab) settings.OutlineWidth = settings.SidebarWidth;
            }
            settings.LayoutVersion = 2;
            settings.Zoom = double.IsFinite(settings.Zoom) ? Math.Clamp(settings.Zoom, 0.5, 2.5) : 1;
            settings.SidebarWidth = double.IsFinite(settings.SidebarWidth) ? Math.Clamp(settings.SidebarWidth, 200, 480) : 260;
            settings.OutlineWidth = double.IsFinite(settings.OutlineWidth) ? Math.Clamp(settings.OutlineWidth, 200, 480) : 240;
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
            // A window that hasn't changed its navigation preferences must not overwrite
            // a newer choice made in a second window.
            var snapshot = (UserSettings)MemberwiseClone();
            var latest = Load();
            if (LastFolder == _savedFolder) snapshot.LastFolder = latest.LastFolder;
            if (ShowFiles == _savedShowFiles) snapshot.ShowFiles = latest.ShowFiles;
            if (ShowOutline == _savedShowOutline) snapshot.ShowOutline = latest.ShowOutline;
            if (SidebarWidth == _savedSidebarWidth) snapshot.SidebarWidth = latest.SidebarWidth;
            if (OutlineWidth == _savedOutlineWidth) snapshot.OutlineWidth = latest.OutlineWidth;
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
        _savedShowOutline = ShowOutline;
        _savedSidebarWidth = SidebarWidth;
        _savedOutlineWidth = OutlineWidth;
    }
}
