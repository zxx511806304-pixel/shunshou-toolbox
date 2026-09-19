param(
    [string]$Version = '1.0.1',
    [Parameter(Mandatory)][string]$BasePackage,
    [Parameter(Mandatory)][string]$BaseSha256,
    [string]$OutputRoot = 'dist'
)
# Reuses verified, unchanged offline engines; compiles all application source afresh.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if ($BaseSha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $BasePackage -Algorithm SHA256).Hash -ne $BaseSha256) { throw 'Base package checksum mismatch.' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$name = "ShunshouToolbox-$Version-win-x64"
$final = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot $name)
if ((Test-Path -LiteralPath $final) -or (Test-Path -LiteralPath ($final + '.zip'))) { throw 'Release output already exists.' }
$stage = Assert-ChildPath -Root $repoRoot -Path (Join-Path $repoRoot ('artifacts/incremental-' + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Force -Path $stage,$OutputRoot | Out-Null
$dotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget'
try {
    [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($BasePackage), $stage)
    $baseRoots = @(Get-ChildItem -LiteralPath $stage -Directory)
    if ($baseRoots.Count -ne 1) { throw 'Unexpected base package layout.' }
    $package = Assert-ChildPath -Root $stage -Path $baseRoots[0].FullName
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $package 'package-manifest.json') | ConvertFrom-Json
    if ($manifest.Product -ne '顺手工具箱' -or $manifest.Architecture -ne 'win-x64') { throw 'Wrong base product.' }
    foreach ($entry in $manifest.Files) {
        $file = Assert-ChildPath -Root $package -Path (Join-Path $package $entry.Path)
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Invalid base file: $($entry.Path)" }
    }
    $appStage = Join-Path $package 'app'
    & $dotnet publish (Join-Path $repoRoot 'src/Shunshou.App/Shunshou.App.csproj') -c Release -r win-x64 --self-contained true --no-restore `
        '-p:Platform=x64' '-p:WindowsAppSDKSelfContained=true' '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' `
        '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$Version" -o $appStage --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    foreach ($resource in @('App.xbf','MainWindow.xbf','Shunshou.App.pri','tools/everything/Everything.exe','tools/everything/es.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $appStage $resource))) { throw "Missing file: $resource" }
    }
    $launcher = Assert-ChildPath -Root $package -Path (Join-Path $package 'ShunshouToolbox.exe')
    Remove-Item -LiteralPath $launcher
    & (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -OutputPath $launcher -Version $Version
    $docsStage = Join-Path $package 'docs'
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') | Copy-Item -Destination $docsStage -Recurse -Force
    foreach ($file in @('CHANGELOG.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $docsStage -Force }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $docsStage 'Usage.md') -Force
    & (Join-Path $PSScriptRoot 'Build-ComponentCatalog.ps1') -DocsDirectory $docsStage -RuntimeDirectory (Join-Path $appStage 'tools')
    foreach ($file in Get-ChildItem -LiteralPath $appStage -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name.EndsWith('.runtimeconfig.dev.json') }) {
        Remove-Item -LiteralPath (Assert-ChildPath -Root $package -Path $file.FullName)
    }
    $files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object FullName -ne (Join-Path $package 'package-manifest.json') | ForEach-Object {
        [ordered]@{ Path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/'); Bytes=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ Product='顺手工具箱'; Version=$Version; Architecture='win-x64'; BuiltUtc=[DateTime]::UtcNow.ToString('o');
        BasePackage=[IO.Path]::GetFileName($BasePackage); BaseSha256=$BaseSha256; Files=$files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
    [IO.Directory]::Move($package, $final)
    [IO.Compression.ZipFile]::CreateFromDirectory($final, $final + '.zip', [IO.Compression.CompressionLevel]::Optimal, $true)
    (Get-FileHash -LiteralPath ($final + '.zip') -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name + '.zip' |
        Set-Content -LiteralPath ($final + '.zip.sha256') -Encoding ascii
    Write-Host "Package: $final.zip"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath (Assert-ChildPath -Root $repoRoot -Path $stage) -Recurse -Force }
}
