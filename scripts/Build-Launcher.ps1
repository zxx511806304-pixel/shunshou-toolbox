param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Launcher version must have three numeric parts.' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw "Launcher output already exists: $OutputPath" }
# Pinned official build-only compiler. It is never included in the portable package.
# Upstream checksums: https://ziglang.org/download/index.json (0.15.2, x86_64-windows).
$archive = Join-Path $repoRoot '.tools/downloads/zig-x86_64-windows-0.15.2.zip'
$compilerRoot = Join-Path $repoRoot '.tools/zig-x86_64-windows-0.15.2'
$compiler = Join-Path $compilerRoot 'zig.exe'
Get-VerifiedDownload -Uri 'https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip' -Path $archive -Sha256 '3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c'
if (-not (Test-Path -LiteralPath $compiler)) {
    if (Test-Path -LiteralPath $compilerRoot) { throw "Incomplete compiler directory exists; inspect before retrying: $compilerRoot" }
    Add-Type -AssemblyName System.IO.Compression
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, (Join-Path $repoRoot '.tools'))
}
$buildRoot = Join-Path $repoRoot '.tools/launcher-build'
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$stage = Assert-ChildPath -Root $buildRoot -Path (Join-Path $buildRoot ([Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $icon = (Join-Path $repoRoot 'src/Shunshou.App/Assets/AppIcon.ico').Replace('\','/')
    $manifest = (Join-Path $repoRoot 'src/Shunshou.Launcher/launcher.manifest').Replace('\','/')
    $versionNumbers = $Version.Replace('.', ',') + ',0'
    $resource = @"
#include <windows.h>
1 ICON "$icon"
1 24 "$manifest"
1 VERSIONINFO
FILEVERSION $versionNumbers
PRODUCTVERSION $versionNumbers
FILEFLAGSMASK 0x3fL
FILEFLAGS 0x0L
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0x0L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "FileDescription", "Shunshou Toolbox"
      VALUE "ProductName", "Shunshou Toolbox"
      VALUE "FileVersion", "$Version.0"
      VALUE "ProductVersion", "$Version"
      VALUE "OriginalFilename", "ShunshouToolbox.exe"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@
    $resourcePath = Join-Path $stage 'launcher.rc'
    [IO.File]::WriteAllText($resourcePath, $resource, [Text.UTF8Encoding]::new($false))
    $compiled = Join-Path $stage 'ShunshouToolbox.exe'
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $buildRoot 'cache'
    $resourceObject = Join-Path $stage 'launcher.res'
    & $compiler rc "/fo$resourceObject" $resourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Native launcher resource compilation failed.' }
    & $compiler cc '-target' 'x86_64-windows-gnu' '-Os' '-s' '-municode' '-Wl,--subsystem,windows' '-Werror' '-Wall' '-Wextra' `
        (Join-Path $repoRoot 'src/Shunshou.Launcher/launcher.c') $resourceObject '-lkernel32' '-luser32' '-o' $compiled
    if ($LASTEXITCODE -ne 0) { throw 'Native launcher compilation failed.' }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
    [IO.File]::Move($compiled, $OutputPath, $false)
    Write-Host "Native launcher: $OutputPath ($((Get-Item -LiteralPath $OutputPath).Length) bytes)"
}
finally {
    $safe = Assert-ChildPath -Root $buildRoot -Path $stage
    if (Test-Path -LiteralPath $safe) { Remove-Item -LiteralPath $safe -Recurse -Force }
}
