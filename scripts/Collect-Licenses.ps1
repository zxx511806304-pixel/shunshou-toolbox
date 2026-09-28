param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetsPath = Join-Path $repoRoot 'src/Shunshou.App/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath)) { throw 'Restore/build the application before collecting NuGet licenses.' }
$assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json -AsHashtable
$packageRoots = @($assets.packageFolders.Keys)
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'licenses/*.txt') -Destination $Destination -Force
$records = [Collections.Generic.List[object]]::new()
foreach ($key in ($assets.libraries.Keys | Sort-Object)) {
    $library = $assets.libraries[$key]
    if ($library.type -ne 'package') { continue }
    $source = $null
    foreach ($cache in $packageRoots) {
        $candidate = Join-Path $cache $library.path
        if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
    }
    if (-not $source) { throw "Restored package is missing: $key" }
    $packageDestination = Assert-ChildPath -Root $Destination -Path (Join-Path $Destination ('nuget/' + $key))
    New-Item -ItemType Directory -Force -Path $packageDestination | Out-Null
    $nuspec = Get-ChildItem -LiteralPath $source -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$xml = Get-Content -Raw -LiteralPath $nuspec.FullName
    $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $copyrightNode = $metadata.SelectSingleNode("*[local-name()='copyright']")
    $authorsNode = $metadata.SelectSingleNode("*[local-name()='authors']")
    $projectNode = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    Copy-Item -LiteralPath $nuspec.FullName -Destination $packageDestination
    $licenseFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Name -match '(?i)(^licen[sc]e|^copying|^notice|third.?party.*notice|sdk_license)' })
    foreach ($file in $licenseFiles) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $target = Assert-ChildPath -Root $packageDestination -Path (Join-Path $packageDestination $relative)
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    $licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { '' }
    $licenseText = if ($licenseNode) { $licenseNode.InnerText } else { '' }
    if ($licenseType -eq 'file') {
        $original = Assert-ChildPath -Root $source -Path (Join-Path $source $licenseText)
        if (-not (Test-Path -LiteralPath $original)) { throw "Declared license file is missing in $key" }
        Copy-Item -LiteralPath $original -Destination (Join-Path $packageDestination ('DECLARED-' + [IO.Path]::GetFileName($original)))
    }
    elseif ($key -in @('DiscUtils.Core/0.16.13','DiscUtils.Ntfs/0.16.13','DiscUtils.Streams/0.16.13')) {
        # These NuGet packages declare MIT but omit its copyright text. Preserve the
        # upstream license from the exact source commit recorded in each nuspec.
        if ($licenseText -ne 'MIT' -or $repositoryNode.GetAttribute('commit') -ne '59d7cadab839c6d8dfcf52f8be5efe6d2ced190f') {
            throw "DiscUtils license snapshot needs to be reviewed for $key"
        }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses/DiscUtils-0.16.13.txt') -Destination (Join-Path $packageDestination 'LICENSE-UPSTREAM.txt')
    }
    elseif ($licenseType -eq 'expression' -and $licenseText -in @('MIT','Apache-2.0')) {
        if ($licenseText -eq 'MIT' -and $copyrightNode) {
            $text = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'licenses/MIT.txt')).Replace('Copyright (c) <year> <copyright holders>', $copyrightNode.InnerText)
            Set-Content -LiteralPath (Join-Path $packageDestination 'LICENSE-SPDX.txt') -Value $text -Encoding utf8
        }
        elseif ($licenseText -ne 'MIT') {
            Copy-Item -LiteralPath (Join-Path $repoRoot "licenses/$licenseText.txt") -Destination (Join-Path $packageDestination 'LICENSE-SPDX.txt')
        }
        elseif (-not ($licenseFiles | Where-Object { $_.Name -match '(?i)^license(\.|$)' })) { throw "MIT copyright notice needs a source for $key" }
    }
    $record = [ordered]@{
        Package=$key; LicenseType=$licenseType; License=$licenseText
        Authors=$(if ($authorsNode) { $authorsNode.InnerText } else { '' })
        Copyright=$(if ($copyrightNode) { $copyrightNode.InnerText } else { '' })
        ProjectUrl=$(if ($projectNode) { $projectNode.InnerText } else { '' })
        Repository=$(if ($repositoryNode) { $repositoryNode.GetAttribute('url') } else { '' })
        SourceCommit=$(if ($repositoryNode) { $repositoryNode.GetAttribute('commit') } else { '' })
        Files=@($licenseFiles | ForEach-Object { [IO.Path]::GetRelativePath($source,$_.FullName).Replace('\','/') })
        Note='NuGet dependency inventory may include build-only packages. Original declarations are preserved; this inventory is not a legal compliance certification.'
    }
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageDestination 'package-license.json') -Encoding utf8
    $records.Add($record)
}
# Framework runtime packs are not always listed under project.assets libraries.
foreach ($cache in $packageRoots) {
    foreach ($runtimeId in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        $directory = Join-Path $cache $runtimeId
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        foreach ($version in Get-ChildItem -LiteralPath $directory -Directory) {
            $target = Join-Path $Destination ('dotnet/' + $runtimeId + '/' + $version.Name)
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Get-ChildItem -LiteralPath $version.FullName -File | Where-Object { $_.Name -match '(?i)(license|notice)' } | Copy-Item -Destination $target
        }
    }
}
$records | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Destination 'nuget-license-index.json') -Encoding utf8
Write-Host "Collected license metadata and available full notices for $($records.Count) restored packages."
