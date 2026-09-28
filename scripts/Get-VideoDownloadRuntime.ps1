param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath = Join-Path $PSScriptRoot 'video-download-runtime-lock.json'
$runtimeLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$destination = Join-Path $repoRoot 'runtime/video-download'
$downloads = Join-Path $repoRoot '.tools/downloads/video-download'
$licenses = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Force -Path $destination,$downloads,$licenses | Out-Null
$ytDlp = Join-Path $destination 'yt-dlp'
Get-VerifiedDownload -Uri $runtimeLock.ytDlp.Url -Path $ytDlp -Sha256 $runtimeLock.ytDlp.Sha256

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($component in @('python','node')) {
    $item = $runtimeLock.$component
    $archivePath = Join-Path $downloads ($component + '-' + $item.Version + '.zip')
    Get-VerifiedDownload -Uri $item.Url -Path $archivePath -Sha256 $item.Sha256
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName
            if ($component -eq 'node') {
                if ($entry.FullName -eq ('node-v' + $item.Version + '-win-x64/node.exe')) { $relative = 'node.exe' }
                elseif ($entry.FullName -eq ('node-v' + $item.Version + '-win-x64/LICENSE')) { $relative = 'licenses/Node-LICENSE.txt' }
                else { continue }
            }
            $path = Assert-ChildPath -Root $destination -Path (Join-Path $destination $relative)
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
        }
    } finally { $archive.Dispose() }
}
# Remove only the research-only, unshipped GPL PyInstaller executable if present.
foreach ($unusedName in @('yt-dlp.exe','deno.exe')) {
    $unusedExecutable = Assert-ChildPath -Root $destination -Path (Join-Path $destination $unusedName)
    if (Test-Path -LiteralPath $unusedExecutable) { Remove-Item -LiteralPath $unusedExecutable }
}
Copy-Item -LiteralPath (Join-Path $destination 'LICENSE.txt') -Destination (Join-Path $licenses 'Python-LICENSE.txt') -Force
# The release archive carries the source and exact license texts for the bundled JavaScript libraries.
$sourceArchive = Join-Path $downloads ('yt-dlp-' + $runtimeLock.ytDlp.Version + '.tar.gz')
Get-VerifiedDownload -Uri $runtimeLock.ytDlp.SourceUrl -Path $sourceArchive -Sha256 $runtimeLock.ytDlp.SourceSha256
$python = Join-Path $destination 'python.exe'
$licenseExtractor = @'
import pathlib, sys, tarfile
with tarfile.open(sys.argv[1], 'r:gz') as archive:
    for member in archive.getmembers():
        if member.isfile() and pathlib.PurePosixPath(member.name).name in ('LICENSE', 'THIRD_PARTY_LICENSES.txt'):
            with archive.extractfile(member) as stream:
                pathlib.Path(sys.argv[2], 'yt-dlp-' + pathlib.PurePosixPath(member.name).name + '.txt').write_bytes(stream.read())
'@
& $python -I -c $licenseExtractor $sourceArchive $licenses
if ($LASTEXITCODE -ne 0) { throw 'Unable to retain the yt-dlp license texts.' }
Get-VerifiedDownload -Uri $runtimeLock.ytDlp.NoticesUrl -Path (Join-Path $licenses 'yt-dlp-THIRD_PARTY_LICENSES.txt') -Sha256 $runtimeLock.ytDlp.NoticesSha256
& $python -I $ytDlp --ignore-config --no-plugin-dirs --version
if ($LASTEXITCODE -ne 0) { throw 'The bundled downloader runtime did not start.' }
& (Join-Path $destination 'node.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'The bundled JavaScript runtime did not start.' }
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $destination 'runtime-lock.json') -Force
$files = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object Name -ne 'build-source.json' | ForEach-Object {
    $relative = [IO.Path]::GetFullPath($_.FullName).Substring(([IO.Path]::GetFullPath($destination).TrimEnd('\') + '\').Length)
    [ordered]@{ Path=$relative.Replace('\','/'); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ Packaging='Official yt-dlp zipimport source distribution; private CPython embeddable runtime; Node.js LTS'; Components=$runtimeLock; Files=$files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'build-source.json') -Encoding utf8
Write-Host 'Pinned video-download runtime and original license notices are ready.'
