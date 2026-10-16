using System.Windows;

namespace MarkdownViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (paths.Length == 0) new MainWindow(null).Show();
        else foreach (var path in paths) new MainWindow(path).Show();
    }
}
