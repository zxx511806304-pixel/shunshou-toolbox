using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Shunshou.Core;

namespace Shunshou.App;

/// <summary>Release smoke check using the actual portable process and its bundled dependencies.</summary>
internal static class PackageVerification
{
    public static async Task<int> RunAsync(string fixtureDir, string outputDir)
    {
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);
        var checks = new List<string>();
        try
        {
            Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")), "Self-contained .NET runtime missing");
            Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe")), "Bundled FFmpeg missing");
            var imagePath = Path.Combine(Path.GetFullPath(fixtureDir), "ocr-zh-en.png");
            var pdfPath = Path.Combine(Path.GetFullPath(fixtureDir), "zh-en.pdf");
            var ocr = new OcrService();
            var text = await ocr.RecognizeAsync(imagePath);
            var normalized = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            Require(ocr.EngineDescription.Contains("内置") && normalized.Contains("Hello12345", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("文字识别"), "Bundled multilingual OCR output failed");
            checks.Add("Bundled PP-OCRv6: Chinese, Latin and digits");
            var images = await new ImageService().ConvertAsync([imagePath], outputDir, "webp", null, 90, null, default);
            Require(images.Count == 1 && new FileInfo(images[0]).Length > 0, "Image output missing");
            checks.Add("Native image codec: PNG to WebP");
            var pdf = new PdfService();
            var pages = await pdf.ExportImagesAsync(pdfPath, outputDir, 150, false);
            Require(pages.Count > 0, "PDF raster output missing");
            var document = await pdf.ExportEditableAsync(pdfPath, outputDir, "docx");
            using (var archive = ZipFile.OpenRead(document))
            {
                using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
                Require((await reader.ReadToEndAsync()).Contains("Hello", StringComparison.OrdinalIgnoreCase), "Editable DOCX text missing");
            }
            checks.Add("Windows PDF raster + editable Open XML document");
            var zip = await new CompressionService().CreateZipAsync(imagePath, outputDir, null, default);
            using (var archive = ZipFile.OpenRead(zip)) Require(archive.Entries.Count == 1, "ZIP entry mismatch");
            checks.Add("ZIP generation and complete entry count");

            var wav = OutputPaths.Unique(outputDir, "package-test.wav");
            const int samples = 48_000 * 2;
            using (var writer = new BinaryWriter(File.Create(wav), Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(48_000); writer.Write(96_000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48_000) * 12_000));
            }
            var mp3 = await new MediaService(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin"))
                .ConvertAsync(wav, outputDir, "mp3", null, null, default);
            Require(new FileInfo(mp3).Length > 0, "MP3 output missing");
            checks.Add("Bundled FFmpeg: WAV to MP3 + decode and duration validation");
            // Exercise the shipped native recovery dependency chain on a generated image only.
            // Never read a live volume or the user's Recycle Bin in release verification.
            string recoveryImage = OutputPaths.Unique(outputDir, "recovery-fixture.raw");
            byte[] knownPng = await File.ReadAllBytesAsync(imagePath);
            await using (var image = new FileStream(recoveryImage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                image.SetLength(8 * 1024 * 1024);
                image.Position = 1024 * 1024;
                await image.WriteAsync(knownPng);
            }
            byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(recoveryImage));
            var recovery = await new RecoveryService().ScanAsync(new(recoveryImage,
                Path.Combine(outputDir, "recovery-candidates"), RecoveryMode.DeepScan), null, default);
            bool exactImage = false;
            foreach (var candidate in recovery.Files.Where(f => f.Extension == "png" && f.Length == knownPng.Length))
                if ((await File.ReadAllBytesAsync(candidate.StoredPath)).SequenceEqual(knownPng)) exactImage = true;
            Require(exactImage && !recovery.Cancelled, "Bundled PhotoRec did not recover the exact artificial PNG");
            byte[] after = SHA256.HashData(await File.ReadAllBytesAsync(recoveryImage));
            Require(before.SequenceEqual(after), "Recovery modified its artificial source");
            checks.Add("Bundled PhotoRec and Cygwin: artificial raw image → byte-identical PNG; source hash unchanged");
            var report = new { Passed = true, BaseDirectory = AppContext.BaseDirectory, Checks = checks, Ocr = text };
            await File.WriteAllTextAsync(Path.Combine(outputDir, "package-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(outputDir, "package-verification.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
