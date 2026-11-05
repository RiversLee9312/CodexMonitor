$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$oldScreenshots = $env:CODEX_MONITOR_SCREENSHOTS
try {
    $env:CODEX_MONITOR_SCREENSHOTS = Join-Path $repoRoot 'artifacts\screenshots'
    & dotnet run --project (Join-Path $repoRoot 'tests\CodexMonitor.UiTests') -p:Platform=x64 -- --filter 'TestCategory!=IsolatedPerformance' --report-trx --results-directory (Join-Path $repoRoot 'artifacts\ui-test-results\functional')
    if ($LASTEXITCODE -ne 0) { throw 'MSTest layout tests failed. Inspect artifacts\screenshots and artifacts\ui-test-results.' }
    # Keep the raw latency assertion in a fresh process. Hundreds of preceding XAML/UIA
    # fixtures otherwise induce process-wide GC pauses unrelated to this workload.
    & dotnet run --project (Join-Path $repoRoot 'tests\CodexMonitor.UiTests') -p:Platform=x64 --no-build -- --filter 'TestCategory=IsolatedPerformance' --report-trx --results-directory (Join-Path $repoRoot 'artifacts\ui-test-results\performance')
    if ($LASTEXITCODE -ne 0) { throw 'MSTest isolated navigation performance test failed.' }
} finally { $env:CODEX_MONITOR_SCREENSHOTS = $oldScreenshots }
