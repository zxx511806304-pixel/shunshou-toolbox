using System.Security.Cryptography;
using Shunshou.Core;

var data = ShopResources.Load();
Check(data.SimStoreUri.OriginalString == "https://ym.ksjhaoka.com/?s=Ma2ssY9L213358", "Original SIM store URL retained");
Check(data.MembershipStoreUri.OriginalString == "https://vip.ksjhaoka.com/?s=Ma2ssY9L213358", "Original membership URL retained");
if (args.Length != 1) throw new ArgumentException("Provide the original owner PNG for byte-for-byte verification.");
Check(data.QrImageBytes.AsSpan().SequenceEqual(File.ReadAllBytes(args[0])), "QR image unchanged byte for byte");

using var stream = typeof(ShopResources).Assembly.GetManifestResourceStream(ShopResources.ResourceName)!;
var package = new byte[checked((int)stream.Length)];
stream.ReadExactly(package);
var changedCiphertext = (byte[])package.Clone();
changedCiphertext[44] ^= 1;
Rejected<CryptographicException>(changedCiphertext, "Ciphertext change rejected before decryption");
var changedSignature = (byte[])package.Clone();
changedSignature[^1] ^= 1;
Rejected<CryptographicException>(changedSignature, "Signature change rejected");
Rejected<InvalidDataException>(package[..^1], "Truncated envelope rejected");
var changedVersion = (byte[])package.Clone();
changedVersion[8] = 2;
Rejected<InvalidDataException>(changedVersion, "Unsupported format version rejected");
Check(!System.Text.Encoding.Latin1.GetString(package).Contains("Ma2ssY9L213358", StringComparison.Ordinal), "Resource has no plaintext referral token");
Console.WriteLine("PASS: 8 focused shop-resource checks.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS: " + name);
}

static void Rejected<T>(byte[] package, string name) where T : Exception
{
    try { ShopResources.Decode(package); }
    catch (T) { Console.WriteLine("PASS: " + name); return; }
    throw new Exception("Accepted invalid resource: " + name);
}
