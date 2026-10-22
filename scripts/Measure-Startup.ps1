[CmdletBinding()]
param(
    [string]$Executable = 'artifacts\app\MarkdownViewer.exe',
    [string]$Document = 'examples\Welcome.md',
    [string]$Label = 'after',
    [ValidateRange(1, 10)][int]$Samples = 3,
    [ValidateRange(0, 15)][int]$SettleSeconds = 6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Executable))
$documentPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Document))
$reportDirectory = Join-Path $projectRoot 'artifacts\performance-startup'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null

for ($sample = 1; $sample -le $Samples; $sample++) {
    # Chromium can keep the profile locked briefly after its host exits. Don't
    # accidentally measure browser shutdown/relaunch contention as startup work.
    if ($SettleSeconds -gt 0) { Start-Sleep -Seconds $SettleSeconds }
    $reportPath = Join-Path $reportDirectory "$Label-$sample.json"
    $arguments = '"{0}" --performance-report "{1}"' -f $documentPath, $reportPath
    $process = Start-Process -FilePath $appPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw 'The measurement window did not finish in 30 seconds.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) {
        throw "The measurement window failed (exit $($process.ExitCode))."
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    [pscustomobject]@{ Sample = $sample; WindowMs = $report.windowLoadedMs; BrowserMs = $report.browserReadyMs; DocumentMs = $report.documentReadyMs }
}
