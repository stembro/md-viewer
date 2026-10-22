# Performance

Measured on the same Windows machine on October 22, 2026. These are local measurements, not guarantees for other machines or documents.

| Operation | Before | After |
| --- | ---: | ---: |
| Open `examples/Welcome.md`, median of 3 runs | 3,343 ms | 1,378 ms |
| Render approximately 2 MB of Markdown, mean of 5 warmed iterations | 256 ms | 107 ms |
| Allocations per 2 MB render | 127.7 MB | 59.4 MB |
| Render 10,000 headings, mean of 5 warmed iterations | 120 ms | 65 ms |

Startup measures process creation through WebView2's successful NavigationCompleted event, including the native window, browser initialization, Markdown parsing, and document navigation. The baseline and optimized builds use the same sample and measurement hooks. Runs use a separate profile and pause six seconds between launches so the previous browser process can release the profile. They do not flush OS file caches. Cold launches and antivirus scanning can take longer.

An initial back-to-back run, without that pause, measured medians of 8.62 seconds before and 6.71 seconds after. Browser shutdown/profile contention remains a limit on very rapid close-and-reopen cycles. The optimization doesn't keep the application running after all its windows close.

The two-window integration check observed an edit appearing in the second reader in 987 ms, including the intentional 650 ms autosave pause and 180 ms file-event debounce. It also checks updates without navigation, unchanged mode switches without DOM replacement, duplicate file events, scroll preservation, and conflict protection.

## Changes

- Serve local content at `markdown.invalid`. Microsoft [warns that `.local` addresses can delay WebView2 navigation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2_3#SetVirtualHostNameToFolderMapping).
- Publish ReadyToRun assemblies and share one WebView2 environment between windows in the same process.
- Read, decode, and render on a worker while WebView2 initializes. Prewarm the browser while the welcome screen is open.
- Count words without allocating a regex Match object for every word; generate regex code at build time.
- Cache the most recently rendered document. Defer hidden rendering during editing and reuse the page when returning to Read without changes.
- Apply ordinary same-document updates to the existing page. Documents with more than 512 KiB of body HTML use the streamed navigation path, retaining support for large files.
- Bind the outline to data and recycle visible containers instead of constructing a WPF control for every heading.
- Debounce file events, skip unchanged content, and reuse the watcher. Settings are saved on document navigation rather than on every reload.
- Avoid whole-document copies/comparisons on each keystroke. Newline-aware comparisons allocate no normalized copies.

Raw Markdown HTML and scripts remain disabled. In-place updates use JSON-encoded HTML passed to host-executed code; the Markdown cannot supply executable script. Navigation, image filtering, content security policy, and save conflict checks remain in effect.

## Reproduce

Use a Windows machine with .NET 10 SDK and WebView2 installed. The build script also supports the SDK in `.tools\dotnet`; use that executable in place of `dotnet` if needed. For that SDK, set `DOTNET_CLI_HOME` to `.tools\dotnet-home` and `NUGET_PACKAGES` to `.tools\nuget`, using absolute paths.

```powershell
.\scripts\Build.ps1
.\scripts\Measure-Startup.ps1
dotnet run --project tests/MarkdownViewer.Tests -c Release
dotnet run --project tests/MarkdownViewer.Tests -c Release -- --benchmark
dotnet run --project tests/MarkdownViewer.IntegrationTests -c Release -- "$PWD\artifacts\integration-checks"
```

The startup script creates temporary viewer windows that close automatically. Reports and their separate browser profile are written under `artifacts\performance-startup`. The integration check edits only its fixture under `artifacts\integration-checks`, uses a separate profile, and closes its own windows. Neither check changes personal documents or reading preferences.
