param(
    [Parameter(Mandatory = $true)][string]$QrImagePath
)
# Replaces only the support QR image inside the protected shop resource.
# Store links are kept as opaque bytes: they are never decoded, displayed or rewritten.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$privateDirectory = Join-Path $repoRoot '.tools/private-shop'
$privateKeyPath = Join-Path $privateDirectory 'shop-signing-key.pk8'
$symmetricKeyPath = Join-Path $privateDirectory 'shop-encryption-key.bin'
$resourcePath = Join-Path $repoRoot 'src/Shunshou.Core/Resources/ShopResources.bin'

$qrBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $QrImagePath).Path)
if ($qrBytes.Length -lt 24 -or $qrBytes.Length -gt 8MB -or
    [Convert]::ToHexString($qrBytes[0..7]) -ne '89504E470D0A1A0A' -or
    [Convert]::ToHexString($qrBytes[16..23]) -ne '0000030800000308') {
    throw 'Expected a tightly cropped 776x776 PNG QR image.'
}

$package = [IO.File]::ReadAllBytes($resourcePath)
$headerBytes = 44
if ([Text.Encoding]::ASCII.GetString($package[0..7]) -ne 'SHTSHOP1' -or [BitConverter]::ToInt32($package, 8) -ne 1) {
    throw 'Shop resource version invalid.'
}
$ciphertextLength = [BitConverter]::ToInt32($package, 40)
if ($package.Length -ne $headerBytes + $ciphertextLength + 384) { throw 'Shop resource structure invalid.' }

$aesKey = [IO.File]::ReadAllBytes($symmetricKeyPath)
if ($aesKey.Length -ne 32) { throw 'Expected a 256-bit encryption key.' }
$rsa = [Security.Cryptography.RSA]::Create(3072)
$plaintext = [byte[]]::new($ciphertextLength)
try {
    $read = 0
    $rsa.ImportPkcs8PrivateKey([IO.File]::ReadAllBytes($privateKeyPath), [ref]$read)
    if ($rsa.KeySize -ne 3072) { throw 'Expected a 3072-bit signing key.' }

    $ciphertextIn = [byte[]]::new($ciphertextLength)
    [Array]::Copy($package, $headerBytes, $ciphertextIn, 0, $ciphertextLength)
    $aes = [Security.Cryptography.AesGcm]::new($aesKey, 16)
    try { $aes.Decrypt($package[12..23], $ciphertextIn, $package[24..39], $plaintext, $package[0..11]) }
    finally { $aes.Dispose() }

    # Payload layout: int32 len + uri, int32 len + uri, int32 len + png. Replace only the final image block.
    $stream = [IO.MemoryStream]::new($plaintext)
    $uriBlocks = [IO.MemoryStream]::new()
    foreach ($i in 1..2) {
        $lenBytes = [byte[]]::new(4)
        $stream.ReadExactly($lenBytes, 0, 4) | Out-Null
        $len = [BitConverter]::ToInt32($lenBytes, 0)
        if ($len -le 0 -or $len -gt 2048) { throw 'Shop address block invalid.' }
        $uriBytes = [byte[]]::new($len)
        $stream.ReadExactly($uriBytes, 0, $len) | Out-Null
        $uriBlocks.Write($lenBytes, 0, 4)
        $uriBlocks.Write($uriBytes, 0, $len)
    }
    $imgLenBytes = [byte[]]::new(4)
    $stream.ReadExactly($imgLenBytes, 0, 4) | Out-Null
    if ([BitConverter]::ToInt32($imgLenBytes, 0) -ne ($stream.Length - $stream.Position)) { throw 'Shop image block invalid.' }

    $newPayload = [IO.MemoryStream]::new()
    $uriBlocks.Position = 0
    $uriBlocks.CopyTo($newPayload)
    $newPayload.Write([BitConverter]::GetBytes($qrBytes.Length), 0, 4)
    $newPayload.Write($qrBytes, 0, $qrBytes.Length)
    $newPlaintext = $newPayload.ToArray()

    $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $ciphertext = [byte[]]::new($newPlaintext.Length)
    $prefix = [byte[]]([Text.Encoding]::ASCII.GetBytes('SHTSHOP1') + [BitConverter]::GetBytes([int]1))
    $aes2 = [Security.Cryptography.AesGcm]::new($aesKey, 16)
    try { $aes2.Encrypt($nonce, $newPlaintext, $ciphertext, $tag, $prefix) } finally { $aes2.Dispose() }
    $signedBytes = [byte[]]($prefix + $nonce + $tag + [BitConverter]::GetBytes([int]$ciphertext.Length) + $ciphertext)
    $signature = $rsa.SignData($signedBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes($resourcePath, [byte[]]($signedBytes + $signature))
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($newPlaintext)
    Write-Output 'Shop QR image replaced; store links unchanged.'
} finally {
    if ($plaintext) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($plaintext) }
    if ($aesKey) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($aesKey) }
    $rsa.Dispose()
}
