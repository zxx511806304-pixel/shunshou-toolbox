using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shunshou.Setup;

internal sealed record PayloadMetadata(string Product, string Version, string Architecture, string ZipRoot, long ZipBytes, string Sha256);

internal static class Payload
{
    public static PayloadMetadata ReadMetadata()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shunshou.Payload.json")
            ?? throw new InvalidDataException("这个程序还没有打包软件内容，请使用正式发布的 EXE 包。");
        var metadata = JsonSerializer.Deserialize<PayloadMetadata>(stream) ?? throw new InvalidDataException("软件包信息不完整。");
        if (metadata.Product != "顺手工具箱" || metadata.Architecture != "win-x64" || metadata.ZipBytes <= 0
            || metadata.Sha256 is null || metadata.Sha256.Length != 64 || !metadata.Sha256.All(Uri.IsHexDigit)
            || !Version.TryParse(metadata.Version, out _) || metadata.ZipRoot != $"ShunshouToolbox-{metadata.Version}-win-x64")
            throw new InvalidDataException("软件包信息不正确，请重新下载。");
        return metadata;
    }

    public static async Task<string> ExtractAsync(string temporaryDirectory, PayloadMetadata metadata, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(temporaryDirectory);
        string path = Path.Combine(temporaryDirectory, "payload.zip");
        await using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shunshou.Payload.zip")
            ?? throw new InvalidDataException("软件包内容缺失，请重新下载。");
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        int count;
        progress?.Report("正在读取并校验软件包…");
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            hash.AppendData(buffer, 0, count);
            copied += count;
            if (copied > metadata.ZipBytes) throw new InvalidDataException("软件包长度不正确，请重新下载。");
        }
        await destination.FlushAsync(ct);
        if (copied != metadata.ZipBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("软件包校验失败，请重新下载。原来的软件没有被替换。");
        return path;
    }

    public static void DeleteOwnTemporaryDirectory(string path)
    {
        // Only this run's UUID directory under our own temporary root is eligible.
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ShunshouSetup"));
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return;
        try
        {
            if (!Directory.Exists(full)) return;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            // We create exactly payload.zip, so avoid a recursive delete of any unknown entries.
            var payload = Path.Combine(full, "payload.zip");
            if (File.Exists(payload) && (File.GetAttributes(payload) & FileAttributes.ReparsePoint) == 0) File.Delete(payload);
            if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        catch { /* A locked temporary payload may be cleaned by Windows later. */ }
    }
}
