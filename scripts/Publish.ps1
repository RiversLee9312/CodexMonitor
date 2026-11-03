param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
& dotnet publish (Join-Path $repoRoot 'src\CodexMonitor.App\CodexMonitor.App.csproj') -c Release -p:Platform=$platform -r $Runtime --self-contained true -o (Join-Path $repoRoot "artifacts\publish\$Runtime")
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($resource in 'CodexMonitor.App.pri','App.xbf','MainWindow.xbf','Styles.xbf') {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "artifacts\publish\$Runtime\$resource"))) {
        throw "Required WinUI publish resource is missing: $resource"
    }
}
