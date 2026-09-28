param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath = Join-Path $PSScriptRoot 'sttn-runtime-lock.json'
$runtimeLock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
$downloads = Join-Path $repoRoot '.tools/downloads'
$sourceRoot = Join-Path $repoRoot '.tools/sources/sttn'
$runtimeRoot = Join-Path $repoRoot 'runtime/ai-inpaint'
$licenseRoot = Join-Path $runtimeRoot 'licenses'
New-Item -ItemType Directory -Force -Path $downloads,$sourceRoot,$licenseRoot | Out-Null
$archivePath = Join-Path $downloads $runtimeLock.Source.CacheFile
$checkpoint = Join-Path $downloads $runtimeLock.Checkpoint.File
Get-VerifiedDownload -Uri $runtimeLock.Source.Url -Path $archivePath -Sha256 $runtimeLock.Source.Sha256
Get-VerifiedDownload -Uri $runtimeLock.Checkpoint.Url -Path $checkpoint -Sha256 $runtimeLock.Checkpoint.Sha256
if ((Get-Item -LiteralPath $checkpoint).Length -ne $runtimeLock.Checkpoint.Bytes) { throw 'STTN checkpoint length mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $slash = $entry.FullName.IndexOf('/')
        if ($slash -lt 0) { throw 'Unexpected STTN source archive structure.' }
        $relative = $entry.FullName.Substring($slash + 1)
        $destination = Assert-ChildPath -Root $sourceRoot -Path (Join-Path $sourceRoot $relative)
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        if (Test-Path -LiteralPath $destination) {
            $stream = $entry.Open()
            try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expected) { throw "STTN source differs from pinned upstream; keep modifications in an export wrapper: $relative" }
        } else { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false) }
    }
} finally { $archive.Dispose() }
$licensePath = Join-Path $licenseRoot $runtimeLock.LicenseFile.File
Get-VerifiedDownload -Uri $runtimeLock.LicenseFile.Url -Path $licensePath -Sha256 $runtimeLock.LicenseFile.Sha256
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $repoRoot '.tools/sources/sttn-provenance.json') -Force
Write-Host "STTN source: $sourceRoot"
Write-Host "STTN original checkpoint: $checkpoint ($($runtimeLock.Checkpoint.Bytes) bytes). ONNX export is a separate build step."
