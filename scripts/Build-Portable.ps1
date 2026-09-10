param(
    [string]$Version = '0.2.2',
    [string]$OutputRoot = '',
    [string]$Dotnet = '',
    [switch]$SkipZip
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Use a semantic version, for example 0.1.0 or 0.1.0-preview.2.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home' }
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath (Join-Path $repoRoot '.tools/nuget'))) { $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget' }
$name = "ShunshouToolbox-$Version-win-x64"
$final = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot $name)
$zip = $final + '.zip'
if ((Test-Path -LiteralPath $final) -or (Test-Path -LiteralPath $zip)) { throw "Output already exists. Choose a new -Version or -OutputRoot; no previous package will be deleted: $final" }
foreach ($required in @('runtime/ffmpeg/bin/ffmpeg.exe','runtime/ffmpeg/bin/ffprobe.exe','runtime/ffmpeg/build-source.json','runtime/vcredist/bin/msvcp140.dll','runtime/vcredist/bin/vcruntime140_1.dll','runtime/ocr/v6/PP-OCRv6_det_small.onnx','runtime/ocr/v6/PP-OCRv6_rec_small.onnx')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $required))) { throw "Missing offline runtime: $required. Run Download-Runtime.ps1 and Download-OcrModels.ps1 first." }
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$stage = Join-Path $OutputRoot ('.build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    & $Dotnet publish (Join-Path $repoRoot 'src/Shunshou.App/Shunshou.App.csproj') -c Release -r win-x64 --self-contained true `
        '-p:Platform=x64' '-p:WindowsAppSDKSelfContained=true' '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' `
        '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$Version" -o $stage --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    foreach ($resource in @('App.xbf','MainWindow.xbf','InputFileList.xbf','OcrWorkspace.xbf','UninstallerWorkspace.xbf','Shunshou.App.pri')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $resource))) { throw "Published WinUI resource is missing: $resource" }
    }
    $hostPath = Join-Path $stage 'Shunshou.App.exe'
    if (-not (Test-Path -LiteralPath $hostPath)) { throw 'Published apphost is missing.' }
    # Apphost embeds the original managed DLL name. Keep that DLL/runtimeconfig and the original host.
    Copy-Item -LiteralPath $hostPath -Destination (Join-Path $stage 'ShunshouToolbox.exe')
    # Unpackaged WinUI resolves its resource index from the running apphost name.
    Copy-Item -LiteralPath (Join-Path $stage 'Shunshou.App.pri') -Destination (Join-Path $stage 'ShunshouToolbox.pri')
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'ShunshouToolbox.pri'))) { throw 'Product apphost resource index is missing.' }
    Copy-Item -Path (Join-Path $repoRoot 'runtime/vcredist/bin/*.dll') -Destination $stage -Force
    $vcLicenses = Join-Path $stage 'licenses/Microsoft.VisualCpp'
    New-Item -ItemType Directory -Force -Path $vcLicenses | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'runtime/vcredist') -File | Copy-Item -Destination $vcLicenses
    foreach ($relative in @('tools/ffmpeg/bin/ffplay.exe','tools/vcredist','Microsoft.Windows.Workloads.Resources_ec.dll')) {
        $unneeded = Assert-ChildPath -Root $stage -Path (Join-Path $stage $relative)
        if (Test-Path -LiteralPath $unneeded) { Remove-Item -LiteralPath $unneeded -Recurse -Force }
    }
    # Remove only incompatible platform payloads in this newly generated staging tree.
    $runtimes = Join-Path $stage 'runtimes'
    if (Test-Path -LiteralPath $runtimes) {
        foreach ($folder in Get-ChildItem -LiteralPath $runtimes -Directory) {
            if ($folder.Name -notin @('win-x64','win10-x64','win')) {
                $safe = Assert-ChildPath -Root $stage -Path $folder.FullName
                Remove-Item -LiteralPath $safe -Recurse -Force
            }
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name.EndsWith('.runtimeconfig.dev.json') }) {
        $safe = Assert-ChildPath -Root $stage -Path $file.FullName
        Remove-Item -LiteralPath $safe
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'Usage.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $stage
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'docs')) { Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $stage -Recurse }
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md') -Destination $stage }
    & (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -Destination (Join-Path $stage 'licenses')
    & (Join-Path $PSScriptRoot 'Inspect-NativeDependencies.ps1') -Directory $stage -ReportPath (Join-Path $stage 'native-dependencies.json')
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        [ordered]@{ Path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/'); Bytes=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ Product='顺手工具箱'; Version=$Version; Architecture='win-x64'; BuiltUtc=[DateTime]::UtcNow.ToString('o'); Files=$files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding utf8
    [IO.Directory]::Move($stage, $final)
    if (-not $SkipZip) {
        Add-Type -AssemblyName System.IO.Compression
        [IO.Compression.ZipFile]::CreateFromDirectory($final, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) |
            Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
        Write-Host "Portable archive: $zip"
    }
    Write-Host "Portable folder: $final"
    Write-Host 'Build success is a structural check. Launch this exact folder and verify UI and offline functions before release.'
}
finally {
    $safe = Assert-ChildPath -Root $OutputRoot -Path $stage
    if (Test-Path -LiteralPath $safe) { Remove-Item -LiteralPath $safe -Recurse -Force }
}
