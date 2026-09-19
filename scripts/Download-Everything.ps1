param([string]$Python = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $repo '.tools/everything-download'
$destination = Join-Path $repo 'runtime/everything'
New-Item -ItemType Directory -Path $cache,$destination -Force | Out-Null
$downloads = @(
    @{ File='Everything-1.4.1.1032.x64.zip'; Url='https://www.voidtools.com/Everything-1.4.1.1032.x64.zip'; Sha256='698DF475EC44E638F66F1B6A32D28FEA613CEC78D3B6310E6ABE53431EEB940C' },
    @{ File='ES-1.1.0.38.x64.zip'; Url='https://www.voidtools.com/ES-1.1.0.38.x64.zip'; Sha256='5E0C70CBF4F694080C34AA7C6C745E606C16FE76A4B5423B93EBF9DC34274C99' },
    @{ File='ES-1.1.0.38.src.zip'; Url='https://www.voidtools.com/ES-1.1.0.38.src.zip'; Sha256='2E36D1177633218821CBF1CE6430BA88E6FC5192CC81A1C254D16E72DF63A8EE' },
    @{ File='Everything-License.txt'; Url='https://www.voidtools.com/License.txt'; Sha256='C13D19ADCBFD5D07E9512DE9DF99956A3423399ED1FADC5FD33186697AD8DF2F' }
)
foreach ($asset in $downloads) {
    $path = Join-Path $cache $asset.File
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $asset.Sha256) {
        if ($Python) {
            # Python uses the normal Windows certificate store; certificate verification stays enabled.
            & $Python -c 'import sys,urllib.request,pathlib; pathlib.Path(sys.argv[2]).write_bytes(urllib.request.urlopen(sys.argv[1]).read())' $asset.Url $path
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $($asset.File)" }
        } else { Invoke-WebRequest -Uri $asset.Url -OutFile $path }
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $asset.Sha256) { throw "SHA256 mismatch: $($asset.File)" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Copy-ZipEntry([string]$Archive,[string]$Entry,[string]$Output) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $cache $Archive))
    try {
        $item = $zip.Entries | Where-Object FullName -EQ $Entry | Select-Object -First 1
        if (!$item) { throw "Missing pinned archive entry: $Entry" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item,(Join-Path $destination $Output),$true)
    } finally { $zip.Dispose() }
}
Copy-ZipEntry 'Everything-1.4.1.1032.x64.zip' 'everything.exe' 'Everything.exe'
Copy-ZipEntry 'Everything-1.4.1.1032.x64.zip' 'Everything.lng' 'Everything.lng'
Copy-ZipEntry 'ES-1.1.0.38.x64.zip' 'es.exe' 'es.exe'
Copy-ZipEntry 'ES-1.1.0.38.src.zip' 'ES-1.1.0.38/LICENSE' 'ES-LICENSE.txt'
Copy-Item -LiteralPath (Join-Path $cache 'Everything-License.txt') -Destination (Join-Path $destination 'Everything-LICENSE.txt') -Force
# These local defaults prevent inherited application data paths or accidental network servers.
@'
[Everything]
app_data=0
run_as_admin=0
http_server_enabled=0
etp_server_enabled=0
check_for_updates_on_startup=0
'@ | Set-Content -LiteralPath (Join-Path $destination 'Everything.ini') -Encoding utf8
@{ Components=@(
    @{ Id='everything'; Name='Everything'; Group='文件搜索'; Purpose='本地文件名索引与实时变更监测'; Version='1.4.1.1032'; License='MIT and PCRE notices'; ProjectUrl='https://www.voidtools.com/'; AdditionalUrl='https://www.voidtools.com/License.txt' },
    @{ Id='everything-es'; Name='Everything ES'; Group='文件搜索'; Purpose='通过本机 IPC 查询 Everything 索引'; Version='1.1.0.38'; License='MIT'; ProjectUrl='https://github.com/voidtools/ES'; AdditionalUrl='https://www.voidtools.com/support/everything/command_line_interface/' }
) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'component-source.json') -Encoding utf8
@{ Downloads=$downloads; Files=@(Get-ChildItem -LiteralPath $destination -File | Where-Object Name -NE 'runtime-lock.json' | ForEach-Object { @{ File=$_.Name; Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash } }) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'runtime-lock.json') -Encoding utf8
Write-Host "Everything portable + ES ready: $destination"
