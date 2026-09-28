param(
    [string]$OutputDirectory = '',
    [string]$SourceDirectory = '',
    [string]$PythonPath = 'python'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'runtime/recovery' }
if (!$SourceDirectory) { $SourceDirectory = Join-Path $repoRoot '.tools/sources/recovery' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$downloadDirectory = Join-Path $repoRoot '.tools/downloads/recovery'
$lockPath = Join-Path $PSScriptRoot 'recovery-runtime-lock.json'
$runtimeLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json

# Python 3.14 provides tar.zst support in its standard library. This is a build
# dependency only: Python and the source archives are not needed to run the app.
& $PythonPath -c 'import sys,compression.zstd; assert sys.version_info >= (3,14), "Python 3.14 or later is required"'
if ($LASTEXITCODE -ne 0) { throw 'Recovery dependency preparation requires Python 3.14 or later.' }
foreach ($package in $runtimeLock.packages) {
    $archive = Assert-ChildPath -Root $downloadDirectory -Path (Join-Path $downloadDirectory $package.install.file)
    Get-VerifiedDownload -Uri $package.install.url -Path $archive -Sha256 $package.install.sha256
    $source = Assert-ChildPath -Root $SourceDirectory -Path (Join-Path $SourceDirectory $package.source.file)
    Get-VerifiedDownload -Uri $package.source.url -Path $source -Sha256 $package.source.sha256
}
foreach ($artifact in $runtimeLock.additionalSources) {
    $source = Assert-ChildPath -Root $SourceDirectory -Path (Join-Path $SourceDirectory $artifact.file)
    Get-VerifiedDownload -Uri $artifact.url -Path $source -Sha256 $artifact.sha256
}
& $PythonPath (Join-Path $PSScriptRoot 'Extract-RecoveryDependencies.py') --lock $lockPath --downloads $downloadDirectory --sources $SourceDirectory --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Recovery dependency extraction or verification failed.' }
Write-Host 'Pinned recovery dependencies and original licenses are ready.'
Write-Host "Distribute the complete corresponding-source companion from $SourceDirectory beside the binary release."
