param(
    [string]$Version = '0.5.1',
    [string]$PayloadZip = '',
    [string]$OutputRoot = '',
    [string]$Dotnet = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric version, for example 0.3.0.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$name = "ShunshouToolbox-$Version-win-x64"
if ([string]::IsNullOrWhiteSpace($PayloadZip)) { $PayloadZip = Join-Path $repoRoot "dist/$name.zip" }
$PayloadZip = [IO.Path]::GetFullPath($PayloadZip)
if (-not (Test-Path -LiteralPath $PayloadZip -PathType Leaf)) { throw "Portable ZIP is missing: $PayloadZip. Run Build-Portable.ps1 first." }
if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/dotnet-home' }
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath (Join-Path $repoRoot '.tools/nuget'))) { $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget' }
$final = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot ($name + '-setup.exe'))
if ((Test-Path -LiteralPath $final) -or (Test-Path -LiteralPath ($final + '.sha256'))) {
    throw "Output already exists. Choose a new -Version or -OutputRoot; no previous package will be deleted: $final"
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$stage = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot ('.setup-build-' + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $stage | Out-Null
$payloadStream = $null
$archive = $null
try {
    # Keep this handle open through publishing, so the ZIP cannot change after hashing.
    $payloadStream = [IO.File]::Open($PayloadZip, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = [Convert]::ToHexString($hasher.ComputeHash($payloadStream)).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    $payloadStream.Position = 0
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipArchive]::new($payloadStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    $manifestName = "$name/package-manifest.json"
    $manifestEntries = @($archive.Entries | Where-Object { $_.FullName -ceq $manifestName })
    if ($manifestEntries.Count -ne 1) { throw "ZIP must contain exactly one $manifestName." }
    $reader = [IO.StreamReader]::new($manifestEntries[0].Open(), [Text.Encoding]::UTF8)
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    if ($manifest.Product -ne '顺手工具箱' -or $manifest.Version -ne $Version -or $manifest.Architecture -ne 'win-x64') {
        throw 'Portable manifest product, version or architecture does not match this setup build.'
    }
    if (@($archive.Entries | Where-Object { -not $_.FullName.StartsWith("$name/", [StringComparison]::Ordinal) }).Count -ne 0) {
        throw 'Portable ZIP contains files outside its expected product folder.'
    }
    $archive.Dispose()
    $archive = $null
    $metadataPath = Join-Path $stage 'payload.json'
    [ordered]@{
        Product = '顺手工具箱'
        Version = $Version
        Architecture = 'win-x64'
        ZipRoot = $name
        ZipBytes = $payloadStream.Length
        Sha256 = $payloadHash
    } | ConvertTo-Json -Depth 4 | ForEach-Object { [IO.File]::WriteAllText($metadataPath, $_, [Text.UTF8Encoding]::new($false)) }
    $publish = Join-Path $stage 'publish'
    & $Dotnet publish (Join-Path $repoRoot 'src/Shunshou.Setup/Shunshou.Setup.csproj') -c Release -r win-x64 --self-contained true `
        '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' `
        '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$Version" `
        "-p:PayloadZip=$PayloadZip" "-p:PayloadMetadata=$metadataPath" -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Setup dotnet publish failed.' }
    $hostPath = Join-Path $publish 'Shunshou.Setup.exe'
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'Published setup apphost is missing.' }
    $sidecars = @(Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Name -ne 'Shunshou.Setup.exe' })
    if ($sidecars.Count -ne 0 -or @(Get-ChildItem -LiteralPath $publish -Directory).Count -ne 0) {
        throw 'Setup publish is not a standalone EXE. Inspect the publish output before distribution.'
    }
    $checksumPath = Join-Path $stage 'setup.sha256'
    (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($final) |
        Set-Content -LiteralPath $checksumPath -Encoding ascii
    # The final move is non-overwriting even if another build finished while we published.
    [IO.File]::Move($hostPath, $final, $false)
    [IO.File]::Move($checksumPath, ($final + '.sha256'), $false)
    Write-Host "Offline setup: $final"
    Write-Host "Embedded portable ZIP SHA256: $payloadHash"
    Write-Host 'This is a full unsigned package. Verify the exact EXE in an isolated test directory before release.'
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $payloadStream) { $payloadStream.Dispose() }
    $safe = Assert-ChildPath -Root $OutputRoot -Path $stage
    if (Test-Path -LiteralPath $safe) { Remove-Item -LiteralPath $safe -Recurse -Force }
}
