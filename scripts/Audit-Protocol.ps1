param([string]$Executable = 'codex')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'artifacts\protocol-audit\current'
& $Executable app-server generate-json-schema --out $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Cannot generate installed CLI protocol schema' }
$taskInventory = Get-Content -LiteralPath (Join-Path $taskRoot 'docs\protocol-coverage.json') -Raw | ConvertFrom-Json -AsHashtable
$taskNotifications = Get-Content -LiteralPath (Join-Path $taskOutput 'ServerNotification.json') -Raw | ConvertFrom-Json -AsHashtable
$taskTurns = Get-Content -LiteralPath (Join-Path $taskOutput 'v2\ThreadTurnsListResponse.json') -Raw | ConvertFrom-Json -AsHashtable
$taskResponses = Get-Content -LiteralPath (Join-Path $taskOutput 'v2\RawResponseItemCompletedNotification.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($taskPair in @(
    @{ Name='notifications'; Actual=@($taskNotifications.oneOf | ForEach-Object { $_.properties.method.enum[0] }); Expected=@($taskInventory.notifications.method) },
    @{ Name='threadItemTypes'; Actual=@($taskTurns.definitions.ThreadItem.oneOf | ForEach-Object { $_.properties.type.enum[0] }); Expected=$taskInventory.threadItemTypes },
    @{ Name='responseItemTypes'; Actual=@($taskResponses.definitions.ResponseItem.oneOf | ForEach-Object { $_.properties.type.enum[0] }); Expected=$taskInventory.responseItemTypes }
)) {
    $taskDifference = @(Compare-Object ($taskPair.Expected | Sort-Object) ($taskPair.Actual | Sort-Object))
    if ($taskDifference.Count) { throw ($taskPair.Name + ': schema differs from reviewed inventory: ' + ($taskDifference | ConvertTo-Json -Compress)) }
    Write-Output ($taskPair.Name + ': ' + $taskPair.Actual.Count + ' reviewed types')
}
# Offline schema generation does not start a server, connect an account, or start an inference turn.
