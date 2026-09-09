param([string]$NuGetRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ocrRoot = Join-Path $repoRoot 'runtime/ocr'
if ([string]::IsNullOrWhiteSpace($NuGetRoot)) {
    $NuGetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
}
$modelBase = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx'
$rapidSource = 'https://raw.githubusercontent.com/BobLd/RapidOcrNet/e3f71c97ed106fec71aadb38a603a9382e444995'
$downloads = @(
    @{ Path='v6/PP-OCRv6_det_small.onnx'; Url="$modelBase/PP-OCRv6/det/PP-OCRv6_det_small.onnx"; Hash='090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f' },
    @{ Path='v6/PP-OCRv6_rec_small.onnx'; Url="$modelBase/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"; Hash='6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884' },
    @{ Path='v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'; Url="$modelBase/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"; Hash='54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7' },
    @{ Path='v6/ppocrv6_dict.txt'; Url="$rapidSource/RapidOcrNet/models/v6/ppocrv6_dict.txt"; Hash='b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d' },
    @{ Path='licenses/RapidOcrNet-LICENSE.txt'; Url="$rapidSource/LICENSE.txt"; Hash='c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4' },
    @{ Path='licenses/RapidOcrNet-NOTICE.txt'; Url="$rapidSource/NOTICE.txt"; Hash='fe492bb607c388e448b9fd64fea0c115d6b4511632aecef7f0d35f519e64d590' }
)
foreach ($item in $downloads) {
    $destination = [System.IO.Path]::GetFullPath((Join-Path $ocrRoot $item.Path))
    if (-not $destination.StartsWith($ocrRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated path.' }
    New-Item -ItemType Directory -Force ([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $item.Hash) {
        Write-Output "Verified $($item.Path)"
        continue
    }
    Get-VerifiedDownload -Uri $item.Url -Path $destination -Sha256 $item.Hash
    Write-Output "Downloaded and verified $($item.Path)"
}
$licenses = @(
    @{ Source='microsoft.ml.onnxruntime/1.29.0/LICENSE'; Name='ONNXRuntime-LICENSE.txt' },
    @{ Source='microsoft.ml.onnxruntime/1.29.0/ThirdPartyNotices.txt'; Name='ONNXRuntime-ThirdPartyNotices.txt' },
    @{ Source='skiasharp/3.119.1/LICENSE.txt'; Name='SkiaSharp-LICENSE.txt' },
    @{ Source='skiasharp.nativeassets.win32/3.119.1/THIRD-PARTY-NOTICES.txt'; Name='SkiaSharp-ThirdPartyNotices.txt' },
    @{ Source='clipper2/2.0.0/License.txt'; Name='Clipper2-LICENSE.txt' }
)
foreach ($license in $licenses) {
    $source = Join-Path $NuGetRoot $license.Source
    if (-not (Test-Path -LiteralPath $source)) { throw "Restore NuGet dependencies before downloading OCR models. Missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $ocrRoot ('licenses/' + $license.Name)) -Force
}
$total = (Get-ChildItem -LiteralPath $ocrRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Output ('Offline OCR model bundle ready: {0:N2} MiB' -f ($total / 1MB))
