param([string]$AssetsFile = '', [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$AssetsFile) { $AssetsFile = Join-Path $repo 'src\CodexMonitor.App\obj\project.assets.json' }
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$folders = @($assets.packageFolders.PSObject.Properties.Name)
$components = [Collections.Generic.List[object]]::new()
function Add-Package([string]$name, [string]$version, [string]$folder) {
    $spec = Get-ChildItem -LiteralPath $folder -Filter *.nuspec | Select-Object -First 1
    [xml]$xml = Get-Content -LiteralPath $spec.FullName
    $metadata = $xml.package.metadata
    $licenseNode = $metadata.license
    $pinned = Join-Path $repo "third-party\$name\$version"
    $license = if ($licenseNode.type -eq 'file') { Get-Content -LiteralPath (Join-Path $folder $licenseNode.InnerText) -Raw } else {
        $file = if (Test-Path -LiteralPath (Join-Path $pinned 'LICENSE.txt')) { Get-Item -LiteralPath (Join-Path $pinned 'LICENSE.txt') } else { Get-ChildItem -LiteralPath $folder -File | Where-Object Name -match '^LICENSE(\.(txt|md))?$' | Select-Object -First 1 }
        if ($file) { Get-Content -LiteralPath $file.FullName -Raw }
        elseif ($metadata.licenseUrl -and $metadata.licenseUrl -ne 'https://aka.ms/deprecateLicenseUrl') { '' }
        else { throw "No license text or original license link for $name" }
    }
    $notices = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -match '^(THIRD.PARTY.NOTICES|NOTICE|NOTICES)(\.(txt|md))?$' | Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n`n"
    if (Test-Path -LiteralPath (Join-Path $pinned 'NOTICE.txt')) { $notices += "`n" + (Get-Content -LiteralPath (Join-Path $pinned 'NOTICE.txt') -Raw) }
    $components.Add([ordered]@{ Name=$name; Version=$version; License=$(if($licenseNode.type -eq 'expression'){$licenseNode.InnerText}else{'License terms'}); Text=([string]$license).Trim(); Notices=$notices.Trim(); Source=$(if(!$license){$metadata.licenseUrl}else{"https://www.nuget.org/packages/$name/$version"}) })
}
foreach ($property in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    $library = $property.Value
    if ($library.type -ne 'package' -or $property.Name -match '^Microsoft.Windows.SDK.BuildTools|(^|\.)Microsoft.DotNet.ILCompiler/|^Microsoft.NET.ILLink.Tasks/') { continue }
    $package = $property.Name.Split('/')
    $folder = $folders | ForEach-Object { Join-Path $_ $library.path } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    Add-Package $package[0] $package[1] $folder
}
# The self-contained/AOT runtime is resolved by the SDK rather than the project's normal library list.
$runtimePackage = 'Microsoft.NETCore.App.Runtime.' + $Runtime
$runtimeDependency = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -eq $runtimePackage | Select-Object -First 1
$runtimeVersion = if ($runtimeDependency) { [regex]::Match($runtimeDependency.version,'\d+\.\d+\.\d+').Value } else { '' }
if (!$runtimeVersion) { throw 'Cannot resolve the runtime version from restored assets' }
$runtimeFolder = Join-Path $folders[0] ($runtimePackage.ToLowerInvariant() + '\' + $runtimeVersion)
Add-Package $runtimePackage $runtimeVersion $runtimeFolder
$projection = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -eq 'Microsoft.Windows.SDK.NET.Ref' | Select-Object -First 1
if ($projection) { $version = [regex]::Match($projection.version,'\d+\.\d+\.\d+\.\d+').Value; Add-Package $projection.name $version (Join-Path $folders[0] "microsoft.windows.sdk.net.ref\$version") }
$output = Join-Path $repo 'src\CodexMonitor.Core\Licenses\components.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$components | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $output -Encoding utf8
"Recorded $($components.Count) components with original license text and notices."
