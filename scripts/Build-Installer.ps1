#requires -Version 7.2
param([string]$Version = '0.1.22', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'Publish.ps1') }
$payload = Join-Path $repoRoot 'artifacts\publish\win-x64'
$output = Join-Path $repoRoot 'artifacts\installer'
$wixFolder = Join-Path $repoRoot 'artifacts\tools\wix'
$wix = Join-Path $wixFolder 'wix.exe'
if (-not (Test-Path -LiteralPath $wix)) {
    & dotnet tool install --tool-path $wixFolder wix --version 4.0.6 --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw 'Unable to install the WiX build tool.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $payload 'CodexMonitor.App.exe'))) { throw 'Release output is missing.' }
foreach ($resource in 'CodexMonitor.App.pri','App.xbf','MainWindow.xbf','Styles.xbf') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $resource))) { throw "Required WinUI resource is missing: $resource" }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$namespace = 'http://wixtoolset.org/schemas/v4/wxs'
$document = [xml]('<Wix xmlns="' + $namespace + '"><Fragment><DirectoryRef Id="INSTALLFOLDER"/><ComponentGroup Id="ApplicationFiles"/></Fragment></Wix>')
$fragment = $document.DocumentElement.FirstChild
$rootDirectory = $fragment.FirstChild
$group = $fragment.LastChild
$directories = @{ '' = $rootDirectory }
$folderComponents = [Collections.Generic.HashSet[string]]::new()
function Add-WixNode($parent, [string]$name, [hashtable]$attributes) {
    $element = $document.CreateElement($name, $namespace)
    foreach ($key in $attributes.Keys) { $element.SetAttribute($key, [string]$attributes[$key]) }
    [void]$parent.AppendChild($element)
    return $element
}
function Get-StableId([string]$value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0,24)
}
function Get-ComponentGuid([string]$relative) {
    $bytes = [Text.Encoding]::UTF8.GetBytes('CodexMonitor.Files:' + $relative.ToLowerInvariant())
    $digest = [Security.Cryptography.SHA256]::HashData($bytes)
    return ([Guid]::new([byte[]]$digest[0..15])).ToString()
}
function Get-PayloadDirectory([string]$relative) {
    if ($directories.ContainsKey($relative)) { return $directories[$relative] }
    $parentPath = [IO.Path]::GetDirectoryName($relative)
    if ($null -eq $parentPath) { $parentPath = '' }
    $parent = Get-PayloadDirectory $parentPath
    $element = Add-WixNode $parent 'Directory' @{ Id = 'D' + (Get-StableId $relative); Name = [IO.Path]::GetFileName($relative) }
    $directories[$relative] = $element
    return $element
}
foreach ($file in (Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)) {
    # Debug symbols and build diagnostics do not belong in the installed application.
    if ($file.Extension -in '.pdb','.log') { continue }
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
    $folder = [IO.Path]::GetDirectoryName($relative)
    if ($null -eq $folder) { $folder = '' }
    $directory = Get-PayloadDirectory $folder
    $id = Get-StableId $relative
    $component = Add-WixNode $directory 'Component' @{Id = 'C' + $id; Guid = (Get-ComponentGuid $relative); Bitness = 'always64'}
    [void](Add-WixNode $component 'File' @{Id = 'F' + $id; Source = $file.FullName})
    # Per-user components use HKCU key paths so MSI repair and ICE validation remain correct.
    [void](Add-WixNode $component 'RegistryValue' @{Root='HKCU'; Key='Software\CodexMonitor\Installer\Files'; Name='F'+$id; Type='integer'; Value='1'; KeyPath='yes'})
    if ($folderComponents.Add($folder)) { [void](Add-WixNode $component 'RemoveFolder' @{Id='R'+$id; On='uninstall'}) }
    [void](Add-WixNode $group 'ComponentRef' @{Id='C'+$id})
}
$payloadManifest = Join-Path $output 'Payload.wxs'
$document.Save($payloadManifest)
$msiPath = Join-Path $output "CodexMonitor-$Version-win-x64.msi"
& $wix build (Join-Path $repoRoot 'installer\Product.wxs') $payloadManifest -arch x64 -d "ProductVersion=$Version" -d "AppIcon=$(Join-Path $repoRoot 'src\CodexMonitor.App\Assets\AppIcon.ico')" -o $msiPath
if ($LASTEXITCODE -ne 0) { throw 'MSI compilation or validation failed.' }
$hash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($msiPath + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($msiPath) + [Environment]::NewLine)
Write-Output $msiPath
