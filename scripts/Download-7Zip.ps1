param([string]$Dotnet = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath = Join-Path $PSScriptRoot 'sevenzip-runtime-lock.json'
$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
$downloadRoot = Join-Path $repoRoot '.tools/downloads'
$destination = Join-Path $repoRoot 'runtime/sevenzip'
$package = Join-Path $downloadRoot $lock.Asset.File

Get-VerifiedDownload -Uri $lock.Asset.Url -Path $package -Sha256 $lock.Asset.Sha256
if ((Get-Item -LiteralPath $package).Length -ne $lock.Asset.Bytes) { throw 'The pinned 7-Zip package length does not match.' }

# Administrative install: the official MSI unpacks its payload without installing anything
# system-wide, so the build machine never gains a 7-Zip installation or shell integration.
$staging = Join-Path $downloadRoot ('7zip-extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging, $destination | Out-Null
try {
    $installer = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/a', $package, '/qn', "TARGETDIR=$staging") -Wait -PassThru
    if ($installer.ExitCode -ne 0) { throw "7-Zip administrative install failed with exit code $($installer.ExitCode)." }
    $engine = Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '7z.exe' | Select-Object -First 1
    if (-not $engine) { throw 'The 7-Zip payload did not contain 7z.exe.' }
    $source = $engine.DirectoryName
    foreach ($name in @('7z.exe', '7z.dll', 'License.txt', 'readme.txt')) {
        $from = Join-Path $source $name
        if (-not (Test-Path -LiteralPath $from)) { throw "The 7-Zip payload is missing $name." }
        Copy-Item -LiteralPath $from -Destination (Join-Path $destination $name) -Force
    }
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

# Prove the packaged engine starts, reports the pinned version and really contains the RAR reader.
$output = & (Join-Path $destination '7z.exe') i 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'The extracted 7z.exe did not start.' }
if ($output -notmatch [regex]::Escape($lock.Version)) { throw "7z.exe did not report version $($lock.Version)." }
if ($output -notmatch '(?m)^\s*\d+\s+\S+\s+Rar\s') { throw '7z.exe does not list the RAR reader; .rar extraction could not be promised.' }

$record = [ordered]@{
    Name = '7-Zip'
    Version = $lock.Version
    ReleasedUtc = $lock.ReleasedUtc
    ProjectUrl = $lock.ProjectUrl
    License = $lock.License
    Source = [ordered]@{ File = $lock.Asset.File; Url = $lock.Asset.Url; Sha256 = $lock.Asset.Sha256; Bytes = $lock.Asset.Bytes; HashSource = $lock.Asset.HashSource }
    Files = @(Get-ChildItem -LiteralPath $destination -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ Path = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    Capabilities = @('7z 解压', 'rar 解压（只读，不创建 rar）', 'zip / tar / gzip 等常见格式解压')
}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'build-source.json') -Encoding utf8
Remove-Item -LiteralPath (Join-Path $destination 'component-source.json') -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $destination 'runtime-lock.json') -Force
Write-Host "Pinned 7-Zip $($lock.Version) engine, license and capability record are ready."
