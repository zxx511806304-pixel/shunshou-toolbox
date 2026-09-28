using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shunshou.Core;

public sealed record ShopResourceData(Uri SimStoreUri, Uri MembershipStoreUri, byte[] QrImageBytes);

/// <summary>Reads the signed, encrypted shop resources without writing plaintext files.</summary>
public static class ShopResources
{
    internal const string ResourceName = "Shunshou.Core.ShopResources.bin";
    internal const int MaximumPackageBytes = 12 * 1024 * 1024;
    private const int HeaderBytes = 44;
    private const int SignatureBytes = 384; // RSA-3072, PSS with SHA-256.
    private const int MaximumImageBytes = 8 * 1024 * 1024;

    public static ShopResourceData Load()
    {
        using var stream = typeof(ShopResources).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("店铺资源不可用。");
        if (stream.Length < HeaderBytes + SignatureBytes || stream.Length > MaximumPackageBytes)
            throw new InvalidDataException("店铺资源大小无效。");
        var package = new byte[checked((int)stream.Length)];
        stream.ReadExactly(package);
        return Decode(package);
    }

    internal static ShopResourceData Decode(ReadOnlySpan<byte> package)
    {
        if (package.Length < HeaderBytes + SignatureBytes || package.Length > MaximumPackageBytes)
            throw new InvalidDataException("店铺资源大小无效。");
        if (!package[..8].SequenceEqual("SHTSHOP1"u8) || BinaryPrimitives.ReadInt32LittleEndian(package[8..12]) != 1)
            throw new InvalidDataException("店铺资源版本无效。");
        var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(package[40..44]);
        if (ciphertextLength <= 0 || ciphertextLength > MaximumPackageBytes - HeaderBytes - SignatureBytes ||
            package.Length != HeaderBytes + ciphertextLength + SignatureBytes)
            throw new InvalidDataException("店铺资源结构无效。");

        var signedLength = HeaderBytes + ciphertextLength;
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ShopResourceKeys.PublicKey), out _);
        // Authenticate the complete header, nonce, authentication tag and ciphertext before decryption.
        if (!rsa.VerifyData(package[..signedLength], package[signedLength..], HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("店铺资源校验失败。");

        var plaintext = new byte[ciphertextLength];
        var key = Convert.FromBase64String(ShopResourceKeys.DecryptionKey);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(package[12..24], package.Slice(HeaderBytes, ciphertextLength), package[24..40], plaintext, package[..12]);
            using var payload = new MemoryStream(plaintext, writable: false);
            using var reader = new BinaryReader(payload, new UTF8Encoding(false, true));
            var simUri = ReadUri(reader, "ym.ksjhaoka.com", ShopResourceKeys.SimUriHash);
            var membershipUri = ReadUri(reader, "vip.ksjhaoka.com", ShopResourceKeys.MembershipUriHash);
            var imageLength = reader.ReadInt32();
            if (imageLength < 24 || imageLength > MaximumImageBytes || imageLength != payload.Length - payload.Position)
                throw new InvalidDataException("客服二维码数据无效。");
            var image = reader.ReadBytes(imageLength);
            if (!image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !image.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
                BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16, 4)) != 776 ||
                BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20, 4)) != 776)
                throw new InvalidDataException("客服二维码图片无效。");
            return new ShopResourceData(simUri, membershipUri, image);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static Uri ReadUri(BinaryReader reader, string allowedHost, string expectedHash)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > 2048 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("店铺地址无效。");
        var bytes = reader.ReadBytes(length);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expectedHash)))
            throw new InvalidDataException("店铺地址校验失败。");
        var value = new UTF8Encoding(false, true).GetString(bytes);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.IdnHost, allowedHost, StringComparison.Ordinal) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Fragment.Length != 0)
            throw new InvalidDataException("店铺地址无效。");
        return uri;
    }
}
