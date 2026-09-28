using System.Text;
using ImageMagick;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace Shunshou.Core;

public sealed record QrCodeResult(string Text, string Format, int Width, int Height);

/// <summary>
/// Offline QR support: generate a PNG from text and read the text back from a picture.
/// Generation and decoding both run locally; nothing is sent anywhere.
/// </summary>
public static class QrCodeService
{
    public static readonly string[] ErrorLevels = ["L 约 7%", "M 约 15%", "Q 约 25%", "H 约 30%（最抗污损）"];
    private const int MaxTextLength = 2_000;
    private const int MaxDecodePixels = 40_000_000;
    private const int DecodeWorkingSize = 1_600;

    public static string Generate(string text, int size, string errorLevel, int margin, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("请先输入要生成二维码的文字或链接。", nameof(text));
        if (text.Length > MaxTextLength) throw new ArgumentException($"单个二维码最多容纳 {MaxTextLength} 个字符，请缩短内容。", nameof(text));
        if (size is < 64 or > 2_048) throw new ArgumentOutOfRangeException(nameof(size), "二维码边长请在 64–2048 像素之间。");
        if (margin is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(margin), "留白请在 0–16 个模块之间。");
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.ERROR_CORRECTION] = ParseLevel(errorLevel),
            [EncodeHintType.MARGIN] = margin
        };
        BitMatrix matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, hints);
        int width = matrix.Width, height = matrix.Height;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = matrix[x, y] ? (byte)0x00 : (byte)0xFF;
                int offset = (y * width + x) * 4;
                pixels[offset] = value; pixels[offset + 1] = value; pixels[offset + 2] = value; pixels[offset + 3] = 0xFF;
            }
        }
        using var image = new MagickImage(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        string full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string partial = full + ".partial";
        try
        {
            image.Write(partial, MagickFormat.Png);
            File.Move(partial, full, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
        return full;
    }

    public static QrCodeResult Decode(string imagePath)
    {
        string path = Path.GetFullPath(imagePath);
        if (!File.Exists(path)) throw new FileNotFoundException("图片不存在。", path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("请先将链接或在线占位图片保存为本地普通文件。");
        using var image = new MagickImage(path);
        image.AutoOrient();
        if ((ulong)image.Width * image.Height > MaxDecodePixels) throw new InvalidDataException("图片像素过大，请先缩小后再识别。");
        // A private copy is safe to resize; the user's file is never modified.
        using var working = (MagickImage)image.Clone();
        if (working.Width > DecodeWorkingSize || working.Height > DecodeWorkingSize) working.Resize(new MagickGeometry(DecodeWorkingSize, DecodeWorkingSize) { IgnoreAspectRatio = false });
        using var rgba = new MagickImage(working);
        rgba.ColorSpace = ColorSpace.sRGB;
        byte[] pixels = rgba.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("无法读取图片像素。");
        var source = new RGBLuminanceSource(pixels, (int)working.Width, (int)working.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, CharacterSet = "UTF-8", PossibleFormats = [BarcodeFormat.QR_CODE] } };
        Result? result = reader.Decode(source);
        if (result is null) throw new InvalidDataException("没有在图片中读到二维码。请使用清晰、平整、四周留白的二维码截图；反光、遮挡或过于模糊的图片可能无法识别。");
        string text = result.Text ?? "";
        if (text.Length == 0) throw new InvalidDataException("二维码内容为空。");
        return new(text, result.BarcodeFormat.ToString(), (int)image.Width, (int)image.Height);
    }

    private static ErrorCorrectionLevel ParseLevel(string value) => (value ?? "").TrimStart().ToUpperInvariant() switch
    {
        var text when text.StartsWith('L') => ErrorCorrectionLevel.L,
        var text when text.StartsWith('Q') => ErrorCorrectionLevel.Q,
        var text when text.StartsWith('H') => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M
    };

}
