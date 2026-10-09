using PdfSharp.Pdf.IO;
using Shunshou.Core;

namespace Shunshou.SmokeTests;

public static class MarkdownTests
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "markdown");
        Directory.CreateDirectory(folder);

        // 1) Headings levels 1-6
        var blocks = MarkdownService.Parse("# 一\n## 二\n### 三\n#### 四\n##### 五\n###### 六");
        CompressionTests.Check(blocks.Count == 6 && blocks[0] is MarkdownHeading { Level: 1 } && blocks[5] is MarkdownHeading { Level: 6 },
            "应解析 1-6 级标题");
        Console.WriteLine("PASS markdown: headings");

        // 2) Inline bold / italic / code and HTML escaping
        blocks = MarkdownService.Parse("这是 **粗体** 和 *斜体* 和 `代码` 以及 <script>alert(1)</script>");
        string html = MarkdownService.RenderBodyHtml(blocks);
        CompressionTests.Check(html.Contains("<strong>粗体</strong>"), "粗体应渲染为 strong");
        CompressionTests.Check(html.Contains("<em>斜体</em>"), "斜体应渲染为 em");
        CompressionTests.Check(html.Contains("<code>代码</code>"), "行内代码应渲染为 code");
        CompressionTests.Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>"), "文本中的 HTML 必须转义防注入");
        Console.WriteLine("PASS markdown: inline styles and escaping");

        // 3) Fenced code block keeps content verbatim and escaped
        blocks = MarkdownService.Parse("```csharp\nvar a = 1 < 2;\n```");
        CompressionTests.Check(blocks.Count == 1 && blocks[0] is MarkdownCode { Language: "csharp" } code && code.Code.Contains("var a = 1 < 2;"),
            "应解析带语言的代码块");
        html = MarkdownService.RenderBodyHtml(blocks);
        CompressionTests.Check(html.Contains("1 &lt; 2") && html.Contains("<pre><code>"), "代码块内容必须转义");
        Console.WriteLine("PASS markdown: code block");

        // 4) Unordered and ordered lists
        blocks = MarkdownService.Parse("- 甲\n- 乙\n\n1. 第一\n2. 第二");
        CompressionTests.Check(blocks.Count == 2
            && blocks[0] is MarkdownList { Ordered: false } unordered && unordered.Items.Count == 2
            && blocks[1] is MarkdownList { Ordered: true } ordered && ordered.Items.Count == 2,
            "应解析无序与有序列表");
        Console.WriteLine("PASS markdown: lists");

        // 5) Table
        blocks = MarkdownService.Parse("| 名称 | 数量 |\n| --- | --- |\n| 苹果 | 3 |\n| 梨 | 5 |");
        CompressionTests.Check(blocks.Count == 1 && blocks[0] is MarkdownTable table
            && table.Header.Count == 2 && table.Rows.Count == 2 && table.Rows[0][0] == "苹果",
            "应解析表格表头与数据行");
        Console.WriteLine("PASS markdown: table");

        // 6) Quote and horizontal rule
        blocks = MarkdownService.Parse("> 引用一句\n> 继续\n\n---");
        CompressionTests.Check(blocks.Count == 2 && blocks[0] is MarkdownQuote quote && quote.Children.Count == 1
            && blocks[1] is MarkdownRule, "应解析引用块与分割线");
        Console.WriteLine("PASS markdown: quote and rule");

        // 7) Links: safe href kept, dangerous scheme dropped, images become alt placeholders
        blocks = MarkdownService.Parse("[官网](https://example.com) [坏](javascript:alert(1)) ![示意图](a.png)");
        html = MarkdownService.RenderBodyHtml(blocks);
        CompressionTests.Check(html.Contains("href=\"https://example.com\""), "安全链接应保留 href");
        CompressionTests.Check(!html.Contains("javascript:"), "危险协议不应输出为链接");
        CompressionTests.Check(html.Contains("图片：示意图"), "图片应显示 alt 占位提示");
        Console.WriteLine("PASS markdown: links and images");

        // 8) PDF export produces a valid document
        string pdf = await MarkdownService.ExportPdfAsync(
            "# 标题\n\n正文 **加粗** 内容。\n\n- 项目一\n- 项目二\n\n| 列 | 值 |\n| - | - |\n| 甲 | 1 |",
            Path.Combine(folder, "导出.pdf"));
        CompressionTests.Check(File.Exists(pdf) && new FileInfo(pdf).Length > 0, "导出应生成非空 PDF");
        using (var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            CompressionTests.Check(doc.PageCount >= 1, "导出的 PDF 应至少有一页");
        Console.WriteLine("PASS markdown: PDF export");
    }
}
