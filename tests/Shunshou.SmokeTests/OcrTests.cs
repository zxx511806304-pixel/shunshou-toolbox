using Shunshou.Core;
using System.Buffers.Binary;
using ImageMagick;
using System.Security.Cryptography;
using Windows.Media.Ocr;

namespace Shunshou.SmokeTests;

public static class OcrTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "ocr"); Directory.CreateDirectory(folder);
        var service = new OcrService();
        bool bundled = File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "ocr", "v6", "PP-OCRv6_rec_small.onnx"));
        Console.WriteLine($"OCR test language resources: {string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(x => x.LanguageTag))}");
        await MustThrow<FileNotFoundException>(() => service.RecognizeAsync(Path.Combine(folder, "missing.png")));
        string path = Path.Combine(folder, "截图文字.png");
        bool chinese = bundled || OcrEngine.AvailableRecognizerLanguages.Any(x => x.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-zh-en.png"), path, true);
        if (!bundled && OcrEngine.AvailableRecognizerLanguages.Count == 0)
        {
            try { await service.RecognizeAsync(path); throw new Exception("没有 OCR 组件时不应报告成功"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("语言组件"))
            { Console.WriteLine("SKIP OCR: 此电脑没有 Windows OCR 语言组件；已验证明确错误路径。"); return; }
        }
        string result = await service.RecognizeAsync(path);
        string normalized = Normalize(result);
        Check(normalized.Contains("SHUNSHOUTOOLBOX") && normalized.Contains("HELLO12345"), "OCR 必须真实识别英文和数字。结果：" + result);
        if (chinese) Check(normalized.Contains("文字识别") && normalized.Contains("学习办公"), "OCR 必须真实识别中文。结果：" + result);
        await File.WriteAllTextAsync(Path.Combine(folder, "识别结果.txt"), result);
        Console.WriteLine($"PASS OCR: {service.EngineDescription}，截图英文大小写、数字{(chinese ? "及中文" : "")}实际识别成功");

        string tiffPath = Path.Combine(folder, "多页扫描件.tiff");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(path));
            frames.Add(new MagickImage(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-long.png")));
            frames.Write(tiffPath, MagickFormat.Tiff);
        }
        var dimensions = OcrService.ReadImageDimensions(tiffPath);
        var expectedDimensions = OcrService.ReadImageDimensions(path);
        Check(dimensions == expectedDimensions, "TIFF 应读取第一页尺寸。");
        byte[] tiffHash = SHA256.HashData(await File.ReadAllBytesAsync(tiffPath));
        string tiffResult = await service.RecognizeAsync(tiffPath);
        string tiffText = Normalize(tiffResult);
        Check(tiffText.Contains("SHUNSHOUTOOLBOX") && tiffText.Contains("HELLO12345") && !tiffText.Contains("LASTSCREEN67890"),
            "TIFF 应真实识别第一页且不混入后续页。结果：" + tiffResult);
        if (chinese) Check(tiffText.Contains("文字识别") && tiffText.Contains("学习办公"), "TIFF 中文识别缺失。");
        byte[] tiffHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(tiffPath));
        Check(tiffHash.SequenceEqual(tiffHashAfter), "TIFF 识别修改了原件。");
        await File.WriteAllTextAsync(Path.Combine(folder, "TIFF第一页识别结果.txt"), tiffResult);
        Console.WriteLine("PASS OCR: 多页 TIFF 仅识别第一页，中英文及数字正确，原件哈希不变");

        string oversizedTiff = Path.Combine(folder, "超大TIFF尺寸头.tiff");
        byte[] tiffBytes = await File.ReadAllBytesAsync(tiffPath);
        PatchTiffDimensions(tiffBytes, 20000, 20000);
        await File.WriteAllBytesAsync(oversizedTiff, tiffBytes);
        try { await service.RecognizeAsync(oversizedTiff); throw new Exception("应在解码 TIFF 像素前拒绝超大尺寸"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("一亿像素")) { }
        Console.WriteLine("PASS OCR: TIFF 尺寸头超过一亿像素时，在归一化解码前拒绝");

        string longPath = Path.Combine(folder, "长截图.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-long.png"), longPath, true);
        string longResult = await service.RecognizeAsync(longPath);
        string longText = Normalize(longResult);
        await File.WriteAllTextAsync(Path.Combine(folder, "长图识别结果.txt"), longResult);
        Check(longText.Contains("FIRSTSCREEN12345") && longText.Contains("LASTSCREEN67890"), "长截图必须同时识别顶部与底部内容。结果：" + longResult);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await MustThrow<OperationCanceledException>(() => service.RecognizeAsync(path, cancelled.Token));
        string oversized = Path.Combine(folder, "超大尺寸头.png");
        byte[] png = await File.ReadAllBytesAsync(path);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 20000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 20000);
        uint crc = uint.MaxValue;
        foreach (byte b in png.AsSpan(12, 17))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
        }
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), crc ^ uint.MaxValue);
        await File.WriteAllBytesAsync(oversized, png);
        try { await service.RecognizeAsync(oversized); throw new Exception("应在解码大像素缓冲前拒绝超大图片"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("一亿像素")) { }
        Console.WriteLine("PASS OCR: 超过系统单图高度的长截图分段识别 / 取消 / 缺失文件");
        Console.WriteLine("PASS OCR: 先读尺寸，拒绝超大像素头，避免解码分配");
    }
    private static string Normalize(string value) => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
    private static void PatchTiffDimensions(byte[] bytes, uint width, uint height)
    {
        // Magick's small Windows fixture is a classic little-endian TIFF. Alter only
        // its first page's metadata, producing a large header without allocating pixels.
        Check(bytes[0] == 'I' && bytes[1] == 'I' && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) == 42,
            "测试 TIFF 应为标准小端格式。");
        int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        int changed = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = offset + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry));
            if (tag is not 256 and not 257) continue;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 2));
            uint value = tag == 256 ? width : height;
            if (type == 3) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entry + 8), checked((ushort)value));
            else if (type == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 8), value);
            else throw new InvalidOperationException("测试 TIFF 尺寸字段类型不正确。");
            changed++;
        }
        Check(changed == 2, "测试 TIFF 缺少尺寸字段。");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task MustThrow<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"预期出现 {typeof(T).Name}，操作却成功了。");
    }
}
