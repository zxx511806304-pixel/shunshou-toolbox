param([string]$Dotnet = '', [switch]$CollectLicensesOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Dotnet) { $Dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe' }
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget' }
$output = Assert-ChildPath -Root (Join-Path $repoRoot 'runtime') -Path (Join-Path $repoRoot 'runtime/ai-inpaint/runner')
if (-not $CollectLicensesOnly) {
    & $Dotnet publish (Join-Path $repoRoot 'src/Shunshou.AiRunner/Shunshou.AiRunner.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -p:DebugType=None -p:DebugSymbols=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'AI runner publish failed.' }
    # This executable has its own Windows DLL search directory. The app's parent
    # runtime is not searched automatically on a clean machine.
    $vcRuntime = Join-Path $repoRoot 'runtime/vcredist/bin'
    if (-not (Test-Path -LiteralPath (Join-Path $vcRuntime 'msvcp140.dll'))) { throw 'Prepare the verified Visual C++ runtime before building the AI runner.' }
    Get-ChildItem -LiteralPath $vcRuntime -Filter '*.dll' -File | Copy-Item -Destination $output -Force
}
$licenseDirectory = Join-Path $repoRoot 'runtime/ai-inpaint/licenses/runner'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Shunshou.AiRunner/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$entries = [Collections.Generic.List[object]]::new()
$licenseIndex = [Collections.Generic.List[object]]::new()
foreach ($key in ($assets.libraries.Keys | Sort-Object)) {
    $library = $assets.libraries[$key]
    if ($library.type -ne 'package') { continue }
    $package = $null
    foreach ($cache in $assets.packageFolders.Keys) {
        $candidate = Join-Path $cache $library.path
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if (-not $package) { throw "Restored AI runner package is missing: $key" }
    $target = Assert-ChildPath -Root $licenseDirectory -Path (Join-Path $licenseDirectory $library.path)
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $nuspec = Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File | Select-Object -First 1
    if (-not $nuspec) { throw "AI runner package has no source metadata: $key" }
    [xml]$xml = Get-Content -Raw -LiteralPath $nuspec.FullName
    $metadata = $xml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $legacyLicenseNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    $projectNode = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
    $authorsNode = $metadata.SelectSingleNode("*[local-name()='authors']")
    $copyrightNode = $metadata.SelectSingleNode("*[local-name()='copyright']")
    $licenseFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -match '(?i)(^licen[sc]e|^copying|^notice|third.?party.*notice|\.nuspec$)' })
    foreach ($file in $licenseFiles) {
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
        $destination = Assert-ChildPath -Root $target -Path (Join-Path $target $relative)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    $licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } elseif ($legacyLicenseNode) { 'url' } else { '' }
    $licenseText = if ($licenseNode) { $licenseNode.InnerText } elseif ($legacyLicenseNode) { $legacyLicenseNode.InnerText } else { '' }
    if ($licenseType -eq 'file') {
        $declaredLicense = Assert-ChildPath -Root $package -Path (Join-Path $package $licenseText)
        if (-not (Test-Path -LiteralPath $declaredLicense)) { throw "Declared AI runner license is missing: $key / $licenseText" }
        Copy-Item -LiteralPath $declaredLicense -Destination (Join-Path $target ('DECLARED-' + [IO.Path]::GetFileName($declaredLicense))) -Force
    }
    $record = [ordered]@{
        Package=$key; LicenseType=$licenseType; License=$licenseText
        Authors=$(if ($authorsNode) { $authorsNode.InnerText } else { '' })
        Copyright=$(if ($copyrightNode) { $copyrightNode.InnerText } else { '' })
        ProjectUrl=$(if ($projectNode) { $projectNode.InnerText } else { '' })
        Repository=$(if ($repositoryNode) { $repositoryNode.GetAttribute('url') } else { '' })
        SourceCommit=$(if ($repositoryNode) { $repositoryNode.GetAttribute('commit') } else { '' })
        Files=@($licenseFiles | ForEach-Object { [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/') })
        Note='Restored dependencies of the isolated AI runner; may include build-only or other-platform assets.'
    }
    $record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $target 'package-license.json') -Encoding utf8
    $licenseIndex.Add($record)
    $entries.Add([ordered]@{ Package=$key; Path=$library.path })
}
$entries | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $licenseDirectory 'packages.json') -Encoding utf8
$licenseIndex | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $licenseDirectory 'nuget-license-index.json') -Encoding utf8
Write-Host "Collected AI runner provenance for $($licenseIndex.Count) restored dependencies."
Write-Host "Private AI runner: $output"
