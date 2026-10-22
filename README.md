# Markdown Viewer

A small, native Windows Markdown reader with a quiet, document-first interface. Open a file, read it, and keep your place while the document updates.

- Rendered Markdown with headings, lists, links, tables, quotes, and code blocks.
- A native source editor with Markdown syntax highlighting, a formatting toolbar, undo/redo, and automatic saving after a short pause.
- Document outline, text search, zoom, and light/dark themes.
- A resizable folder tree with Files/Outline tabs, lazy subfolder loading, and automatic updates when files are added, renamed, or removed.
- Recent files, drag and drop, and automatic reload when the file changes, including when a second viewer window has the same document open.
- Relative local images and links to other Markdown documents.
- Native WPF desktop window with an embedded WebView2 document surface.

Raw HTML and scripts are disabled, and remote images are not fetched. Edit mode changes the Markdown source directly and saves after a short pause. Use Ctrl+S to save immediately. If another window changes the file while you have unsaved edits, Markdown Viewer keeps your text and asks before replacing the newer file.

In Edit mode, select text and use the toolbar to apply formatting. With no selection, inline actions insert an editable example; headings and lists apply to the current line. The toolbar offers heading levels 1–6, bold, italic, strikethrough, inline code, links, bullet and numbered lists, checklists, quotes, code blocks, images, tables, and dividers. Clicking an active heading/list format or wrapping style again removes it. Link and image insertion selects the destination so you can replace it immediately. Each toolbar action is one undo step.

The editor has line numbers, wrapped prose, a visible current line, and explicit caret/selection colors for both themes. The zoom controls also resize editor text. Highlighting uses an incremental native text editor and initializes when entering Edit mode.

Open documents from a local drive; network shares are not supported. Markdown files can be up to 10 MB. Text is read as UTF-8, with byte order mark detection for UTF-8 and UTF-16 files.

## Browse a folder

Click **Folder** or press **Ctrl+Shift+O**, then choose a folder such as `C:\Dev\notes`. The **Files** tab shows its subfolders and Markdown files; expand a folder with its arrow or the keyboard, and click a file to read it. The **Outline** tab still shows headings in the current document. Drag the divider to resize the sidebar, or use Ctrl+Shift+B to hide it.

The folder choice and sidebar width are remembered. Opening a folder with no document open displays its root `README.md` when one exists. You can also drop a folder onto the window or pass its path on the command line. **More → Browse this file's folder** opens the current document's containing folder in the tree.

The tree lists `.md`, `.markdown`, `.mdown`, and `.mkd` files and omits hidden/system entries, `.git`, and linked directories. Subfolders load when expanded. Additions, renames, and removals update loaded branches automatically; the folder header also has a Refresh button. Right-click a file for **Open in new window**, useful for keeping an editing window and a rendered preview side by side. File switching saves pending edits and keeps the current document open if saving is blocked by a conflict. Closing the folder leaves the current document open.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open a Markdown file |
| Ctrl+Shift+O | Open a folder |
| Ctrl+F | Find in the document |
| Ctrl+S | Save edits immediately |
| Enter / Shift+Enter | Next / previous match while searching |
| F3 / Shift+F3 | Next / previous match |
| Esc | Close search |
| Ctrl+B | Bold selected text while editing; show/hide sidebar while reading |
| Ctrl+I / Ctrl+K | Italic / insert link while editing |
| Ctrl+` | Inline code while editing |
| Ctrl+Z / Ctrl+Y | Undo / redo while editing |
| Ctrl+Shift+B | Show or hide the sidebar in either mode |
| Ctrl++ / Ctrl+- | Zoom in / out |
| Ctrl+0 | Reset zoom |
| Alt+Left / Alt+Right | Previous / next document in navigation history |
| F5 | Reload the current document |
| Ctrl+P | Print |

## Build and run

Requires Windows 10 version 2004 (build 19041) or later, or Windows 11, on x64. Building requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The build script also recognizes an SDK extracted to `.tools\dotnet`.

The app requires Microsoft's [Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), which is already present on most Windows computers. Install it if the app reports that the runtime is missing.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
.\artifacts\app\MarkdownViewer.exe
.\artifacts\app\MarkdownViewer.exe "C:\Notes\Read me.md"
```

The Release build in `artifacts\app` includes the .NET runtime, this README, third-party notices, and sample documents in `examples`. Keep the entire folder together when copying the portable app to another Windows x64 computer. Package restore requires network access on the first build. Unless already configured, SDK state and NuGet packages stay in `.tools\dotnet-home` and `.tools\nuget` inside this project.

Release publishing precompiles the app with ReadyToRun to reduce startup work. Document loading overlaps browser startup, and live updates reuse the open page. While editing, rendering waits until you select Read; saving still updates other viewer windows automatically. See [performance measurements and checks](PERFORMANCE.md) for results and reproduction steps.

## Install and enable double-click opening

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1 -OpenDefaultApps
```

The installer copies the app to `%LOCALAPPDATA%\Programs\MarkdownViewer`, adds a Start Menu shortcut, and registers **Markdown Viewer** as an Open With option for `.md`, `.markdown`, `.mdown`, and `.mkd`. Installation is per-user and requires no administrator access.

Windows asks you to choose the default app. Right-click a Markdown file, select **Open with → Choose another app → Markdown Viewer → Always**. You can also assign extensions in **Settings → Apps → Default apps**. After that, double-clicking a Markdown file opens the rendered document in Markdown Viewer. The installer never overwrites Windows' protected default-app choices.

To update, close the installed app, rebuild, and run the installer again. To uninstall, use **Settings → Apps → Installed apps → Markdown Viewer**, or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall.ps1
```

Uninstall removes this app's installation, shortcut, and registrations. Your documents and saved reading preferences remain.

## Dependencies

Markdown parsing uses [Markdig](https://github.com/xoofx/markdig) (BSD 2-Clause). Source editing uses [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) (MIT). Document display uses [Microsoft WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) under its SDK and runtime terms. The desktop shell uses [.NET WPF](https://github.com/dotnet/wpf) (MIT). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for license texts and attribution; the published app also includes the bundled runtime's notices.
