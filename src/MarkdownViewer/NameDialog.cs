using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MarkdownViewer;

internal sealed class NameDialog : Window
{
    internal string Value { get; private set; } = "";

    internal NameDialog(Window owner, string title, string location, string initialValue, string action, Func<string, string?> validate)
    {
        Owner = owner; Title = title; Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Background = owner.Background; Foreground = owner.Foreground;
        foreach (var key in new[] { "Surface", "Panel", "Ink", "Muted", "Line", "Hover", "Accent" }) Resources[key] = owner.Resources[key];
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = location, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Resources["Muted"], FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });
        var input = new TextBox { Text = initialValue, Padding = new Thickness(9), FontSize = 14,
            Background = (Brush)Resources["Surface"], Foreground = Foreground, CaretBrush = Foreground, BorderBrush = (Brush)Resources["Line"] };
        panel.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Resources["Muted"], Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        var accept = new Button { Content = action, IsDefault = true, MinWidth = 80, Background = (Brush)Resources["Hover"], Foreground = (Brush)Resources["Accent"] };
        accept.Click += (_, _) =>
        {
            var message = validate(input.Text);
            if (message is not null) { error.Text = message; error.Visibility = Visibility.Visible; input.Focus(); return; }
            Value = input.Text; DialogResult = true;
        };
        buttons.Children.Add(accept); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.Select(0, System.IO.Path.GetFileNameWithoutExtension(initialValue).Length); };
    }
}
