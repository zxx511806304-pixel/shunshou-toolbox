param([string]$Python = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $repoRoot '.tools'
$runtimeRoot = Join-Path $repoRoot 'runtime/ai-inpaint'
$depsPath = Join-Path $PSScriptRoot 'sttn-export-deps-lock.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$sourceLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sttn-runtime-lock.json') -Raw | ConvertFrom-Json
$exporter = Join-Path $PSScriptRoot 'Export-Sttn.py'
$exporterHash = (Get-FileHash -LiteralPath $exporter -Algorithm SHA256).Hash.ToLowerInvariant()
$depsHash = (Get-FileHash -LiteralPath $depsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$model = Join-Path $runtimeRoot 'sttn.onnx'
$report = Join-Path $runtimeRoot 'sttn.export.json'
$cacheStamp = Join-Path $toolsRoot 'sttn-export-cache.json'

function Test-ModelReport([string]$ModelPath, [string]$ReportPath) {
    try {
        if (-not (Test-Path -LiteralPath $ModelPath) -or -not (Test-Path -LiteralPath $ReportPath)) { return $false }
        $result = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
        if ($result.model -ne 'STTN' -or $result.source_commit -ne $sourceLock.Source.Commit -or
            $result.checkpoint_sha256 -ne $sourceLock.Checkpoint.Sha256 -or $result.opset -ne 17) { return $false }
        if ($result.onnx_sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            (Get-Item -LiteralPath $ModelPath).Length -ne $result.onnx_bytes -or
            (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash -ne $result.onnx_sha256) { return $false }
        if (($result.inputs.frames -join ',') -ne '1,5,3,240,432' -or
            ($result.inputs.masks -join ',') -ne '1,5,1,240,432' -or
            ($result.output -join ',') -ne '1,3,240,432') { return $false }
        foreach ($name in @('torch','onnx','onnxruntime')) {
            if ($result.$name -ne ($deps.Packages | Where-Object Name -EQ $name).Version) { return $false }
        }
        $errorValue = [double]$result.parity_max_abs_error
        $influence = [double]$result.adjacent_frame_influence_mean_abs
        return ([double]::IsFinite($errorValue) -and $errorValue -ge 0 -and $errorValue -le 0.003 -and
            [double]::IsFinite($influence) -and $influence -ge 0.00001)
    } catch { return $false }
}

& (Join-Path $PSScriptRoot 'Get-SttnSource.ps1')
$reuse = Test-ModelReport -ModelPath $model -ReportPath $report
if ($reuse) {
    $modelHash = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $cacheStamp) {
        try {
            $stamp = Get-Content -LiteralPath $cacheStamp -Raw | ConvertFrom-Json
            $reuse = $stamp.ExporterSha256 -eq $exporterHash -and $stamp.DependenciesSha256 -eq $depsHash -and $stamp.OnnxSha256 -eq $modelHash
        } catch { $reuse = $false }
    } else {
        # Adopt the already verified initial export without repeating costly inference.
        $reuse = $modelHash -eq $deps.ReferenceOnnxSha256 -and $exporterHash -eq $deps.ReferenceExporterSha256
    }
}
if (-not $reuse) {
    if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) { throw 'STTN export dependencies are locked for Windows x64.' }
    $environmentRoot = Assert-ChildPath -Root $toolsRoot -Path (Join-Path $toolsRoot 'sttn-export-env')
    $environmentPython = Join-Path $environmentRoot 'Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $environmentPython)) {
        if (-not $Python) {
            $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
            if ($pythonCommand) { $Python = $pythonCommand.Source }
        }
        if (-not $Python) { throw 'Python 3.12 x64 is required for a fresh export. Supply -Python with its python.exe path.' }
        $version = & $Python -I -c 'import platform,struct; print(platform.python_version()+"|"+str(struct.calcsize("P")*8))'
        if ($LASTEXITCODE -ne 0 -or $version -notmatch '^3\.12\.\d+\|64$') { throw 'The supplied Python must be Python 3.12 x64.' }
        & $Python -I -m venv $environmentRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated STTN export environment.' }
    }
    $version = & $environmentPython -I -c 'import platform,struct; print(platform.python_version()+"|"+str(struct.calcsize("P")*8))'
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^3\.12\.\d+\|64$') { throw 'Existing STTN environment must use Python 3.12 x64; use a fresh isolated environment.' }
    $requirements = Join-Path $toolsRoot 'sttn-export-requirements.lock.txt'
    $lines = foreach ($package in $deps.Packages) {
        if ($package.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $package.Url -notmatch '^https://(files\.pythonhosted\.org|download(-r2)?\.pytorch\.org)/') { throw 'Unexpected dependency URL or checksum in STTN dependency lock.' }
        "$($package.Name) @ $($package.Url) --hash=sha256:$($package.Sha256)"
    }
    $lines | Set-Content -LiteralPath $requirements -Encoding utf8
    & $environmentPython -I -m pip --isolated install --disable-pip-version-check --only-binary=:all: --no-deps --no-index --require-hashes --requirement $requirements
    if ($LASTEXITCODE -ne 0) { throw 'Installing the exact STTN export wheel dependencies failed.' }
    & $environmentPython -I -c 'import importlib.metadata,json,sys; lock=json.load(open(sys.argv[1],encoding="utf-8")); errors=[p["Name"] for p in lock["Packages"] if importlib.metadata.version(p["Name"])!=p["Version"]]; assert not errors, errors' $depsPath
    if ($LASTEXITCODE -ne 0) { throw 'Installed STTN export dependency versions do not match the lock.' }
    & $environmentPython -I -m pip --isolated check
    if ($LASTEXITCODE -ne 0) { throw 'STTN export dependency compatibility check failed.' }
    $staging = Assert-ChildPath -Root $toolsRoot -Path (Join-Path $toolsRoot 'sttn-export-build')
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $stagedModel = Join-Path $staging 'sttn.onnx'
    $stagedReport = Join-Path $staging 'sttn.export.json'
    & $environmentPython -I $exporter --source (Join-Path $toolsRoot 'sources/sttn') --checkpoint (Join-Path (Join-Path $toolsRoot 'downloads') $sourceLock.Checkpoint.File) --output $stagedModel
    if ($LASTEXITCODE -ne 0 -or -not (Test-ModelReport -ModelPath $stagedModel -ReportPath $stagedReport)) { throw 'STTN ONNX export or verification failed; the existing runtime model was not replaced.' }
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    Copy-Item -LiteralPath $stagedModel -Destination $model -Force
    Copy-Item -LiteralPath $stagedReport -Destination $report -Force
} else {
    Write-Host 'Reusing verified STTN ONNX export; Python installation and model inference are skipped.'
}
& (Join-Path $PSScriptRoot 'Get-AiInpaintRuntime.ps1') -RequireSttn
[ordered]@{
    ExporterSha256=$exporterHash; DependenciesSha256=$depsHash
    OnnxSha256=(Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath $cacheStamp -Encoding utf8
Write-Host "STTN model ready: $model"
