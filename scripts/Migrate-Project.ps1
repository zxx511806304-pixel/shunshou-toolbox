<#
.SYNOPSIS
  顺手工具箱项目跨电脑迁移协议 — 导出 / 导入 / 校验
.DESCRIPTION
  Export   — 扫描源代码与配置，生成 SHA256 清单 + 环境快照，打包迁移包
  Import   — 解包、逐文件校验、下载环境、还原依赖、构建并验证
  Verify   — 仅校验已有清单，不执行还原
.PARAMETER Mode
  Export | Import | Verify
.PARAMETER BundlePath
  迁移包路径（Export 时为输出；Import/Verify 时为输入）
.PARAMETER TargetPath
  Import 时的目标目录（默认当前目录）
.PARAMETER SkipRuntime
  Import 时跳过运行时下载（仅还原源码 + NuGet + 构建）
.EXAMPLE
  # 在源机器上导出
  pwsh -File scripts/Migrate-Project.ps1 -Mode Export -BundlePath D:\migration\shunshou-migrate.zip
.EXAMPLE
  # 在目标机器上导入（一键还原）
  pwsh -File scripts/Migrate-Project.ps1 -Mode Import -BundlePath D:\migration\shunshou-migrate.zip -TargetPath E:\projects\app
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateSet('Export','Import','Verify')]
  [string]$Mode,

  [Parameter(Mandatory)]
  [string]$BundlePath,

  [string]$TargetPath = (Get-Location).Path,

  [switch]$SkipRuntime
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ProjectRoot = switch ($Mode) {
  'Export' { (Get-Location).Path }
  'Import' { $TargetPath }
  'Verify' { $TargetPath }
}

# ── 工具函数 ──────────────────────────────────────────
function Get-FileManifest {
  param([string]$Root)
  $ignore = '\\(\.git|\.tools|\.trae|\.backup-xaml|bin|obj|dist|runtime|artifacts|TestResults|\.vs)(\\|$)'
  $excludeExt = @('.user','.suo','.log','.tmp','.binlog','.cache')
  Get-ChildItem $Root -Recurse -File -Force |
    Where-Object {
      $_.FullName -notmatch $ignore -and
      $_.Extension -notin $excludeExt -and
      $_.Name -ne 'desktop.ini'
    } |
    ForEach-Object {
      $rel = $_.FullName.Substring($Root.Length + 1) -replace '\\','/'
      $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
      [PSCustomObject]@{
        Path   = $rel
        Size   = $_.Length
        Sha256 = $hash
      }
    }
}

function Get-EnvSnapshot {
  param([string]$Root)
  $dotnet = Join-Path $Root '.tools\dotnet\dotnet.exe'
  $sdkVer = if (Test-Path $dotnet) { & $dotnet --version 2>$null } else { 'not found' }
  $gitExe = Get-Command git -ErrorAction SilentlyContinue
  $gitVer = if ($gitExe) { & $gitExe --version 2>$null } else { 'not found' }
  $remote = ''
  $gitDir = Join-Path $Root '.git'
  if (Test-Path $gitDir) {
    $gitCmd = 'C:\Program Files\Git\cmd\git.exe'
    if (Test-Path $gitCmd) {
      $remote = (& $gitCmd -C $Root remote get-url origin 2>$null)
    }
  }
  [PSCustomObject]@{
    DotNetSdkVersion   = $sdkVer
    GitVersion         = $gitVer
    OsPlatform         = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    Architecture       = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    NuGetSource        = 'https://api.nuget.org/v3/index.json'
    GitRemote          = $remote
    ProjectVersion     = ([xml](Get-Content (Join-Path $Root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    ExportDate         = (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz')
  }
}

function Test-Checksums {
  param([string]$Root, [array]$Manifest)
  $pass = 0; $fail = 0; $missing = 0
  foreach ($entry in $Manifest) {
    $full = Join-Path $Root ($entry.Path -replace '/','\')
    if (-not (Test-Path -LiteralPath $full)) { $missing++; Write-Warning "MISSING: $($entry.Path)"; continue }
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLower()
    if ($hash -eq $entry.Sha256) { $pass++ }
    else { $fail++; Write-Warning "HASH MISMATCH: $($entry.Path)" }
  }
  [PSCustomObject]@{ Pass = $pass; Fail = $fail; Missing = $missing; Total = $Manifest.Count }
}

function Invoke-DownloadWithRetry {
  param([string]$Url, [string]$OutFile, [int]$Retries = 3)
  for ($i = 1; $i -le $Retries; $i++) {
    try {
      Write-Host "  下载（第 $i 次）: $Url"
      Invoke-WebRequest -Uri $Url -OutFile $OutFile -MaximumRedirection 5
      return $true
    } catch {
      Write-Warning "  失败: $($_.Exception.Message)"
      if ($i -lt $Retries) { Start-Sleep -Seconds 5 }
    }
  }
  return $false
}

# ═══════════════════════════════════════════════════════
# Export 模式
# ═══════════════════════════════════════════════════════
if ($Mode -eq 'Export') {
  Write-Host "`n=== 导出迁移包 ===" -ForegroundColor Cyan
  $tmpDir = Join-Path $env:TEMP "shunshou-migrate-$(Get-Date -Format yyyyMMddHHmmss)"
  New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

  # 1. 文件清单 + SHA256
  Write-Host "[1/4] 扫描源代码并计算 SHA256 ..."
  $files = Get-FileManifest -Root $ProjectRoot
  $fileCount = @($files).Count
  $totalSize = ($files | Measure-Object Size -Sum).Sum
  Write-Host ("  共 $fileCount 个文件，{0:N2} MB" -f ($totalSize/1MB))

  $files | ConvertTo-Json -Depth 3 | Out-File (Join-Path $tmpDir 'manifest.json') -Encoding UTF8

  # 2. 环境快照
  Write-Host "[2/4] 采集环境信息 ..."
  $envInfo = Get-EnvSnapshot -Root $ProjectRoot
  $envInfo | ConvertTo-Json -Depth 3 | Out-File (Join-Path $tmpDir 'environment.json') -Encoding UTF8
  Write-Host "  .NET SDK: $($envInfo.DotNetSdkVersion)"
  Write-Host "  项目版本: $($envInfo.ProjectVersion)"

  # 3. 拷贝源码（保持相对路径）
  Write-Host "[3/4] 打包源代码 ..."
  $srcDir = Join-Path $tmpDir 'src'
  New-Item -ItemType Directory -Path $srcDir -Force | Out-Null
  foreach ($f in $files) {
    $srcPath = Join-Path $ProjectRoot ($f.Path -replace '/','\')
    $dstPath = Join-Path $srcDir ($f.Path -replace '/','\')
    $dstParent = Split-Path $dstPath -Parent
    if (-not (Test-Path $dstParent)) { New-Item -ItemType Directory -Path $dstParent -Force | Out-Null }
    Copy-Item -LiteralPath $srcPath -Destination $dstPath -Force
  }

  # 4. 打包
  Write-Host "[4/4] 生成迁移包 ..."
  $bundleDir = Split-Path $BundlePath -Parent
  if ($bundleDir -and -not (Test-Path $bundleDir)) { New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  if (Test-Path $BundlePath) { Remove-Item $BundlePath -Force }
  [System.IO.Compression.ZipFile]::CreateFromDirectory($tmpDir, $BundlePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

  $bundleSize = (Get-Item $BundlePath).Length
  Write-Host "`n导出完成 ✅" -ForegroundColor Green
  Write-Host "  迁移包: $BundlePath"
  Write-Host ("  大小: {0:N2} MB" -f ($bundleSize/1MB))
  Write-Host "  文件数: $fileCount"

  # 写一份迁移指南副本
  $guidePath = Join-Path $tmpDir 'migration-guide.md'
  Remove-Item -Recurse $tmpDir -Force
  return
}

# ═══════════════════════════════════════════════════════
# Verify 模式（仅校验，不还原）
# ═══════════════════════════════════════════════════════
if ($Mode -eq 'Verify') {
  Write-Host "`n=== 校验文件完整性 ===" -ForegroundColor Cyan
  $tmpDir = Join-Path $env:TEMP "shunshou-verify-$(Get-Date -Format yyyyMMddHHmmss)"
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($BundlePath, $tmpDir)
  $manifest = Get-Content (Join-Path $tmpDir 'manifest.json') -Raw | ConvertFrom-Json
  $envInfo = Get-Content (Join-Path $tmpDir 'environment.json') -Raw | ConvertFrom-Json
  Remove-Item -Recurse $tmpDir -Force

  Write-Host "源机器环境: .NET $($envInfo.DotNetSdkVersion), 项目 v$($envInfo.ProjectVersion), 导出于 $($envInfo.ExportDate)"
  $result = Test-Checksums -Root $ProjectRoot -Manifest $manifest
  Write-Host "`n校验结果:" -ForegroundColor $(if ($result.Fail -eq 0 -and $result.Missing -eq 0) { 'Green' } else { 'Yellow' })
  Write-Host "  通过: $($result.Pass) / $($result.Total)"
  if ($result.Fail -gt 0)     { Write-Host "  哈希不匹配: $($result.Fail)" -ForegroundColor Red }
  if ($result.Missing -gt 0)  { Write-Host "  缺失文件: $($result.Missing)" -ForegroundColor Red }
  if ($result.Fail -eq 0 -and $result.Missing -eq 0) { Write-Host "`nRESULT: PASS ✅" -ForegroundColor Green }
  else { Write-Host "`nRESULT: FAIL ❌" -ForegroundColor Red; exit 1 }
  return
}

# ═══════════════════════════════════════════════════════
# Import 模式（一键还原）
# ═══════════════════════════════════════════════════════
if ($Mode -eq 'Import') {
  Write-Host "`n=== 导入迁移包 ===" -ForegroundColor Cyan
  $tmpDir = Join-Path $env:TEMP "shunshou-import-$(Get-Date -Format yyyyMMddHHmmss)"
  New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

  # 1. 解包
  Write-Host "[1/6] 解包迁移包 ..."
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($BundlePath, $tmpDir)
  $manifest = Get-Content (Join-Path $tmpDir 'manifest.json') -Raw | ConvertFrom-Json
  $envInfo  = Get-Content (Join-Path $tmpDir 'environment.json') -Raw | ConvertFrom-Json

  # 2. 还原文件
  Write-Host "[2/6] 还原源代码到 $TargetPath ..."
  if (-not (Test-Path $TargetPath)) { New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null }
  $srcDir = Join-Path $tmpDir 'src'
  foreach ($entry in $manifest) {
    $dstPath = Join-Path $TargetPath ($entry.Path -replace '/','\')
    $srcPath = Join-Path $srcDir ($entry.Path -replace '/','\')
    $dstParent = Split-Path $dstPath -Parent
    if (-not (Test-Path $dstParent)) { New-Item -ItemType Directory -Path $dstParent -Force | Out-Null }
    Copy-Item -LiteralPath $srcPath -Destination $dstPath -Force
  }

  # 3. 校验
  Write-Host "[3/6] 逐文件校验 SHA256 ..."
  $result = Test-Checksums -Root $TargetPath -Manifest $manifest
  if ($result.Fail -gt 0 -or $result.Missing -gt 0) {
    Write-Host "校验失败: $($result.Fail) 不匹配, $($result.Missing) 缺失" -ForegroundColor Red
    exit 1
  }
  Write-Host "  全部 $($result.Pass) 个文件校验通过 ✅" -ForegroundColor Green

  # 4. 安装 Git（如未安装）
  Write-Host "[4/6] 检查 Git ..."
  $gitExe = Get-Command git -ErrorAction SilentlyContinue
  if (-not $gitExe) {
    Write-Host "  Git 未安装，正在通过 winget 安装 ..."
    winget install --id Git.Git -e --silent --accept-source-agreements --accept-package-agreements 2>&1 | Out-Null
    $env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')
    $gitExe = Get-Command git -ErrorAction SilentlyContinue
  }
  if ($gitExe) { Write-Host "  Git: $(& $gitExe.Source --version)" } else { Write-Warning "Git 安装失败，请手动安装" }

  # 5. 下载 .NET SDK
  Write-Host "[5/6] 检查 .NET SDK ..."
  $dotnetExe = Join-Path $TargetPath '.tools\dotnet\dotnet.exe'
  if (-not (Test-Path $dotnetExe)) {
    $sdkVer = $envInfo.DotNetSdkVersion
    Write-Host "  .NET SDK $sdkVer 未找到，开始下载 ..."
    $toolsDir = Join-Path $TargetPath '.tools'
    $downloadsDir = Join-Path $toolsDir 'downloads'
    if (-not (Test-Path $downloadsDir)) { New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null }

    $sdkUrl = "https://download.visualstudio.microsoft.com/download/pr/dotnet-sdk-$sdkVer-win-x64.zip"
    # 尝试多个已知 URL 模式
    $altUrls = @(
      "https://download.visualstudio.microsoft.com/download/pr/dotnet-sdk-$sdkVer-win-x64.zip",
      "https://dotnetcli.azureedge.net/dotnet/Sdk/$sdkVer/dotnet-sdk-$sdkVer-win-x64.zip",
      "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVer/dotnet-sdk-$sdkVer-win-x64.zip"
    )
    $sdkZip = Join-Path $downloadsDir "dotnet-sdk-$sdkVer-win-x64.zip"
    $downloaded = $false
    foreach ($url in $altUrls) {
      if (Invoke-DownloadWithRetry -Url $url -OutFile $sdkZip -Retries 2) { $downloaded = $true; break }
    }
    if ($downloaded) {
      $sdkExtractDir = Join-Path $toolsDir 'dotnet'
      New-Item -ItemType Directory -Path $sdkExtractDir -Force | Out-Null
      Add-Type -AssemblyName System.IO.Compression.FileSystem
      [System.IO.Compression.ZipFile]::ExtractToDirectory($sdkZip, $sdkExtractDir)
      Remove-Item $sdkZip -Force
      Write-Host "  .NET SDK $sdkVer 安装完成" -ForegroundColor Green
    } else {
      Write-Warning "  .NET SDK 自动下载失败。请手动下载 $sdkVer 版本："
      Write-Warning "  https://dotnet.microsoft.com/download/dotnet/10.0"
      Write-Warning "  解压到 $TargetPath\.tools\dotnet\"
    }
  } else {
    $ver = & $dotnetExe --version 2>$null
    Write-Host "  .NET SDK: $ver (已存在)"
  }

  # 6. 还原 NuGet + 构建
  Write-Host "[6/6] 还原 NuGet 包并构建 ..."
  if (Test-Path $dotnetExe) {
    Write-Host "  dotnet restore ..."
    & $dotnetExe restore (Join-Path $TargetPath 'Shunshou.sln') --nologo 2>&1 | Select-Object -Last 3
    Write-Host "  dotnet build (Debug) ..."
    & $dotnetExe build (Join-Path $TargetPath 'src\Shunshou.App\Shunshou.App.csproj') -c Debug -p:Platform=x64 -nologo 2>&1 | Select-Object -Last 3
  } else {
    Write-Warning "  跳过构建（.NET SDK 不可用）"
  }

  # 可选：下载运行时组件
  if (-not $SkipRuntime) {
    Write-Host "`n  下载离线运行时组件 ..."
    $scripts = @(
      'Download-Runtime.ps1',
      'Download-OcrModels.ps1',
      'Download-Everything.ps1',
      'Download-Recovery.ps1',
      'Download-7Zip.ps1',
      'Get-VideoDownloadRuntime.ps1'
    )
    foreach ($s in $scripts) {
      $sp = Join-Path $TargetPath "scripts\$s"
      if (Test-Path $sp) {
        Write-Host "  运行 $s ..."
        & pwsh -NoProfile -File $sp 2>&1 | Select-Object -Last 1
      }
    }
  }

  # 克隆 Git 历史（如远端可用）
  if ($envInfo.GitRemote) {
    Write-Host "`n  Git 远端: $($envInfo.GitRemote)"
    $gitDir = Join-Path $TargetPath '.git'
    if (-not (Test-Path $gitDir) -and $gitExe) {
      Write-Host "  初始化 Git 并拉取历史 ..."
      & $gitExe.Source -C $TargetPath init -b main 2>$null
      & $gitExe.Source -C $TargetPath remote add origin $envInfo.GitRemote 2>$null
      & $gitExe.Source -C $TargetPath fetch origin --quiet 2>$null
      Write-Host "  Git 历史已拉取（如需完整历史请 git merge origin/main）"
    }
  }

  Remove-Item -Recurse $tmpDir -Force -ErrorAction SilentlyContinue
  Write-Host "`n导入完成 ✅" -ForegroundColor Green
  Write-Host "  目标目录: $TargetPath"
  Write-Host "  文件校验: $($result.Pass) / $($result.Total) 通过"
  Write-Host "`n下一步:"
  Write-Host "  1. 打开 IDE 指向 $TargetPath"
  Write-Host "  2. dotnet build src\Shunshou.App\Shunshou.App.csproj -c Debug -p:Platform=x64"
  Write-Host "  3. pwsh -File scripts\Build-Portable.ps1 -Version $($envInfo.ProjectVersion)"
  return
}
