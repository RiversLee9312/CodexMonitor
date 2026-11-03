param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$project = Join-Path $repoRoot 'src\CodexMonitor.App\CodexMonitor.App.csproj'
& dotnet restore $project -p:Platform=$platform -r $Runtime -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& (Join-Path $PSScriptRoot 'Update-LicenseCatalog.ps1') -Runtime $Runtime
& dotnet publish $project --no-restore -c Release -p:Platform=$platform -r $Runtime --self-contained true -o (Join-Path $repoRoot "artifacts\publish\$Runtime")
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($resource in 'CodexMonitor.App.pri','App.xbf','MainWindow.xbf','Styles.xbf') {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "artifacts\publish\$Runtime\$resource"))) {
        throw "Required WinUI publish resource is missing: $resource"
    }
}
