using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using MarkdownViewer.Editing;

namespace MarkdownViewer;

public partial class MainWindow
{
    private TextEditor? _editor;
    private TextEditor Editor => _editor ??= CreateEditor();

    private TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            Name = "Editor", FontFamily = new FontFamily("Consolas"), FontSize = 14 * _settings.Zoom,
            ShowLineNumbers = true, WordWrap = true, Padding = new Thickness(16, 24, 24, 40),
            BorderThickness = new Thickness(0), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.HighlightCurrentLine = true;
        editor.Document.UndoStack.SizeLimit = 1000;
        editor.TextChanged += Editor_TextChanged;
        editor.Document.UndoStack.PropertyChanged += (_, _) => UpdateEditorUndo();
        AutomationProperties.SetName(editor, "Markdown source editor");
        EditorHost.Children.Add(editor);
        RegisterName("Editor", editor);
        ApplyEditorTheme(editor);
        return editor;
    }

    private void ApplyEditorTheme(TextEditor editor)
    {
        editor.Background = (Brush)Resources["Surface"];
        editor.Foreground = (Brush)Resources["Ink"];
        editor.LineNumbersForeground = (Brush)Resources["Muted"];
        editor.TextArea.Caret.CaretBrush = Brush(_settings.DarkMode ? "#FFFFFF" : "#163E2C");
        editor.TextArea.SelectionBrush = Brush(_settings.DarkMode ? "#416550" : "#C7E2CF");
        editor.TextArea.SelectionForeground = (Brush)Resources["Ink"];
        editor.TextArea.SelectionBorder = new Pen(Brushes.Transparent, 0);
        editor.TextArea.TextView.CurrentLineBackground = Brush(_settings.DarkMode ? "#2B352E" : "#EFF4EE");
        editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        editor.SyntaxHighlighting = MarkdownHighlighting.Get(_settings.DarkMode);

        static SolidColorBrush Brush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }

    private void UpdateEditorUndo()
    {
        EditorUndoButton.IsEnabled = _editor?.CanUndo == true;
        EditorRedoButton.IsEnabled = _editor?.CanRedo == true;
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command }) ApplyFormatting(command);
    }

    private void ApplyFormatting(string command)
    {
        if (!_isEditing) return;
        if (command == "undo") Editor.Undo();
        else if (command == "redo") Editor.Redo();
        else
        {
            var edit = MarkdownFormatting.Apply(Editor.Text, Editor.SelectionStart, Editor.SelectionLength,
                command, _sourceFile?.NewLine ?? "\n");
            using (Editor.Document.RunUpdate())
                Editor.Document.Replace(edit.Offset, edit.Length, edit.Text);
            Editor.Select(edit.SelectionStart, edit.SelectionLength);
        }
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
        UpdateEditorUndo();
    }

    private void EditorMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string group } button) return;
        var menu = CreateAppMenu(button);
        menu.Resources[SystemColors.MenuBrushKey] = (Brush)Resources["Panel"];
        menu.Resources[SystemColors.MenuTextBrushKey] = (Brush)Resources["Ink"];
        menu.Resources[SystemColors.HighlightBrushKey] = (Brush)Resources["Hover"];
        menu.Resources[SystemColors.HighlightTextBrushKey] = (Brush)Resources["Ink"];
        menu.Resources["Ink"] = (Brush)Resources["Ink"];
        menu.Resources["Hover"] = (Brush)Resources["Hover"];
        void Item(string label, string command)
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("EditorMenuItemStyle") };
            item.Click += (_, _) => ApplyFormatting(command);
            menu.Items.Add(item);
        }
        if (group == "headings")
        {
            Item("Paragraph", "paragraph");
            menu.Items.Add(new Separator());
            for (var level = 1; level <= 6; level++) Item($"Heading {level}   {new string('#', level)}", $"h{level}");
        }
        else if (group == "lists")
        {
            Item("•  Bullet list", "bullet");
            Item("1.  Numbered list", "ordered");
            Item("☐  Checklist", "task");
        }
        else
        {
            Item("Code block", "codeblock");
            Item("Image", "image");
            Item("Table", "table");
            Item("Horizontal divider", "rule");
        }
        menu.IsOpen = true;
    }

    private bool HandleEditorShortcut(Key key, ModifierKeys modifiers)
    {
        if (!_isEditing || _editor?.IsKeyboardFocusWithin != true || modifiers != ModifierKeys.Control) return false;
        var command = key switch { Key.B => "bold", Key.I => "italic", Key.K => "link", Key.Oem3 => "code", _ => null };
        if (command is null) return false;
        ApplyFormatting(command);
        return true;
    }
}
