using Shunshou.Core;
using System.Buffers.Binary;
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
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task MustThrow<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"预期出现 {typeof(T).Name}，操作却成功了。");
    }
}
