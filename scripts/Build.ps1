[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Build this WPF application on Windows.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectFile = Join-Path $projectRoot 'src\MarkdownViewer\MarkdownViewer.csproj'
$outputDirectory = Join-Path $projectRoot 'artifacts\app'
$candidates = @()
$installedDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($installedDotnet) { $candidates += $installedDotnet.Source }
$candidates += (Join-Path $projectRoot '.tools\dotnet\dotnet.exe')

$dotnetExecutable = $null
foreach ($candidate in ($candidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $sdkVersions = @(& $candidate --list-sdks 2>$null)
    if ($LASTEXITCODE -eq 0 -and ($sdkVersions -match '^10\.')) {
        $dotnetExecutable = $candidate
        break
    }
}
if (-not $dotnetExecutable) {
    throw 'The .NET 10 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/10.0, or extract it to .tools\dotnet. A .NET runtime alone cannot build the app.'
}
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Project not found: $projectFile"
}

Write-Host "Building with $dotnetExecutable"
& $dotnetExecutable publish $projectFile --configuration $Configuration --runtime win-x64 --self-contained true --output $outputDirectory -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $outputDirectory 'MarkdownViewer.exe') -PathType Leaf)) {
    throw 'Publishing finished without producing MarkdownViewer.exe.'
}
foreach ($fileName in @('README.md', 'PERFORMANCE.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $fileName) -Destination $outputDirectory -Force
}
$publishedExamples = Join-Path $outputDirectory 'examples'
New-Item -ItemType Directory -Path $publishedExamples -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'examples') -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $publishedExamples -Recurse -Force
}

# Keep the bundled runtime's own third-party notices with the portable output.
$runtimeConfig = Get-Content -LiteralPath (Join-Path $outputDirectory 'MarkdownViewer.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
$runtimeNoticeCandidates = @(
    (Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT"),
    (Join-Path (Split-Path -Parent $dotnetExecutable) "packs\Microsoft.NETCore.App.Runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT")
)
$runtimeNotices = $runtimeNoticeCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $runtimeNotices) { throw "Could not locate third-party notices for the bundled .NET $runtimeVersion runtime." }
Copy-Item -LiteralPath $runtimeNotices -Destination (Join-Path $outputDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
Write-Host "Built: $outputDirectory\MarkdownViewer.exe"
Write-Host 'The output includes the .NET runtime; keep all files in the app folder together.'
