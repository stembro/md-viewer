using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MarkdownViewer;

public partial class MainWindow
{
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowFiles = !_settings.ShowFiles;
        ApplySidebars();
        _settings.Save();
    }

    private void Outline_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowOutline = !_settings.ShowOutline;
        ApplySidebars();
        _settings.Save();
    }

    private void ApplySidebars()
    {
        FilesSidebar.Visibility = FilesSplitter.Visibility = _settings.ShowFiles ? Visibility.Visible : Visibility.Collapsed;
        OutlineSidebar.Visibility = OutlineSplitter.Visibility = _settings.ShowOutline ? Visibility.Visible : Visibility.Collapsed;
        FilesSplitterColumn.Width = new GridLength(_settings.ShowFiles ? 4 : 0);
        OutlineSplitterColumn.Width = new GridLength(_settings.ShowOutline ? 4 : 0);
        ApplySidebarWidths();
        UpdateSidebarButtons();
    }

    private void WindowLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplySidebarWidths();
    }

    private void ApplySidebarWidths()
    {
        var files = _settings.ShowFiles ? _settings.SidebarWidth : 0;
        var outline = _settings.ShowOutline ? _settings.OutlineWidth : 0;
        var filesMinimum = _settings.ShowFiles ? 200 : 0;
        var outlineMinimum = _settings.ShowOutline ? 200 : 0;
        // The inner grid can exceed the window while its fixed columns are being
        // measured. Use the outer viewport to avoid treating that overflow as space.
        var width = WindowLayout.ActualWidth > 0 ? WindowLayout.ActualWidth : Width - 16;
        var available = Math.Max(filesMinimum + outlineMinimum,
            width - DocumentColumn.MinWidth - FilesSplitterColumn.Width.Value - OutlineSplitterColumn.Width.Value);
        // Temporarily shrink wide panels in a narrow window, retaining the user's
        // preferred widths for when there is room again. Keep space for the document.
        if (files + outline > available)
        {
            var extra = files + outline - filesMinimum - outlineMinimum;
            var scale = extra > 0 ? Math.Clamp((available - filesMinimum - outlineMinimum) / extra, 0, 1) : 0;
            files = filesMinimum + (files - filesMinimum) * scale;
            outline = outlineMinimum + (outline - outlineMinimum) * scale;
        }
        FilesColumn.MinWidth = filesMinimum;
        OutlineColumn.MinWidth = outlineMinimum;
        FilesColumn.MaxWidth = Math.Clamp(available - outline, 200, 480);
        OutlineColumn.MaxWidth = Math.Clamp(available - files, 200, 480);
        FilesColumn.Width = new GridLength(files);
        OutlineColumn.Width = new GridLength(outline);
    }

    private void FilesSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_settings.ShowFiles) return;
        _settings.SidebarWidth = Math.Clamp(FilesColumn.ActualWidth, 200, 480);
        ApplySidebarWidths();
        _settings.Save();
    }

    private void OutlineSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_settings.ShowOutline) return;
        _settings.OutlineWidth = Math.Clamp(OutlineColumn.ActualWidth, 200, 480);
        ApplySidebarWidths();
        _settings.Save();
    }

    private void UpdateSidebarButtons()
    {
        Update(FilesToggleButton, _settings.ShowFiles, "files", "Ctrl+Shift+B");
        Update(OutlineToggleButton, _settings.ShowOutline, "outline", "Ctrl+Shift+L");
        void Update(Button button, bool visible, string label, string shortcut)
        {
            button.Background = (Brush)Resources[visible ? "Hover" : "Surface"];
            button.Foreground = (Brush)Resources[visible ? "Accent" : "Muted"];
            button.ToolTip = $"{(visible ? "Hide" : "Show")} {label} ({shortcut})";
        }
    }
}
