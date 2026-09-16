param(
    [string]$Version = '1.0.0',
    [string]$OutputRoot = '',
    [string]$SourceDirectory = '',
    [string]$RuntimeDirectory = '',
    [string]$DocumentationPath = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric version.' }
if (!$OutputRoot) { $OutputRoot = Join-Path $repoRoot 'dist' }
if (!$SourceDirectory) { $SourceDirectory = Join-Path $repoRoot '.tools/sources/recovery' }
if (!$RuntimeDirectory) { $RuntimeDirectory = Join-Path $repoRoot 'runtime/recovery' }
if (!$DocumentationPath) { $DocumentationPath = Join-Path $repoRoot 'docs/RecoverySources.md' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$lockPath = Join-Path $PSScriptRoot 'recovery-runtime-lock.json'
$runtimeLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
foreach ($package in $runtimeLock.packages) {
    foreach ($file in $package.files) {
        $dll = Join-Path (Join-Path $RuntimeDirectory 'bin') $file.name
        if (!(Test-Path -LiteralPath $dll) -or (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $file.sha256) { throw "Recovery runtime differs from the source lock. Run Download-RecoveryDependencies.ps1: $dll" }
    }
}
foreach ($file in @($runtimeLock.redistributedExecutable,$runtimeLock.retainedDll)) {
    $binary = Join-Path (Join-Path $RuntimeDirectory 'bin') $file.name
    if (!(Test-Path -LiteralPath $binary) -or (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $file.sha256) { throw "Recovery binary does not match its source lock: $binary" }
}
$archiveName = "RecoverySources-$Version.zip"
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$archivePath = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot $archiveName)
$stage = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot ('.recovery-sources-' + [Guid]::NewGuid().ToString('N')))
$temporaryZip = Assert-ChildPath -Root $OutputRoot -Path ($stage + '.zip')
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $archives = Join-Path $stage 'archives'
    $build = Join-Path $stage 'build'
    New-Item -ItemType Directory -Path $archives,$build | Out-Null
    $sourceItems = (@($runtimeLock.packages | ForEach-Object { $_.source }) + @($runtimeLock.additionalSources)) | Sort-Object file -Unique
    foreach ($item in $sourceItems) {
        $source = Assert-ChildPath -Root $SourceDirectory -Path (Join-Path $SourceDirectory $item.file)
        if (!(Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $item.sha256) { throw "Missing or changed corresponding source: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $archives $item.file)
    }
    foreach ($name in @('recovery-runtime-lock.json','Runtime.Common.ps1','Download-Recovery.ps1','Download-RecoveryDependencies.ps1','Extract-RecoveryDependencies.py','Build-RecoverySources.ps1','Inspect-NativeDependencies.ps1','RecoverySources-README.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $build
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RecoverySources-README.md') -Destination (Join-Path $stage 'SOURCE-README.md')
    foreach ($entry in @(@{From=(Join-Path $SourceDirectory 'recipes'); To='recipes'}, @{From=(Join-Path $RuntimeDirectory 'licenses'); To='licenses'})) {
        if (!(Test-Path -LiteralPath $entry.From) -or @(Get-ChildItem -LiteralPath $entry.From -File).Count -eq 0) { throw "Missing source recipes/licenses. Run Download-RecoveryDependencies.ps1: $($entry.From)" }
        Copy-Item -LiteralPath $entry.From -Destination (Join-Path $stage $entry.To) -Recurse
    }
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/'); Bytes=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ Product='Shunshou Toolbox Recovery Corresponding Source'; Version=$Version; Files=$files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'SOURCE-MANIFEST.json') -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($temporaryZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($stage,$file.FullName).Replace('\','/')
            $entry = $zip.CreateEntry($relative,[IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead($file.FullName)
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $sha = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $archivePath) {
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $sha) { throw "A different source companion already exists; use a new OutputRoot: $archivePath" }
        Remove-Item -LiteralPath $temporaryZip
    } else { [IO.File]::Move($temporaryZip,$archivePath) }
    "$sha  $archiveName" | Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding ascii
    $doc = @"
# 恢复组件对应源码

本版本的恢复程序、运行库及其静态依赖对应源码随同提供，运行软件不需要保留源码包。

- 源码包文件名：$archiveName
- SHA-256：$sha
- 文件大小：$((Get-Item -LiteralPath $archivePath).Length) 字节

请从与本软件相同的下载位置取得这个源码包。分发本软件时必须同时提供对应源码包与等同的下载访问条件；上游网站链接不能替代这个源码包。离线转交软件时也请一并转交源码包。

源码包包括 PhotoRec 7.2 所属的 TestDisk 完整源档、六枚官方 Cygwin 运行库及匹配的终端定义，以及保留的 libewf 和静态链接的 ext2fs/ntfs 源码、补丁、构建规则、原文许可与逐文件校验清单。具体版本与源文件对应关系见源码包内 build/recovery-runtime-lock.json。
"@
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($DocumentationPath))) | Out-Null
    $doc | Set-Content -LiteralPath $DocumentationPath -Encoding utf8
    Write-Host "Recovery source companion: $archivePath"
    Write-Host "SHA256: $sha"
    [pscustomobject]@{ Path=$archivePath; Sha256=$sha; Bytes=(Get-Item -LiteralPath $archivePath).Length; DocumentationPath=$DocumentationPath }
} finally {
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip }
    if (Test-Path -LiteralPath $stage) {
        $safeStage = Assert-ChildPath -Root $OutputRoot -Path $stage
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
}
