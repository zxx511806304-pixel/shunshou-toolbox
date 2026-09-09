Set-StrictMode -Version Latest

function Assert-ChildPath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Path)
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw "Generated path is outside its intended directory: $full" }
    return $full
}

function Get-VerifiedDownload {
    param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sha256)
    if ($Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'A pinned SHA256 is required.' }
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) { throw "Existing download has a different SHA256; refusing to use it: $Path" }
        Write-Host "Verified cached download: $([IO.Path]::GetFileName($Path))"
        return
    }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))) | Out-Null
    $temporary = $Path + '.download-' + [Guid]::NewGuid().ToString('N')
    try {
        try { Invoke-WebRequest -Uri $Uri -OutFile $temporary -Headers @{'User-Agent'='ShunshouToolbox-Build/0.1'} -ErrorAction Stop }
        catch {
            # Some restricted Windows accounts cannot initialize Schannel. urllib uses its normal verified TLS stack.
            $python = Get-Command python -ErrorAction SilentlyContinue
            if (-not $python) { throw }
            Write-Host 'PowerShell HTTPS failed; retrying with Python standard-library HTTPS and the same SHA256 requirement.'
            & $python.Source -c 'import urllib.request,sys; urllib.request.urlretrieve(sys.argv[1],sys.argv[2])' $Uri $temporary
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $Uri" }
        }
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Sha256) { throw "SHA256 mismatch; refusing changed upstream content: $Uri" }
        [IO.File]::Move([IO.Path]::GetFullPath($temporary), [IO.Path]::GetFullPath($Path), $false)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
