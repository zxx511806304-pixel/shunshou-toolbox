using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Shunshou.Core;

public sealed partial class PdfService
{
    private static void WriteLayoutPresentation(string path, IReadOnlyList<PdfOfficePage> pages, CancellationToken ct)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var part = document.AddPresentationPart();
        var master = part.AddNewPart<SlideMasterPart>();
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(EmptyShapeTree()),
            new P.ColorMapOverride(new A.MasterColorMapping())) { Type = P.SlideLayoutValues.Blank, Preserve = true };
        layout.AddPart(master);
        master.SlideMaster = new P.SlideMaster(new P.CommonSlideData(EmptyShapeTree()),
            new P.ColorMap { Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649, RelationshipId = master.GetIdOfPart(layout) }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        var theme = master.AddNewPart<ThemePart>();
        theme.Theme = new A.Theme(ThemeElements()) { Name = "PDF 原版式" };
        var ids = new P.SlideIdList();
        int width = (int)Math.Clamp(Math.Round(pages[0].Width * 12700), 914400, 51206400);
        int height = (int)Math.Clamp(Math.Round(pages[0].Height * 12700), 914400, 51206400);
        part.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648, RelationshipId = part.GetIdOfPart(master) }),
            ids, new P.SlideSize { Cx = width, Cy = height, Type = P.SlideSizeValues.Custom },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 });
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[i];
            var slide = part.AddNewPart<SlidePart>();
            slide.AddPart(layout);
            var tree = EmptyShapeTree();
            double scale = Math.Min(width / page.Width, height / page.Height);
            double dx = (width - page.Width * scale) / 2, dy = (height - page.Height * scale) / 2;
            uint id = 2;
            if (page.BackgroundImage is not null)
            {
                var picture = slide.AddImagePart(ImagePartType.Png);
                using (var stream = File.OpenRead(page.BackgroundImage)) picture.FeedData(stream);
                tree.Append(new P.Picture(
                    new P.NonVisualPictureProperties(new P.NonVisualDrawingProperties { Id = id++, Name = "PDF 页面图形" },
                        new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }), new P.ApplicationNonVisualDrawingProperties()),
                    new P.BlipFill(new A.Blip { Embed = slide.GetIdOfPart(picture) }, new A.Stretch(new A.FillRectangle())),
                    new P.ShapeProperties(new A.Transform2D(new A.Offset { X = (long)dx, Y = (long)dy },
                            new A.Extents { Cx = (long)(page.Width * scale), Cy = (long)(page.Height * scale) }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })));
            }
            foreach (var text in page.Texts)
            {
                ct.ThrowIfCancellationRequested();
                var properties = new A.RunProperties { FontSize = (int)Math.Clamp(Math.Round(text.FontSize * scale / 127), 100, 40000),
                    Bold = text.Bold, Italic = text.Italic, Dirty = false };
                properties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = text.ColorHex }),
                    new A.LatinFont { Typeface = text.FontFamily }, new A.EastAsianFont { Typeface = text.FontFamily },
                    new A.ComplexScriptFont { Typeface = text.FontFamily });
                var textBody = new P.TextBody(
                    new A.BodyProperties(new A.NoAutoFit()) { LeftInset = 0, RightInset = 0, TopInset = 0, BottomInset = 0,
                        Wrap = A.TextWrappingValues.None, Anchor = A.TextAnchoringTypeValues.Top }, new A.ListStyle(),
                    new A.Paragraph(new A.ParagraphProperties(new A.LineSpacing(new A.SpacingPoints { Val = properties.FontSize!.Value }),
                            new A.SpaceBefore(new A.SpacingPoints { Val = 0 }), new A.SpaceAfter(new A.SpacingPoints { Val = 0 }))
                        { Alignment = A.TextAlignmentTypeValues.Left },
                        new A.Run(properties, new A.Text(CleanXmlText(text.Text))), new A.EndParagraphRunProperties()));
                tree.Append(new P.Shape(new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = id++, Name = "可编辑文字" },
                        new P.NonVisualShapeDrawingProperties { TextBox = true }, new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(new A.Transform2D(
                            new A.Offset { X = (long)(dx + text.X * scale), Y = (long)(dy + text.Y * scale) },
                            new A.Extents { Cx = (long)Math.Max(12700, (text.Width + 2) * scale), Cy = (long)Math.Max(12700, text.Height * scale) })
                            { Rotation = (int)Math.Round(text.Rotation * 60000) },
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                        new A.NoFill(), new A.Outline(new A.NoFill())), textBody));
            }
            // Explicit white canvas keeps PDF colors stable with any Office theme.
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.Background(
                    new P.BackgroundProperties(new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" }), new A.EffectList())), tree),
                new P.ColorMapOverride(new A.MasterColorMapping()));
            slide.Slide.Save();
            ids.Append(new P.SlideId { Id = (uint)(256 + i), RelationshipId = part.GetIdOfPart(slide) });
        }
        theme.Theme.Save(); master.SlideMaster.Save(); layout.SlideLayout.Save(); part.Presentation.Save();
    }
}
