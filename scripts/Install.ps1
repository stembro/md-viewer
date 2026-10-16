[CmdletBinding()]
param([switch]$OpenDefaultApps)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Markdown Viewer can only be installed on Windows.'
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

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishDirectory = Join-Path $projectRoot 'artifacts\app'
$programsDirectory = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'MarkdownViewer'))
$executable = Join-Path $installDirectory 'MarkdownViewer.exe'
$markerPath = Join-Path $installDirectory '.markdown-viewer-install.json'
$openCommand = '"{0}" "%1"' -f $executable
$progId = 'MarkdownViewer.Document'
$applicationKey = 'HKCU:\Software\MarkdownViewer'
$classesRoot = 'HKCU:\Software\Classes'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Markdown Viewer.lnk'

if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'MarkdownViewer.exe') -PathType Leaf)) {
    throw 'Build the application first: powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1'
}
if (-not $installDirectory.StartsWith($programsDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installation path is outside the per-user Programs directory.'
}
if (Test-Path -LiteralPath $installDirectory) {
    $existingDirectory = Get-Item -LiteralPath $installDirectory -Force
    if (-not $existingDirectory.PSIsContainer -or ($existingDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to install into a file or redirected directory: $installDirectory"
    }
    if (@(Get-ChildItem -LiteralPath $installDirectory -Force).Count -gt 0) {
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "The installation folder already contains files but has no Markdown Viewer installation marker: $installDirectory"
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($marker.application -ne 'MarkdownViewer' -or $marker.installDirectory -ne $installDirectory) {
            throw 'The installation marker does not match this application and directory.'
        }
    }
}
if (@(Get-Process -Name MarkdownViewer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }).Count -gt 0) {
    throw 'Close the installed Markdown Viewer before installing an update.'
}

# Never replace another application's Open With registration.
foreach ($relativeKey in @($progId, 'Applications\MarkdownViewer.exe')) {
    $commandKey = Join-Path $classesRoot ($relativeKey + '\shell\open\command')
    if (Test-Path -LiteralPath $commandKey) {
        if ((Get-Item -LiteralPath $commandKey).GetValue('') -ne $openCommand) {
            throw "An existing registration uses a different executable: $commandKey"
        }
    }
}
if (Test-Path -LiteralPath $applicationKey) {
    $previousLocation = (Get-Item -LiteralPath $applicationKey).GetValue('InstallDirectory', '')
    if ($previousLocation -ne $installDirectory) {
        throw 'An existing MarkdownViewer application registration belongs to a different installation.'
    }
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
if ((Test-Path -LiteralPath $shortcutPath) -and $shortcut.TargetPath -ne $executable) {
    throw "The existing Start Menu shortcut belongs to a different application: $shortcutPath"
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $publishDirectory -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $installDirectory -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $installDirectory 'Uninstall.ps1') -Force
@{ application = 'MarkdownViewer'; schemaVersion = 1; installDirectory = $installDirectory } |
    ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8

function Set-RegistryString([string]$Path, [string]$Name, [string]$Value) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    if ($Name -eq '(default)') {
        Set-Item -LiteralPath $Path -Value $Value
    } else {
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType String -Force | Out-Null
    }
}

$progIdPath = Join-Path $classesRoot $progId
Set-RegistryString $progIdPath '(default)' 'Markdown document'
Set-RegistryString (Join-Path $progIdPath 'DefaultIcon') '(default)' ('"{0}",0' -f $executable)
Set-RegistryString (Join-Path $progIdPath 'shell\open\command') '(default)' $openCommand
$applicationPath = Join-Path $classesRoot 'Applications\MarkdownViewer.exe'
Set-RegistryString $applicationPath 'FriendlyAppName' 'Markdown Viewer'
Set-RegistryString (Join-Path $applicationPath 'shell\open\command') '(default)' $openCommand
Set-RegistryString $applicationKey 'InstallDirectory' $installDirectory
$capabilities = Join-Path $applicationKey 'Capabilities'
Set-RegistryString $capabilities 'ApplicationName' 'Markdown Viewer'
Set-RegistryString $capabilities 'ApplicationDescription' 'A clean, native viewer for Markdown documents.'
Set-RegistryString $capabilities 'ApplicationIcon' ('"{0}",0' -f $executable)
foreach ($extension in @('.md', '.markdown', '.mdown', '.mkd')) {
    $openWithPath = Join-Path $classesRoot ($extension + '\OpenWithProgids')
    if (-not (Test-Path -LiteralPath $openWithPath)) { New-Item -Path $openWithPath -Force | Out-Null }
    New-ItemProperty -LiteralPath $openWithPath -Name $progId -Value ([byte[]]@()) -PropertyType None -Force | Out-Null
    Set-RegistryString (Join-Path $applicationPath 'SupportedTypes') $extension ''
    Set-RegistryString (Join-Path $capabilities 'FileAssociations') $extension $progId
}
Set-RegistryString 'HKCU:\Software\RegisteredApplications' 'MarkdownViewer' 'Software\MarkdownViewer\Capabilities'

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MarkdownViewer'
Set-RegistryString $uninstallKey 'DisplayName' 'Markdown Viewer'
Set-RegistryString $uninstallKey 'DisplayVersion' ([Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion)
Set-RegistryString $uninstallKey 'DisplayIcon' $executable
Set-RegistryString $uninstallKey 'InstallLocation' $installDirectory
Set-RegistryString $uninstallKey 'UninstallString' ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $installDirectory 'Uninstall.ps1'))
New-ItemProperty -LiteralPath $uninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -LiteralPath $uninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null

$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'Read Markdown documents'
$shortcut.IconLocation = $executable + ',0'
$shortcut.Save()
[MarkdownViewer.InstallerShell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "Installed: $executable"
Write-Host 'To enable double-click opening, right-click a Markdown file, choose Open with > Choose another app, select Markdown Viewer, and choose Always.'
Write-Host 'Windows controls default applications. You can also choose Markdown Viewer in Settings > Apps > Default apps.'
Write-Host 'Use -OpenDefaultApps to open that Settings page after installation.'
if ($OpenDefaultApps) { Start-Process 'ms-settings:defaultapps' }
