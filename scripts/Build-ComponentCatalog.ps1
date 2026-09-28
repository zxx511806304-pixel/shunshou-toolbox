param(
    [Parameter(Mandatory)][string]$DocsDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DocsDirectory = [IO.Path]::GetFullPath($DocsDirectory)
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$spec = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src/Shunshou.App/Assets/ComponentSources.json') | ConvertFrom-Json -AsHashtable
$indexPath = Join-Path $DocsDirectory 'licenses/nuget-license-index.json'
if (-not (Test-Path -LiteralPath $indexPath)) { throw 'Collect NuGet licenses before building the component catalog.' }
$index = @(Get-Content -Raw -LiteralPath $indexPath | ConvertFrom-Json -AsHashtable)
$versions = @{}
foreach ($entry in $index) { $parts = $entry['Package'].Split('/'); $versions[$parts[0]] = $parts[1] }
$runtimeLock = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'scripts/runtime-lock.json') | ConvertFrom-Json -AsHashtable
$videoLockPath = Join-Path $RuntimeDirectory 'video-download/runtime-lock.json'
$videoLock = if (Test-Path -LiteralPath $videoLockPath) { Get-Content -Raw -LiteralPath $videoLockPath | ConvertFrom-Json -AsHashtable } else { @{} }
$rows = [Collections.Generic.List[object]]::new()
function Assert-ProjectUrl([string]$Value) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('https','http')) { throw "Invalid component project URL: $Value" }
}
foreach ($source in $spec.Components) {
    $row = [ordered]@{}
    foreach ($key in @('Id','Name','Group','Purpose','Version','License','ProjectUrl','AdditionalUrl')) { if ($source[$key]) { $row[$key] = $source[$key] } }
    if ($source['NuGetPackage']) {
        if (-not $versions.ContainsKey($source['NuGetPackage'])) { throw "Component NuGet package not found: $($source['NuGetPackage'])" }
        $row['Version'] = $versions[$source['NuGetPackage']]
    }
    elseif ($source['VersionSource'] -like 'video:*') { $key = $source['VersionSource'].Substring(6); $row['Version'] = $videoLock[$key].Version }
    elseif ($source['VersionSource'] -eq 'ffmpeg') {
        $ffmpeg = Get-Content -Raw -LiteralPath (Join-Path $RuntimeDirectory 'ffmpeg/build-source.json') | ConvertFrom-Json
        $row['Version'] = $ffmpeg.Name -replace '^ffmpeg-','' -replace '-win64.*$',''
    }
    elseif ($source['VersionSource'] -eq 'photorec') { $row['Version'] = (Get-Content -Raw -LiteralPath (Join-Path $RuntimeDirectory 'recovery/build-source.json') | ConvertFrom-Json).Version }
    elseif ($source['VersionSource'] -eq 'sevenzip') { $row['Version'] = (Get-Content -Raw -LiteralPath (Join-Path $RuntimeDirectory 'sevenzip/build-source.json') | ConvertFrom-Json).Version }
    elseif ($source['VersionSource'] -eq 'visualCpp') { $row['Version'] = $runtimeLock.visualCpp.Version }
    elseif ($source['VersionSource'] -eq 'dotnet') {
        $configPath = Join-Path (Split-Path -Parent $RuntimeDirectory) 'Shunshou.App.runtimeconfig.json'
        if (Test-Path -LiteralPath $configPath) {
            $config = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
            $row['Version'] = ($config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App' | Select-Object -First 1).version
        }
        if (-not $row['Version']) { $row['Version'] = '运行时版本由应用显示' }
    }
    if (-not $row['Version']) { throw "Missing actual component version: $($row['Name'])" }
    Assert-ProjectUrl $row['ProjectUrl']
    if ($row['AdditionalUrl']) { Assert-ProjectUrl $row['AdditionalUrl'] }
    $rows.Add($row)
}
# Optional/new engines register themselves without needing UI code changes.
# component-source.json accepts a single row or { Components: [rows] }; every row
# has Name, Version, License, ProjectUrl, and optional Id/Group/Purpose/AdditionalUrl.
foreach ($folder in Get-ChildItem -LiteralPath $RuntimeDirectory -Directory) {
    $manifestPath = Join-Path $folder.FullName 'component-source.json'
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -AsHashtable
        $components = if ($manifest.ContainsKey('Components')) { @($manifest.Components) } else { @($manifest) }
        foreach ($component in $components) {
            foreach ($required in @('Name','Version','License','ProjectUrl')) { if (-not $component[$required]) { throw "$manifestPath is missing $required" } }
            Assert-ProjectUrl $component['ProjectUrl']
            if ($component['AdditionalUrl']) { Assert-ProjectUrl $component['AdditionalUrl'] }
            if (-not $component['Group']) { $component['Group'] = '其他处理组件' }
            $rows.Add($component)
        }
    }
    elseif ($folder.Name -notin $spec.KnownRuntimeFolders) { throw "New runtime requires a component-source.json attribution manifest: $($folder.Name)" }
}
$inventoryEntries = @{}
function Add-DependencyRecords($Records, [string]$Origin) {
    foreach ($entry in $Records) {
        if (-not $entry['Package'] -or $entry['Package'].Split('/').Count -ne 2) { throw "Invalid dependency identity in $Origin" }
        if (-not $inventoryEntries.ContainsKey($entry['Package'])) {
            $inventoryEntries[$entry['Package']] = @{ Entry=$entry; Origins=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase) }
        }
        [void]$inventoryEntries[$entry['Package']].Origins.Add($Origin)
    }
}
Add-DependencyRecords $index '主程序'
# Independent engines publish the same small provenance-index contract. Discover
# all of them rather than hard-coding the current AI runner or its dependency versions.
foreach ($dependencyIndex in Get-ChildItem -LiteralPath $RuntimeDirectory -Recurse -File -Filter 'nuget-license-index.json') {
    $relative = [IO.Path]::GetRelativePath($RuntimeDirectory, $dependencyIndex.FullName).Replace('\','/')
    $engineName = $relative.Split('/')[0]
    $origin = if ($engineName -eq 'ai-inpaint') { 'AI 推理组件' } else { '独立组件：' + $engineName }
    $engineEntries = @(Get-Content -Raw -LiteralPath $dependencyIndex.FullName | ConvertFrom-Json -AsHashtable)
    Add-DependencyRecords $engineEntries $origin
}
$inventory = @(foreach ($key in ($inventoryEntries.Keys | Sort-Object)) {
    $item = $inventoryEntries[$key]
    $entry = $item.Entry
    $parts = $entry['Package'].Split('/')
    $url = if ($entry['Repository'] -match '^https?://') { $entry['Repository'] } elseif ($entry['ProjectUrl'] -match '^https?://') { $entry['ProjectUrl'] } else { 'https://www.nuget.org/packages/' + $parts[0] + '/' + $parts[1] }
    [ordered]@{ Name=$parts[0]; Version=$parts[1]; License=$(if ($entry['LicenseType'] -eq 'file') { '许可原文：' + $entry['License'] } elseif ($entry['License']) { $entry['License'] } else { '见随包原始声明' }); ProjectUrl=$url; Purpose=('来源：' + ((@($item.Origins) | Sort-Object) -join ' / ')); Origins=@($item.Origins) }
})
[ordered]@{ SchemaVersion=1; Components=@($rows); References=@($spec.References); Dependencies=@($inventory) } |
    ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $DocsDirectory 'components.json') -Encoding utf8
Write-Host "Component catalog: $($rows.Count) functional entries and $($inventory.Count) distinct package/version dependency records."
