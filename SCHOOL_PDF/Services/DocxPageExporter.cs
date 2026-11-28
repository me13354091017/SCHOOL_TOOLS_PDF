using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SchoolPdf.Models;
using SchoolPdf.Services;

namespace SchoolPdf.Services;

/// <summary>把当前 PDF 每页（含批注）按 150dpi 烘成图片，写成一个 .docx（Word 能直接打开、能再转 PDF）。
/// 不依赖 Office/OpenXML SDK：直接拼最小 OOXML 包（document.xml + media/*.png + rels）。
/// WPF 的 RenderTargetBitmap 需要 STA，整段导出跑在独立 STA 线程，不冻结 UI、不与墨迹栏抢帧。</summary>
public static class DocxPageExporter
{
    /// <summary>导出像素密度（150dpi ≈ 打印清晰、文件不至于过大）。</summary>
    private const double Dpi = 150;

    /// <summary>图片在 Word 页面里的最大显示宽度（英寸），留出 1 英寸页边距。</summary>
    private const double MaxDisplayWidthInch = 6.2;

    private const long EmuPerInch = 914400L;

    /// <summary>导出整份文档。返回页数；失败抛异常由调用方提示。</summary>
    public static Task<int> ExportAsync(IReadOnlyList<PageModel> pages, PdfRenderService pdf, string path)
    {
        return Task.Run(() =>
        {
            int result = 0;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { result = ExportCore(pages, pdf, path); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null)
                throw new InvalidOperationException(error.Message, error);
            return result;
        });
    }

    private static int ExportCore(IReadOnlyList<PageModel> pages, PdfRenderService pdf, string path)
    {
        var images = new List<(byte[] Png, int Width, int Height)>();
        foreach (var page in pages)
        {
            int width = (int)Math.Round(page.ContentWidth / 72.0 * Dpi);
            int height = (int)Math.Round(page.ContentHeight / 72.0 * Dpi);
            if (width < 1 || height < 1)
                continue;
            // 单页像素上限 2600 宽，防超大页把文件顶到几百 MB
            if (width > 2600)
            {
                double k = 2600.0 / width;
                width = 2600;
                height = Math.Max(1, (int)Math.Round(height * k));
            }

            byte[]? pagePixels = pdf.RenderPagePixels(page.PageIndex, width, height);
            if (pagePixels == null)
                continue;
            int stride = width * 4;
            if (pagePixels.Length < stride * height)
                continue;

            // 叠墨迹：Pbgra32 预乘叠加（dst = src + dst × (1-α)）
            var ink = PdfRenderService.RenderInkOverlayAt(page, width, height);
            if (page.Strokes.Count > 0 && ink.Pixels.Length >= stride * height)
            {
                var src = ink.Pixels;
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int i = row + x * 4;
                        int a = src[i + 3];
                        if (a == 0)
                            continue;
                        int rest = 255 - a;
                        pagePixels[i] = (byte)Math.Min(255, src[i] + (pagePixels[i] * rest + 127) / 255);
                        pagePixels[i + 1] = (byte)Math.Min(255, src[i + 1] + (pagePixels[i + 1] * rest + 127) / 255);
                        pagePixels[i + 2] = (byte)Math.Min(255, src[i + 2] + (pagePixels[i + 2] * rest + 127) / 255);
                        pagePixels[i + 3] = 255;
                    }
                }
            }
            // 白底兜底：PDFium 输出的 alpha 理论恒 255，个别页可能是 0，统一置不透明
            for (int i = 3; i < stride * height; i += 4)
                pagePixels[i] = 255;

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pagePixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            images.Add((ms.ToArray(), width, height));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("没有可导出的页面。");

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var zip = new ZipArchive(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None), ZipArchiveMode.Create);
        WriteText(zip, "[Content_Types].xml", ContentTypes);
        WriteText(zip, "_rels/.rels", RootRels);
        WriteText(zip, "word/_rels/document.xml.rels", BuildDocumentRels(images.Count));
        WriteText(zip, "word/document.xml", BuildDocument(images));
        for (int i = 0; i < images.Count; i++)
            WriteBytes(zip, $"word/media/page{i + 1}.png", images[i].Png);
        return images.Count;
    }

    private static string BuildDocument(List<(byte[] Png, int Width, int Height)> images)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"")
          .Append(" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"")
          .Append(" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"")
          .Append(" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"")
          .Append(" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
          .Append("<w:body>");
        for (int i = 0; i < images.Count; i++)
        {
            var (_, width, height) = images[i];
            double displayInch = Math.Min(MaxDisplayWidthInch, width / Dpi);
            long cx = (long)Math.Round(displayInch * EmuPerInch);
            long cy = (long)Math.Round(displayInch * height / width * EmuPerInch);
            int id = i + 1;
            sb.Append("<w:p><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">")
              .Append("<wp:extent cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/>")
              .Append("<wp:docPr id=\"").Append(id).Append("\" name=\"Page").Append(id).Append("\"/>")
              .Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
              .Append("<pic:pic><pic:nvPicPr><pic:cNvPr id=\"").Append(id).Append("\" name=\"page").Append(id)
              .Append(".png\"/><pic:cNvPicPr/></pic:nvPicPr>")
              .Append("<pic:blipFill><a:blip r:embed=\"rIdImg").Append(id).Append("\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>")
              .Append("<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy)
              .Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>")
              .Append("</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
            if (i != images.Count - 1)
                sb.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
        }
        sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>")
          .Append("<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>")
          .Append("</w:sectPr></w:body></w:document>");
        return sb.ToString();
    }

    private static string BuildDocumentRels(int imageCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 1; i <= imageCount; i++)
            sb.Append("<Relationship Id=\"rIdImg").Append(i)
              .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/page")
              .Append(i).Append(".png\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteBytes(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";
}
