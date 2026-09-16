param([switch]$RequireSttn)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockPath = Join-Path $PSScriptRoot 'ai-inpaint-runtime-lock.json'
$runtimeLock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
$destination = Join-Path $repoRoot 'runtime/ai-inpaint'
$cache = Join-Path (Join-Path $repoRoot '.tools/downloads') $runtimeLock.Model.CacheFile
$licenses = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Force -Path $destination,$licenses | Out-Null
Get-VerifiedDownload -Uri $runtimeLock.Model.Url -Path $cache -Sha256 $runtimeLock.Model.Sha256
if ((Get-Item -LiteralPath $cache).Length -ne $runtimeLock.Model.Bytes) { throw 'The pinned MI-GAN model length does not match.' }
$modelPath = Assert-ChildPath -Root $destination -Path (Join-Path $destination $runtimeLock.Model.File)
Copy-Item -LiteralPath $cache -Destination $modelPath -Force
foreach ($license in $runtimeLock.Licenses) {
    $licensePath = Assert-ChildPath -Root $licenses -Path (Join-Path $licenses $license.File)
    Get-VerifiedDownload -Uri $license.Url -Path $licensePath -Sha256 $license.Sha256
}
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $destination 'runtime-lock.json') -Force
$components = [Collections.Generic.List[object]]::new()
$components.Add([ordered]@{
    Id='migan'; Group='AI 视频修补'; Name='MI-GAN'
    Version='512 / pipeline v2 (406830d0)'; License='MIT'
    ProjectUrl=$runtimeLock.ProjectUrl; AdditionalUrl='https://huggingface.co/andraniksargsyan/migan'
    Purpose='轻量 AI：本地逐帧局部补全'
})
$components.Add([ordered]@{
    Id='onnxruntime-directml'; Group='AI 视频修补'; Name='ONNX Runtime DirectML'
    Version='1.24.4'; License='MIT'; ProjectUrl='https://github.com/microsoft/onnxruntime'
    AdditionalUrl='https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html'
    Purpose='独立 AI 推理进程；支持本地 CPU 与 DirectML GPU'
})
$components.Add([ordered]@{
    Id='directml'; Group='AI 视频修补'; Name='Microsoft DirectML'
    Version='1.15.4'; License='Microsoft Software License Terms (DirectML binary)'
    ProjectUrl='https://github.com/microsoft/DirectML'
    AdditionalUrl='https://learn.microsoft.com/windows/ai/directml/dml'
    Purpose='Windows 本地 GPU 推理加速；二进制按微软 DirectML 许可分发'
})
$sttnPath = Join-Path $destination 'sttn.onnx'
if (Test-Path -LiteralPath $sttnPath) {
    $sttnLockPath = Join-Path $PSScriptRoot 'sttn-runtime-lock.json'
    $sttnLock = Get-Content -Raw -LiteralPath $sttnLockPath | ConvertFrom-Json
    $exportPath = Join-Path $destination 'sttn.export.json'
    if (-not (Test-Path -LiteralPath $exportPath)) { throw 'STTN ONNX export verification metadata is missing.' }
    $export = Get-Content -Raw -LiteralPath $exportPath | ConvertFrom-Json
    if ($export.source_commit -ne $sttnLock.Source.Commit -or $export.checkpoint_sha256 -ne $sttnLock.Checkpoint.Sha256) {
        throw 'STTN export provenance does not match the pinned source and checkpoint.'
    }
    if ($export.onnx_sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        (Get-FileHash -LiteralPath $sttnPath -Algorithm SHA256).Hash -ne $export.onnx_sha256 -or
        (Get-Item -LiteralPath $sttnPath).Length -ne $export.onnx_bytes) {
        throw 'STTN ONNX export checksum or length mismatch.'
    }
    if ($export.parity_max_abs_error -gt 0.003 -or $export.adjacent_frame_influence_mean_abs -lt 0.00001) {
        throw 'STTN export did not pass the required numerical parity and temporal-context checks.'
    }
    $sttnLicensePath = Join-Path $licenses $sttnLock.LicenseFile.File
    Get-VerifiedDownload -Uri $sttnLock.LicenseFile.Url -Path $sttnLicensePath -Sha256 $sttnLock.LicenseFile.Sha256
    Copy-Item -LiteralPath $sttnLockPath -Destination (Join-Path $destination 'sttn-runtime-lock.json') -Force
    $components.Add([ordered]@{
        Id='sttn'; Group='AI 视频修补'; Name='STTN'
        Version='ECCV 2020 (f39f62c5)'; License='MIT'; ProjectUrl=$sttnLock.ProjectUrl
        AdditionalUrl=$sttnLock.Checkpoint.PublishedLink
        Purpose='深度 AI：利用相邻视频帧进行时空注意力补全'
    })
} elseif ($RequireSttn) {
    throw 'The STTN ONNX model is missing. Run Get-SttnSource.ps1 and the separate pinned ONNX export step before packaging deep AI.'
}
[ordered]@{ Components=$components.ToArray() } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'component-source.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/AiInpaintComponents.md') -Destination (Join-Path $destination 'MODEL-PROVENANCE.md') -Force
if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $runtimeLock.Model.Sha256) { throw 'Published MI-GAN model checksum mismatch.' }
Write-Host "MI-GAN model ready: $modelPath ($($runtimeLock.Model.Bytes) bytes); code and weights MIT notices retained."
