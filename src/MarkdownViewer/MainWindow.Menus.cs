using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MarkdownViewer;

public partial class MainWindow
{
    private ContextMenu? _openContextMenu;
    private ContextMenu CreateAppMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint,
            Style = (Style)FindResource("AppContextMenuStyle"), ItemContainerStyle = (Style)FindResource("EditorMenuItemStyle") };
        foreach (var key in new[] { "Panel", "Ink", "Muted", "Hover", "Line", "Accent" }) menu.Resources[key] = Resources[key];
        menu.Opened += (_, _) => { if (_openContextMenu is not null && !ReferenceEquals(_openContextMenu, menu)) _openContextMenu.IsOpen = false; _openContextMenu = menu; };
        menu.Closed += (_, _) => { if (ReferenceEquals(_openContextMenu, menu)) _openContextMenu = null; };
        return menu;
    }

    private MenuItem AddMenuItem(ContextMenu menu, string text, Func<Task> action, bool enabled = true, string shortcut = "")
    {
        var item = new MenuItem { Header = text, InputGestureText = shortcut, IsEnabled = enabled, Style = (Style)FindResource("EditorMenuItemStyle") };
        item.Click += async (_, _) => await action();
        menu.Items.Add(item);
        return item;
    }

    private void AddMenuSeparator(ContextMenu menu) => menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparatorStyle") });
}
