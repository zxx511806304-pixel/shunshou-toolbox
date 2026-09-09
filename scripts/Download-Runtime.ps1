param([switch]$SkipVisualCpp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'runtime-lock.json') | ConvertFrom-Json
$downloadRoot = Join-Path $repoRoot '.tools/downloads'
$ffmpegRoot = Join-Path $repoRoot 'runtime/ffmpeg'
$ffmpegZip = Join-Path $downloadRoot 'ffmpeg-fixed.zip'
Get-VerifiedDownload -Uri $lock.ffmpeg.Url -Path $ffmpegZip -Sha256 $lock.ffmpeg.Sha256
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($ffmpegZip)
try {
    $engineEntry = @($archive.Entries | Where-Object { $_.FullName.EndsWith('/bin/ffmpeg.exe') })
    if ($engineEntry.Count -ne 1) { throw 'Unexpected FFmpeg archive structure.' }
    $prefix = $engineEntry[0].FullName.Substring(0, $engineEntry[0].FullName.Length - 'bin/ffmpeg.exe'.Length)
    foreach ($entry in $archive.Entries) {
        if (-not $entry.FullName.StartsWith($prefix) -or $entry.FullName.EndsWith('/')) { continue }
        $relative = $entry.FullName.Substring($prefix.Length)
        $keep = $relative -eq 'LICENSE.txt' -or $relative.StartsWith('doc/') -or
            $relative -match '^bin/(ffmpeg\.exe|ffprobe\.exe|[^/]+\.dll)$'
        if (-not $keep) { continue }
        $destination = Assert-ChildPath -Root $ffmpegRoot -Path (Join-Path $ffmpegRoot $relative)
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
    }
}
finally { $archive.Dispose() }
$ffmpegRecord = [ordered]@{}
foreach ($property in $lock.ffmpeg.PSObject.Properties) { $ffmpegRecord[$property.Name] = $property.Value }
$ffmpegRecord['Files'] = @(Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot 'bin') -File | Where-Object { $_.Name -ne 'ffplay.exe' } | ForEach-Object {
    [ordered]@{ Path=('bin/' + $_.Name); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$ffmpegRecord | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ffmpegRoot 'build-source.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses/GPL-3.0.txt') -Destination (Join-Path $ffmpegRoot 'COPYING.GPLv3') -Force
Write-Host 'Pinned FFmpeg LGPL shared engine, license and original documentation are ready.'

if ($SkipVisualCpp) { return }
$redist = Join-Path $downloadRoot 'vc_redist.x64.exe'
Get-VerifiedDownload -Uri $lock.visualCpp.Url -Path $redist -Sha256 $lock.visualCpp.Sha256
$signature = Get-AuthenticodeSignature -LiteralPath $redist
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') { throw 'Microsoft redistributable signature validation failed.' }
$jobRoot = Join-Path $repoRoot '.tools/runtime-extraction'
$job = Join-Path $jobRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $job | Out-Null
try {
    # Read the two signed Burn CAB containers; never execute the installer or invoke MSI installation.
    $bytes = [IO.File]::ReadAllBytes($redist)
    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    $offset = 0
    $cabinets = [Collections.Generic.List[string]]::new()
    while (($offset = $ascii.IndexOf('MSCF', $offset, [StringComparison]::Ordinal)) -ge 0) {
        $length = [BitConverter]::ToUInt32($bytes, $offset + 8)
        if ($length -lt 36 -or $offset + [long]$length -gt $bytes.Length) { throw 'Invalid embedded Microsoft CAB boundary.' }
        $cab = Join-Path $job ($cabinets.Count.ToString() + '.cab')
        $stream = [IO.File]::Create($cab)
        try { $stream.Write($bytes, $offset, [int]$length) } finally { $stream.Dispose() }
        $cabinets.Add($cab)
        $offset += [int]$length
    }
    if ($cabinets.Count -ne 2) { throw 'The pinned Microsoft redistributable has an unexpected container layout.' }
    $bootstrap = Join-Path $job 'bootstrap'
    $payload = Join-Path $job 'payload'
    $crt = Join-Path $job 'crt'
    New-Item -ItemType Directory -Force -Path $bootstrap,$payload,$crt | Out-Null
    & "$env:WINDIR/System32/expand.exe" '-F:*' $cabinets[0] $bootstrap | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to extract Microsoft bootstrap CAB.' }
    & "$env:WINDIR/System32/expand.exe" '-F:*' $cabinets[1] $payload | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to extract Microsoft payload CAB.' }
    [xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $bootstrap '0')
    $minimum = $manifest.SelectSingleNode("//*[local-name()='Payload' and contains(@FilePath,'vcRuntimeMinimum_amd64') and contains(@FilePath,'cab1.cab')]")
    if (-not $minimum) { throw 'Missing x64 C++ runtime cabinet in signed manifest.' }
    & "$env:WINDIR/System32/expand.exe" '-F:*' (Join-Path $payload $minimum.SourcePath) $crt | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to extract x64 C++ runtime DLLs.' }
    $vcRoot = Join-Path $repoRoot 'runtime/vcredist'
    $vcBin = Join-Path $vcRoot 'bin'
    New-Item -ItemType Directory -Force -Path $vcBin | Out-Null
    $names = @('concrt140.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','vcruntime140_threads.dll')
    foreach ($name in $names) {
        $source = Join-Path $crt ($name + '_amd64')
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing pinned C++ runtime DLL: $name" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $vcBin $name) -Force
    }
    foreach ($locale in @('license.rtf','2052\license.rtf')) {
        $license = $manifest.SelectSingleNode("//*[local-name()='UX']/*[local-name()='Payload' and @FilePath='$locale']")
        if (-not $license) { throw 'The Microsoft runtime license was not found.' }
        $name = if ($locale.StartsWith('2052')) { 'Microsoft-VC-License-zh-CN.rtf' } else { 'Microsoft-VC-License-en-US.rtf' }
        Copy-Item -LiteralPath (Join-Path $bootstrap $license.SourcePath) -Destination (Join-Path $vcRoot $name) -Force
    }
    [ordered]@{
        Version=$lock.visualCpp.Version; Url=$lock.visualCpp.Url; Sha256=$lock.visualCpp.Sha256
        Method='Read signed embedded CAB files and extract x64 runtime only; installer never executed.'
        Files=@(Get-ChildItem -LiteralPath $vcBin -File | ForEach-Object { [ordered]@{ Path=('bin/' + $_.Name); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $vcRoot 'build-source.json') -Encoding utf8
    Write-Host 'Microsoft C++ runtime extracted for app-local deployment; no system runtime was installed.'
}
finally {
    $safe = Assert-ChildPath -Root $jobRoot -Path $job
    if (Test-Path -LiteralPath $safe) { Remove-Item -LiteralPath $safe -Recurse -Force }
}
