$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$oldScreenshots = $env:CODEX_MONITOR_SCREENSHOTS
try {
    $env:CODEX_MONITOR_SCREENSHOTS = Join-Path $repoRoot 'artifacts\screenshots'
    & dotnet run --project (Join-Path $repoRoot 'tests\CodexMonitor.UiTests') -p:Platform=x64 -- --report-trx --results-directory (Join-Path $repoRoot 'artifacts\ui-test-results')
    if ($LASTEXITCODE -ne 0) { throw 'MSTest layout tests failed. Inspect artifacts\screenshots and artifacts\ui-test-results.' }
} finally { $env:CODEX_MONITOR_SCREENSHOTS = $oldScreenshots }
