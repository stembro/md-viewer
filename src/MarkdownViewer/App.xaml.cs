using System.Windows;
using System.IO;

namespace MarkdownViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.ToList();
        var reportIndex = args.IndexOf("--performance-report");
        if (reportIndex >= 0 && reportIndex + 1 < args.Count)
        {
            StartupMetrics.ReportPath = Path.GetFullPath(args[reportIndex + 1]);
            StartupMetrics.ProfileDirectory = Path.Combine(Path.GetDirectoryName(StartupMetrics.ReportPath)!, "profile");
            args.RemoveRange(reportIndex, 2);
        }
        StartupMetrics.Mark("managedStartupMs");
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        new MainWindow(paths.FirstOrDefault(), additionalPaths: paths.Skip(1).ToArray()).Show();
    }
}
