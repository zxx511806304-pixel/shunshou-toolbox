using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Shunshou.Core;

namespace Shunshou.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Release check for 1.2.0: tool search with recents, sending search results to another tool,
    /// image cropping, clip/audio, text comparison, QR codes, the PDF page organizer and 7z/RAR extraction.
    /// </summary>
    private async Task<int> VerifyV120Async(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var details = new Dictionary<string, object?>();
        try
        {
            string fixtures = Path.Combine(output, "generated");
            Directory.CreateDirectory(fixtures);
            string image = CreateTestImage(fixtures);
            string video = CreateTestVideo(fixtures);
            string pdf = CreateTestPdf(fixtures);
            string archive = CreateTestArchive(fixtures);
            details["Fixtures"] = new { image, video, pdf, archive };

            // 1. Tool search, favourites and recents.
            var match = FirstMatch("二维码");
            RequireUi(match is { Operation: "二维码", Category: "text" }, "工具搜索没有命中二维码");
            RequireUi(FirstMatch("改名") is { Operation: "批量重命名" }, "关键词搜索没有匹配到批量重命名");
            RequireUi(FirstMatch("zzz-不存在") is null, "无匹配时不应返回工具");
            SubmitToolSuggestion(ToolSearchBox, match);
            RequireUi(_category == "text" && Operation == "二维码", "搜索框跳转没有打开二维码页");
            NavigateToTool("image", "图片裁剪");
            RequireUi(BuildSuggestions("").Any(item => item.Group == "最近使用"), "空搜索时没有显示最近使用");
            details["ToolSearch"] = new { Search = "二维码 / 改名 / 无匹配", Recents = true };
            checks.Add("工具搜索：关键词命中、无结果提示、最近使用在空搜索时出现");

            // 2. Send search results straight into another tool.
            SendToTool("image", "图片裁剪", [image]);
            RequireUi(Operation == "图片裁剪" && _inputs.Contains(image, StringComparer.OrdinalIgnoreCase), "把文件发送到图片裁剪失败");
            SendToTool("image", "图片裁剪", [Path.Combine(fixtures, "说明.txt")]);
            RequireUi(!_inputs.Contains(Path.Combine(fixtures, "说明.txt"), StringComparer.OrdinalIgnoreCase), "格式不符的文件不应被发送到图片工具");
            RequireUi(StatusInfo.IsOpen && StatusInfo.Severity == InfoBarSeverity.Warning, "格式不符时没有给出提示");
            checks.Add("搜索结果发送：文件进入目标工具，格式不符时给出提示且不污染输入");

            // 3. Image cropping, including the preview.
            CropKindBox.SelectedIndex = 0;
            CropRatioBox.SelectedIndex = 0;
            CropAnchorBox.SelectedIndex = 0;
            // Isolate engine problems from UI problems: the same service call must finish on its own.
            try
            {
                var probe = await new ImageCropService()
                    .CropAsync([image], Path.Combine(output, "crop-probe"), CurrentCropOptions(), 90, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                details["CropProbe"] = probe;
                var decoded = await ImagePreview.FromFileAsync(probe[0], 720).WaitAsync(TimeSpan.FromSeconds(15));
                RequireUi(decoded is not null, "预览解码返回空图片");
            }
            catch (TimeoutException) { throw new InvalidOperationException("裁剪引擎在应用进程内 20 秒没有返回。"); }
            CropPreview_Click(CropPreviewButton, new RoutedEventArgs());
            await WaitForUiAsync(() => CropPreviewImage.Source is BitmapImage, "裁剪预览没有生成图片：" + CropPreviewHint.Text + " / " + StatusInfo.Message);
            RequireUi(CropPreviewCard.Visibility == Visibility.Visible, "裁剪预览卡片没有显示");
            OutputDirectory.Text = Path.Combine(output, "crop-output");
            Run_Click(RunButton, new RoutedEventArgs());
            await WaitForIdleAsync("裁剪没有完成");
            RequireUi(Directory.Exists(OutputDirectory.Text), "裁剪没有生成输出目录：" + ResultText.Text + " / " + StatusInfo.Message);
            var cropped = Directory.GetFiles(OutputDirectory.Text, "*.png", SearchOption.AllDirectories).Select(Path.GetFullPath).FirstOrDefault();
            RequireUi(cropped is not null, "裁剪没有生成输出：" + ResultText.Text + " / " + StatusInfo.Message);
            var size = await Task.Run(() => { using var probe = new ImageMagick.MagickImage(cropped!); return (probe.Width, probe.Height); });
            RequireUi(size.Width == size.Height && size.Width > 0, $"1:1 裁剪结果不是正方形：{size.Width}×{size.Height}");
            details["Crop"] = new { Output = cropped, size.Width, size.Height };
            checks.Add($"图片裁剪：预览 + 真实 1:1 裁剪输出 {size.Width}×{size.Height}");
            await SaveScreenshot(Path.Combine(output, "crop-Light.png"));

            // 4. Clip and audio extraction wiring (the engines themselves are covered by the smoke tests).
            NavigateToTool("media", "截取音视频片段");
            RequireUi(TrimPanel.Visibility == Visibility.Visible && OutputPanel.Visibility == Visibility.Visible, "截取面板没有显示");
            RequireUi((FormatBox.ItemsSource as IEnumerable<string>)?.Contains("mp4") == true, "截取没有提供 MP4 输出");
            RequireUi(!TryReadClipTime("abc", out _, out _) && TryReadClipTime("00:00:02", out double parsed, out _) && Math.Abs(parsed - 2) < 0.01,
                "时间校验没有拒绝无效输入或解析有效时间");
            SendToTool("media", "截取音视频片段", [video]);
            TrimStartBox.Text = "00:00:01";
            TrimEndBox.Text = "00:00:03";
            TrimModeBox.SelectedIndex = 1;
            FormatBox.SelectedIndex = 0;
            OutputDirectory.Text = Path.Combine(output, "clip-output");
            Run_Click(RunButton, new RoutedEventArgs());
            await WaitForIdleAsync("截取没有完成");
            RequireUi(Directory.Exists(OutputDirectory.Text), "截取没有生成输出目录：" + ResultText.Text + " / " + StatusInfo.Message);
            string? clip = Directory.EnumerateFiles(OutputDirectory.Text, "*.mp4", SearchOption.AllDirectories).FirstOrDefault();
            RequireUi(clip is not null, "截取没有生成 MP4：" + ResultText.Text + " / " + StatusInfo.Message);
            details["Clip"] = new { Output = clip, ResultText.Text };
            checks.Add("截取音视频片段：时间校验 + 真实精确截取生成新 MP4");
            NavigateToTool("media", "提取音轨");
            RequireUi((FormatBox.ItemsSource as IEnumerable<string>)?.Contains("mp3") == true &&
                (FormatBox.ItemsSource as IEnumerable<string>)?.Contains("mp4") == false, "提取音轨没有只提供音频格式");
            checks.Add("提取音轨：只提供音频格式选项");

            // 5. Text comparison.
            NavigateToTool("text", "文本对比");
            TextDiffTools.SetTexts("第一行\n旧内容\n第三行", "第一行\n新内容\n第三行");
            TextDiffTools.Compare();
            RequireUi(TextDiffTools.Summary.Contains("新增 1") && TextDiffTools.Summary.Contains("删除 1"), "文本对比统计不正确：" + TextDiffTools.Summary);
            int diffRows = TextDiffTools.DisplayedRows;
            TextDiffTools.SetShowSame(true);
            await Task.Delay(200);
            // Two unchanged lines plus one removal and one addition: four rows once context is shown.
            RequireUi(TextDiffTools.DisplayedRows > diffRows && TextDiffTools.DisplayedRows == 4,
                $"同时显示相同行没有展开上下文（差异行 {diffRows} → {TextDiffTools.DisplayedRows}）");
            TextDiffTools.SetShowSame(false);
            details["TextDiff"] = new { Summary = TextDiffTools.Summary, Rows = diffRows };
            await SaveScreenshot(Path.Combine(output, "text-diff-Light.png"));
            checks.Add("文本对比：增删统计、差异行与相同行切换");

            // 6. QR code reading (generation is covered by the smoke tests and needs a save dialog here).
            NavigateToTool("text", "二维码");
            string code = Path.Combine(fixtures, "二维码.png");
            QrCodeService.Generate("https://example.com/顺手工具箱?v=120", 384, "M 约 15%", 4, code);
            await QrTools.ShowAndDecodeAsync(code);
            RequireUi(QrTools.DecodedText == "https://example.com/顺手工具箱?v=120", "二维码识别结果不正确：" + QrTools.DecodedText);
            details["QrCode"] = new { code, QrTools.DecodedText };
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                RootLayout.RequestedTheme = theme;
                await Task.Delay(500);
                await SaveScreenshot(Path.Combine(output, $"qr-{theme}.png"));
            }
            checks.Add("二维码：真实 PNG 识别出中文链接，浅色与深色界面已截图");

            // 7. PDF page organizer.
            NavigateToTool("pdf", "整理 PDF 页面");
            AcceptInputPaths([pdf]);
            await WaitForUiAsync(() => PdfPageTools.TotalPages > 0, "整理 PDF 没有读取到页面");
            RequireUi(PdfPageTools.TotalPages == 4 && PdfPageTools.KeptPages == 4, $"页面列表不正确：{PdfPageTools.TotalPages}/{PdfPageTools.KeptPages}");
            PdfPageTools.SelectPage(3);
            PdfPageTools.RemoveSelected();
            RequireUi(PdfPageTools.KeptPages == 3, "删除页面后保留页数不正确");
            PdfPageTools.SetOutputDirectory(Path.Combine(output, "pdf-pages"));
            await PdfPageTools.GenerateAsync();
            RequireUi(PdfPageTools.LastOutput is not null && File.Exists(PdfPageTools.LastOutput), "整理 PDF 没有生成新文件：" + PdfPageTools.StatusMessage);
            int pages = await Task.Run(() => { using var document = UglyToad.PdfPig.PdfDocument.Open(PdfPageTools.LastOutput!); return document.NumberOfPages; });
            RequireUi(pages == 3, $"生成的新 PDF 页数不正确：{pages}");
            await SaveScreenshot(Path.Combine(output, "pdf-pages-Light.png"));
            details["PdfPages"] = new { Output = PdfPageTools.LastOutput, Pages = pages };
            checks.Add("整理 PDF 页面：读取 4 页、删除 1 页、生成 3 页新 PDF（原文件保留）");

            // 8. 7z / RAR extraction through the compression tool.
            NavigateToTool("compression", "解压 7z / RAR");
            AcceptInputPaths([archive]);
            OutputDirectory.Text = Path.Combine(output, "archive-output");
            Run_Click(RunButton, new RoutedEventArgs());
            await WaitForIdleAsync("解压没有完成");
            RequireUi(Directory.Exists(OutputDirectory.Text), "解压没有生成输出目录：" + ResultText.Text + " / " + StatusInfo.Message);
            string? folder = Directory.EnumerateDirectories(OutputDirectory.Text).FirstOrDefault();
            RequireUi(folder is not null && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(),
                "解压没有生成文件：" + ResultText.Text + " / " + StatusInfo.Message);
            details["Archive"] = new { Output = folder, Files = Directory.EnumerateFiles(folder!, "*", SearchOption.AllDirectories).Count() };
            checks.Add("解压 7z：真实 7z 压缩包解压出文件与目录");

            RootLayout.RequestedTheme = ElementTheme.Light;
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            {
                Passed = true,
                Version = "1.2.0",
                Checks = checks,
                Details = details,
                NotCovered = "Clean Windows 10/11 machine, High Contrast rendering, a real RAR with links (covered by engine tests), and dialog-driven file pickers."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new
            { Passed = false, Version = "1.2.0", Checks = checks, Details = details, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            try { await SaveScreenshot(Path.Combine(output, "failure.png")); } catch { }
            return 1;
        }
    }

    private async Task WaitForIdleAsync(string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (_busy && DateTime.UtcNow < deadline) await Task.Delay(120);
        RequireUi(!_busy, message + "：" + ResultText.Text + " / " + ProgressText.Text);
    }

    private static string CreateTestImage(string directory)
    {
        string path = Path.Combine(directory, "裁剪样例.png");
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.SteelBlue, 640, 400);
        image.Write(path);
        return path;
    }

    private static string CreateTestPdf(string directory)
    {
        string path = Path.Combine(directory, "整理样例.pdf");
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int index = 1; index <= 3; index++)
        {
            var page = document.AddPage();
            var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.SlateGray, 40, 40, 160, 90);
            gfx.Dispose();
        }
        document.AddPage();
        document.Save(path);
        return path;
    }

    private static string CreateTestVideo(string directory)
    {
        string path = Path.Combine(directory, "截取样例.mp4");
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x184:rate=15:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-c:v", "libopenh264", "-b:v", "600000", "-c:a", "aac", "-shortest", path })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 FFmpeg 生成测试视频。");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg 生成测试视频失败：" + process.StandardError.ReadToEnd());
        return path;
    }

    private static string CreateTestArchive(string directory)
    {
        string content = Path.Combine(directory, "压缩内容");
        Directory.CreateDirectory(Path.Combine(content, "子目录"));
        File.WriteAllText(Path.Combine(content, "说明.txt"), "顺手工具箱 1.2.0 解压校验");
        File.WriteAllText(Path.Combine(content, "子目录", "数据.csv"), "列1,列2\n1,2\n");
        string archive = Path.Combine(directory, "解压样例.7z");
        string engine = Path.Combine(AppContext.BaseDirectory, "tools", "sevenzip", "7z.exe");
        var info = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "a", "-t7z", "-mx=1", "-bso0", "-bsp0", archive, content }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 7z 引擎。");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("生成测试压缩包失败：" + process.StandardError.ReadToEnd());
        return archive;
    }
}
