using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;

namespace Shunshou.Core;

/// <summary>One page of the source document as the page organizer sees it.</summary>
public sealed record PdfPageInfo(int Number, int Rotation, bool LikelyBlank, double WidthPoints, double HeightPoints);

/// <summary>Keeps source page <paramref name="SourcePage"/> (1-based) with an extra quarter-turn rotation.</summary>
public sealed record PdfPageEdit(int SourcePage, int Rotation);

public sealed partial class PdfService
{
    /// <summary>
    /// Reads page count, current rotation and a conservative "looks blank" hint. The hint only reports
    /// pages without extractable text and without embedded images, so a page a user still wants is never
    /// dropped automatically – the caller decides.
    /// </summary>
    public IReadOnlyList<PdfPageInfo> InspectPages(string input, CancellationToken ct = default)
    {
        ValidateInput(input);
        var blank = new List<bool>();
        try
        {
            using var pig = PdfDocument.Open(input);
            foreach (var page in pig.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                bool empty = page.Letters.Count == 0 && page.NumberOfImages == 0 && page.Paths.Count == 0;
                blank.Add(empty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("无法读取该 PDF 的页面内容：" + ex.Message, ex);
        }
        using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var pages = new List<PdfPageInfo>();
        for (int index = 0; index < source.PageCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            var page = source.Pages[index];
            int rotation = ((page.Rotate % 360) + 360) % 360;
            bool likelyBlank = index < blank.Count && blank[index];
            pages.Add(new(index + 1, rotation, likelyBlank, page.Width.Point, page.Height.Point));
        }
        if (pages.Count == 0) throw new InvalidDataException("该 PDF 没有页面。");
        return pages;
    }

    /// <summary>Writes a new PDF that contains exactly the listed pages, in the listed order and rotation.</summary>
    public Task<string> RebuildAsync(string input, string outputDir, IReadOnlyList<PdfPageEdit> pages,
        IProgress<ToolProgress>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        ValidateInput(input);
        if (pages.Count == 0) throw new ArgumentException("请至少保留一页。", nameof(pages));
        Directory.CreateDirectory(outputDir);
        string staging = CreateStaging(outputDir);
        try
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            using var output = new PdfSharp.Pdf.PdfDocument();
            for (int index = 0; index < pages.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var edit = pages[index];
                if (edit.SourcePage < 1 || edit.SourcePage > source.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(pages), $"第 {edit.SourcePage} 页不存在。");
                var page = output.AddPage(source.Pages[edit.SourcePage - 1]);
                int rotation = ((edit.Rotation % 360) + 360) % 360;
                if (rotation % 90 != 0) throw new ArgumentException("旋转角度只能是 0、90、180 或 270 度。", nameof(pages));
                if (rotation != 0) page.Rotate = rotation;
                progress?.Report(new(90.0 * (index + 1) / pages.Count, $"已放置第 {index + 1}/{pages.Count} 页"));
            }
            if (output.PageCount != pages.Count) throw new InvalidDataException("生成结果的页数与选择不一致，未发布结果。");
            string temporary = Path.Combine(staging, "pages.pdf");
            output.Save(temporary);
            ct.ThrowIfCancellationRequested();
            string final = UniquePath(outputDir, Path.GetFileNameWithoutExtension(input) + "_pages", ".pdf");
            File.Move(temporary, final);
            progress?.Report(new(100, $"已生成 {pages.Count} 页的新 PDF"));
            return final;
        }
        finally { CleanupStaging(staging); }
    }, ct);
}
