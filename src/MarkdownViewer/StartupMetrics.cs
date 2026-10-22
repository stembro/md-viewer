using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MarkdownViewer;

// Opt-in only: the performance script supplies a report path and closes its own window.
internal static class StartupMetrics
{
    private static readonly Dictionary<string, double> Timings = [];
    private static readonly Lazy<DateTime> Started = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    });
    internal static string? ReportPath { get; set; }
    internal static string? ProfileDirectory { get; set; }

    internal static void Mark(string stage)
    {
        if (ReportPath is not null)
            lock (Timings) Timings[stage] = (DateTime.UtcNow - Started.Value).TotalMilliseconds;
    }

    internal static void Complete()
    {
        if (ReportPath is null) return;
        Mark("documentReadyMs");
        File.WriteAllText(ReportPath, JsonSerializer.Serialize(Timings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
