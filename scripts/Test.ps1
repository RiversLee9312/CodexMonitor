param([switch]$IncludeInstalledCli, [switch]$IncludeRelease)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$oldIntegration = $env:CODEX_MONITOR_INTEGRATION
$oldReleaseRoot = $env:CODEX_MONITOR_RELEASE_ROOT
try {
    if ($IncludeInstalledCli) { $env:CODEX_MONITOR_INTEGRATION = '1' }
    else { $env:CODEX_MONITOR_INTEGRATION = '0' }
    if ($IncludeRelease) { $env:CODEX_MONITOR_RELEASE_ROOT = Join-Path $repoRoot 'artifacts\publish\win-x64' }
    & dotnet run --project (Join-Path $repoRoot 'tests\CodexMonitor.Core.Tests') -- --report-trx --results-directory (Join-Path $repoRoot 'artifacts\test-results')
    if ($LASTEXITCODE -ne 0) { throw 'MSTest failed.' }
} finally { $env:CODEX_MONITOR_INTEGRATION = $oldIntegration; $env:CODEX_MONITOR_RELEASE_ROOT = $oldReleaseRoot }
