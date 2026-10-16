[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This uninstall script is for Windows.'
}
if (-not ('MarkdownViewer.InstallerShell' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MarkdownViewer {
    public static class InstallerShell {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
'@
}

$programsDirectory = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'MarkdownViewer'))
$executable = Join-Path $installDirectory 'MarkdownViewer.exe'
$markerPath = Join-Path $installDirectory '.markdown-viewer-install.json'
$openCommand = '"{0}" "%1"' -f $executable
$classesRoot = 'HKCU:\Software\Classes'
$progId = 'MarkdownViewer.Document'

# Check the full target and its ownership before deleting any installation files.
if (-not $installDirectory.StartsWith($programsDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($installDirectory) -ne 'MarkdownViewer') {
    throw 'The computed uninstall directory is outside the expected per-user installation path.'
}
if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
    throw "No Markdown Viewer installation marker was found in $installDirectory. No files or registrations were removed."
}
$installedDirectory = Get-Item -LiteralPath $installDirectory -Force
if (-not $installedDirectory.PSIsContainer -or ($installedDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing to remove a file or redirected installation directory.'
}
$resolvedDirectory = (Resolve-Path -LiteralPath $installDirectory).ProviderPath.TrimEnd('\')
if (-not $resolvedDirectory.Equals($installDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved installation path does not match the expected uninstall target.'
}
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.application -ne 'MarkdownViewer' -or $marker.installDirectory -ne $installDirectory) {
    throw 'The installation marker does not match this application and directory.'
}
if (@(Get-Process -Name MarkdownViewer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }).Count -gt 0) {
    throw 'Close Markdown Viewer before uninstalling it.'
}
# Reject nested redirects too, so recursive deletion cannot follow one elsewhere.
$redirects = @(Get-ChildItem -LiteralPath $installDirectory -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
if ($redirects.Count -gt 0) {
    throw 'The installation contains a redirected file or folder. Remove that redirect manually before uninstalling.'
}

foreach ($relativeKey in @($progId, 'Applications\MarkdownViewer.exe')) {
    $registrationPath = Join-Path $classesRoot $relativeKey
    $commandKey = Join-Path $registrationPath 'shell\open\command'
    if ((Test-Path -LiteralPath $commandKey) -and (Get-Item -LiteralPath $commandKey).GetValue('') -eq $openCommand) {
        Remove-Item -LiteralPath $registrationPath -Recurse -Force
    }
}
foreach ($extension in @('.md', '.markdown', '.mdown', '.mkd')) {
    $openWithPath = Join-Path $classesRoot ($extension + '\OpenWithProgids')
    if (Test-Path -LiteralPath $openWithPath) {
        Remove-ItemProperty -LiteralPath $openWithPath -Name $progId -ErrorAction SilentlyContinue
    }
}
$registeredApplications = 'HKCU:\Software\RegisteredApplications'
if ((Test-Path -LiteralPath $registeredApplications) -and
    (Get-Item -LiteralPath $registeredApplications).GetValue('MarkdownViewer', '') -eq 'Software\MarkdownViewer\Capabilities') {
    Remove-ItemProperty -LiteralPath $registeredApplications -Name 'MarkdownViewer'
}
$applicationKey = 'HKCU:\Software\MarkdownViewer'
if ((Test-Path -LiteralPath $applicationKey) -and
    (Get-Item -LiteralPath $applicationKey).GetValue('InstallDirectory', '') -eq $installDirectory) {
    Remove-Item -LiteralPath $applicationKey -Recurse -Force
}
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MarkdownViewer'
if ((Test-Path -LiteralPath $uninstallKey) -and
    (Get-Item -LiteralPath $uninstallKey).GetValue('InstallLocation', '') -eq $installDirectory) {
    Remove-Item -LiteralPath $uninstallKey -Recurse -Force
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Markdown Viewer.lnk'
if (Test-Path -LiteralPath $shortcutPath) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ($shortcut.TargetPath -eq $executable) { Remove-Item -LiteralPath $shortcutPath -Force }
}
Remove-Item -LiteralPath $installDirectory -Recurse -Force
[MarkdownViewer.InstallerShell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Markdown Viewer was uninstalled. Your Markdown documents and reading preferences were kept.'
