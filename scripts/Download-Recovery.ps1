param([string]$OutputDirectory = '', [string]$SourceDirectory = '', [string]$PythonPath = 'python')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'runtime/recovery' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!$SourceDirectory) { $SourceDirectory = Join-Path $repoRoot '.tools/sources/recovery' }
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
if ($SourceDirectory.StartsWith($OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $SourceDirectory.Equals($OutputDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'SourceDirectory must be outside the application runtime directory.' }
$archive = Join-Path $repoRoot '.tools/downloads/testdisk-7.2.win64.zip'
$binaryHash = 'e97e203ce77b6b1a3a37d01beccf069dc6c4632b579ffbb82ae739cdda229f38'
$sourceHash = 'f8343be20cb4001c5d91a2e3bcd918398f00ae6d8310894a5a9f2feb813c283f'
$lockPath = Join-Path $PSScriptRoot 'recovery-runtime-lock.json'
$runtimeLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
Get-VerifiedDownload -Uri 'https://www.cgsecurity.org/testdisk-7.2.win64.zip' -Path $archive -Sha256 $binaryHash
$bin = Join-Path $OutputDirectory 'bin'
$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $bin,$licenses | Out-Null

function Remove-KnownRecoveryFile {
    param([string]$RelativePath, [string]$ExpectedHash)
    $target = Assert-ChildPath -Root $OutputDirectory -Path (Join-Path $OutputDirectory $RelativePath)
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $ExpectedHash) { throw "A previous generated recovery file was modified; refusing to remove it: $target" }
        Remove-Item -LiteralPath $target
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        $name = [IO.Path]::GetFileName($entry.FullName)
        if ($entry.FullName -ne "testdisk-7.2/$name") { continue }
        if ($name -eq 'testdisk_win.exe') {
            $stream = $entry.Open()
            try { $oldHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
            Remove-KnownRecoveryFile -RelativePath 'bin/testdisk_win.exe' -ExpectedHash $oldHash
            continue
        }
        if ($name -in @('COPYING.txt','AUTHORS.txt','THANKS.txt','readme.txt')) {
            $target = Assert-ChildPath -Root $licenses -Path (Join-Path $licenses "CGSecurity-7.2-$name")
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            Remove-KnownRecoveryFile -RelativePath "bin/$name" -ExpectedHash (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            continue
        }
        # Only PhotoRec and the source-matched libewf DLL are retained from this
        # archive. All other DLLs and terminfo come from the pinned Cygwin packages.
        if ($name -notin @('photorec_win.exe','cygewf-2.dll')) { continue }
        $target = Assert-ChildPath -Root $bin -Path (Join-Path $bin $name)
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $zip.Dispose() }
& (Join-Path $PSScriptRoot 'Download-RecoveryDependencies.ps1') -OutputDirectory $OutputDirectory -SourceDirectory $SourceDirectory -PythonPath $PythonPath
Remove-KnownRecoveryFile -RelativePath 'sources/testdisk-7.2.tar.bz2' -ExpectedHash $sourceHash
foreach ($file in @($runtimeLock.redistributedExecutable,$runtimeLock.retainedDll)) {
    $path = Assert-ChildPath -Root $bin -Path (Join-Path $bin $file.name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Recovery binary differs from its locked source: $path" }
}
[ordered]@{
    Component='PhotoRec'; Version='7.2'; Source='https://www.cgsecurity.org/wiki/TestDisk_Download'
    Archive='https://www.cgsecurity.org/testdisk-7.2.win64.zip'; ArchiveSHA256=$binaryHash
    SourceArchive='https://www.cgsecurity.org/testdisk-7.2.tar.bz2'; SourceSHA256=$sourceHash
    Executable=$runtimeLock.redistributedExecutable
    Integration='Unmodified standalone PhotoRec process; signature-based extraction into a separate destination. Original-name NTFS recovery uses the managed reader. TestDisk executable is not redistributed.'
    RuntimeLock='dependency-sources.json'
    RuntimeLockSHA256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'dependency-sources.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    RuntimePackages=@($runtimeLock.packages | ForEach-Object { [ordered]@{ Package=$_.package; Version=$_.version } })
    SourceDistribution='RecoverySources-<toolbox-version>.zip is provided beside the binary package; source archives are not copied into the application runtime.'
    UpstreamHashList='https://www.cgsecurity.org/testdisk_sha256.txt'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-source.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Inspect-NativeDependencies.ps1') -Directory $bin -ReportPath (Join-Path $OutputDirectory 'native-imports.json')
Write-Host 'Verified PhotoRec and source-matched dependencies are ready. Corresponding source remains in the build cache for the separate release companion.'
