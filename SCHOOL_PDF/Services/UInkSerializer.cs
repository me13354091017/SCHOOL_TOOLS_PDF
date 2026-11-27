using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using SchoolPdf.Models;

namespace SchoolPdf.Services;

/// <summary>UInk 交换格式（本应用自定义的通用墨迹文本格式，扩展名 .uink，内容为 UTF-8 JSON）。
/// 设计目标：跨设备/跨应用搬运批注——不依赖 SQLite 墨迹库与 PDF 哈希，只带页号、页面尺寸和笔画。
/// 结构：format/version/app/doc/exportedAt + pages[{index,width,height,strokes[{color,width,height,highlighter,points[[x,y,pressure]...]}]}]。</summary>
public static class UInkSerializer
{
    public const string FormatId = "uink";
    public const string FileExtension = ".uink";
    private const int FormatVersion = 1;

    internal sealed class UInkDoc
    {
        public string format { get; set; } = FormatId;
        public int version { get; set; } = FormatVersion;
        public string app { get; set; } = "SCHOOL_TOOLS_PDF";
        public string doc { get; set; } = "";
        public string exportedAt { get; set; } = "";
        public List<UInkPage> pages { get; set; } = new();
    }

    internal sealed class UInkPage
    {
        public int index { get; set; }
        public double width { get; set; }
        public double height { get; set; }
        public List<UInkStroke> strokes { get; set; } = new();
    }

    internal sealed class UInkStroke
    {
        public string color { get; set; } = "#FF000000";
        public double width { get; set; } = 2;
        public double height { get; set; } = 2;
        public bool highlighter { get; set; }
        public List<double[]> points { get; set; } = new();
    }

    /// <summary>把整份文档的墨迹写成 .uink 文本。</summary>
    public static string Build(IEnumerable<PageModel> pages, string documentName)
    {
        var doc = new UInkDoc
        {
            doc = documentName ?? "",
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        foreach (var page in pages)
        {
            if (page.Strokes.Count == 0)
                continue;
            var outPage = new UInkPage
            {
                index = page.PageIndex,
                width = page.ContentWidth,
                height = page.ContentHeight
            };
            foreach (var stroke in page.Strokes)
            {
                var da = stroke.DrawingAttributes;
                var outStroke = new UInkStroke
                {
                    color = $"#{da.Color.A:X2}{da.Color.R:X2}{da.Color.G:X2}{da.Color.B:X2}",
                    width = da.Width,
                    height = da.Height,
                    highlighter = da.IsHighlighter
                };
                var points = stroke.StylusPoints;
                outStroke.points.Capacity = points.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    outStroke.points.Add(new[]
                    {
                        Math.Round(points[i].X, 2),
                        Math.Round(points[i].Y, 2),
                        Math.Round((double)points[i].PressureFactor, 3)
                    });
                }
                outPage.strokes.Add(outStroke);
            }
            doc.pages.Add(outPage);
        }
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public sealed class ImportResult
    {
        public int PageCount;
        public int StrokeCount;
        public string DocumentName = "";
        public string Error = "";
        public bool Ok => Error.Length == 0;
    }

    /// <summary>把 .uink 文本写回页面集合（按页号覆盖对应页的墨迹；不动 PDF 位图）。
    /// 只认结构合法的文件；解析失败返回带 Error 的结果（不抛异常、不改动任何页）。</summary>
    public static ImportResult Apply(string json, IList<PageModel> pages)
    {
        var result = new ImportResult();
        UInkDoc? doc;
        try
        {
            doc = JsonSerializer.Deserialize<UInkDoc>(json);
        }
        catch (Exception ex)
        {
            result.Error = "不是有效的 JSON：" + ex.Message;
            return result;
        }
        if (doc is null || !string.Equals(doc.format, FormatId, StringComparison.OrdinalIgnoreCase))
        {
            result.Error = "不是 UInk 文件（缺少 format=uink）。";
            return result;
        }
        result.DocumentName = doc!.doc ?? "";
        var staged = new List<(int index, StrokeCollection strokes)>();
        foreach (var page in doc.pages ?? new List<UInkPage>())
        {
            if (page.index < 0 || page.index >= pages.Count)
                continue;
            var collection = new StrokeCollection();
            foreach (var s in page.strokes ?? new List<UInkStroke>())
            {
                if (s.points is null || s.points.Count == 0)
                    continue;
                var attributes = new DrawingAttributes
                {
                    Color = ParseColor(s.color),
                    Width = Math.Max(0.5, s.width),
                    Height = Math.Max(0.5, s.height),
                    IsHighlighter = s.highlighter,
                    IgnorePressure = s.highlighter,
                    FitToCurve = true
                };
                var points = new StylusPointCollection();
                foreach (var p in s.points ?? new List<double[]>())
                {
                    if (p.Length < 2)
                        continue;
                    float pressure = p.Length > 2 ? (float)Math.Clamp(p[2], 0.0, 1.0) : 0.5f;
                    points.Add(new StylusPoint(p[0], p[1], pressure));
                }
                if (points.Count == 0)
                    continue;
                collection.Add(new Stroke(points, attributes));
            }
            if (collection.Count == 0)
                continue;
            staged.Add((page.index, collection));
        }
        foreach (var (index, strokes) in staged)
        {
            pages[index].Strokes.Clear();
            foreach (var stroke in strokes)
                pages[index].Strokes.Add(stroke);
            result.PageCount++;
            result.StrokeCount += strokes.Count;
        }
        if (result.PageCount == 0)
            result.Error = "文件里没有可落到本文档任何页的墨迹。";
        return result;
    }

    private static Color ParseColor(string text)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch
        {
            return Colors.Black;
        }
    }

    /// <summary>建议的默认文件名：<pdf 名>.uink（去掉扩展名，非法字符替换成下划线）。</summary>
    public static string SuggestFileName(string pdfPath)
    {
        string name = Path.GetFileNameWithoutExtension(pdfPath);
        if (string.IsNullOrWhiteSpace(name))
            name = "墨迹";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name + FileExtension;
    }
}
