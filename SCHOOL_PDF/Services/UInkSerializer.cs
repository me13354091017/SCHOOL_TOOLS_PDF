using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using SchoolPdf.Models;
using SchoolPdf.Services.UInk;

namespace SchoolPdf.Services;

/// <summary>UInk 1.1（Header.version = 11）读写实现，按 https://www.inkeys.top/standard/ 的公开规范：
/// 主文件是一串连续 MessagePack 对象——Header(Array 7) → Header Extension(Map) → 每页 Canvas(Map) + 若干 Ink(Map)。
///
/// 能力声明（规范允许读取与写入分别声明等级）：
/// 写入：基础级（Pen / Highlighter、opacity、nib 笔尖、逐点 width）；
/// 读取：核心级（Ink 笔迹按点位置与宽度还原；Shape / Media 跳过；Erase Ink 按 INK-11 跳过）。
/// 曲线插值、抗锯齿由各软件自行决定，点的位置、宽度、颜色与顺序保持一致（规范"通用"要求）。</summary>
public static class UInkSerializer
{
    public const string FileExtension = ".uink";
    private const int FormatVersion = 11;

    public const string WriteLevelText = "UInk 1.1 基础级";
    public const string ReadLevelText = "UInk 1.1 核心级";

    /// <summary>把整份文档的墨迹写成 UInk 主文件字节（.uink）。空页不写，页序由 Canvas 的 pageIndex 决定。</summary>
    public static byte[] Build(IEnumerable<PageModel> pages, string documentName)
    {
        var list = new List<PageModel>();
        int maxWidth = 1, maxHeight = 1;
        int pageNum = 0;
        foreach (var page in pages)
        {
            if (page.ContentWidth > maxWidth)
                maxWidth = page.ContentWidth;
            if (page.ContentHeight > maxHeight)
                maxHeight = page.ContentHeight;
            list.Add(page);
            pageNum++;   // 空白页也计入：每页都会写出一个 Canvas（FILE-5）
        }

        var deviceGuid = Guid.NewGuid().ToString();
        var workspaceGuid = Guid.NewGuid().ToString();
        var writer = new MpWriter();

        // ── Header：固定 array(7)，字段位宽不可变（HDR-1）──
        writer.WriteArray(7);
        writer.WriteUInt16(0);                          // type
        writer.WriteUInt16((ushort)FormatVersion);      // version = 11
        writer.WriteString(Guid.NewGuid().ToString());  // guid（str8，36 字符）
        writer.WriteUInt32(1);                          // deviceNum
        writer.WriteUInt32(1);                          // workspaceNum
        writer.WriteUInt32((uint)pageNum);              // pageNum
        writer.WriteUInt64((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // ── Header Extension：一块显示区域 + 一个白板场景（HEXT-5：写了显式条目，Canvas 就必须引用 GUID）──
        writer.WriteMap(4);
        writer.WriteString("type");
        writer.WriteUInt16(1);
        writer.WriteString("name");
        writer.WriteString(string.IsNullOrWhiteSpace(documentName) ? "墨迹" : documentName);
        writer.WriteString("devices");
        writer.WriteArray(1);
        writer.WriteMap(6);
        writer.WriteString("guid");
        writer.WriteString(deviceGuid);
        writer.WriteString("deviceType");
        writer.WriteInt32(0);                           // 0 Display
        writer.WriteString("x");
        writer.WriteInt32(0);
        writer.WriteString("y");
        writer.WriteInt32(0);
        writer.WriteString("width");
        writer.WriteUInt32((uint)Math.Max(1, maxWidth));
        writer.WriteString("height");
        writer.WriteUInt32((uint)Math.Max(1, maxHeight));
        writer.WriteString("workspaces");
        writer.WriteArray(1);
        writer.WriteMap(4);
        writer.WriteString("guid");
        writer.WriteString(workspaceGuid);
        writer.WriteString("workspaceType");
        writer.WriteInt32(1);                           // 1 Whiteboard
        writer.WriteString("name");
        writer.WriteString(string.IsNullOrWhiteSpace(documentName) ? "墨迹" : documentName);
        writer.WriteString("currentPageIndex");
        writer.WriteUInt32(0);

        foreach (var page in list)
        {
            // ── Canvas：这一页在唯一 Device 上的第 0 层 ──
            writer.WriteMap(10);
            writer.WriteString("type");
            writer.WriteUInt16(2);
            writer.WriteString("workspaceGuid");
            writer.WriteString(workspaceGuid);
            writer.WriteString("deviceGuid");
            writer.WriteString(deviceGuid);
            writer.WriteString("pageGuid");
            writer.WriteString(Guid.NewGuid().ToString());
            writer.WriteString("pageIndex");
            writer.WriteUInt32((uint)page.PageIndex);
            writer.WriteString("pageNumber");
            writer.WriteUInt32((uint)(page.PageIndex + 1));
            writer.WriteString("layerIndex");
            writer.WriteUInt32(0);
            writer.WriteString("layerNumber");
            writer.WriteUInt32(0);
            writer.WriteString("background");
            writer.WriteMap(2);
            writer.WriteString("color");
            writer.WriteMap(1);
            writer.WriteString("fallback");
            writer.WriteUInt32(0xFFFFFF);
            writer.WriteString("pattern");
            writer.WriteInt32(0);
            writer.WriteString("viewport");
            writer.WriteMap(3);
            writer.WriteString("x");
            writer.WriteFloat32(0);
            writer.WriteString("y");
            writer.WriteFloat32(0);
            writer.WriteString("scale");
            writer.WriteFloat32(1);

            int contentId = 0;
            foreach (var stroke in page.Strokes)
            {
                var da = stroke.DrawingAttributes;
                var points = stroke.StylusPoints;
                if (points.Count == 0)
                    continue;
                bool highlighter = da.IsHighlighter;
                double baseHeight = highlighter
                    ? Math.Max(1.0, da.Height)
                    : Math.Max(1.0, da.Width);

                int keyCount = highlighter ? 9 : 8;
                writer.WriteMap(keyCount);
                writer.WriteString("type");
                writer.WriteUInt16(3);
                writer.WriteString("contentId");
                writer.WriteUInt32((uint)contentId);
                writer.WriteString("undoId");
                writer.WriteUInt32((uint)contentId);
                writer.WriteString("inkType");
                writer.WriteInt32(highlighter ? 2 : 1);          // 2 Highlighter / 1 Pen
                writer.WriteString("color");
                writer.WriteMap(1);
                writer.WriteString("fallback");
                writer.WriteUInt32((uint)((da.Color.R << 16) | (da.Color.G << 8) | da.Color.B));
                writer.WriteString("opacity");
                writer.WriteFloat32(highlighter ? Math.Clamp(da.Color.A / 255.0f, 0f, 1f) : 1f);
                writer.WriteString("texture");
                writer.WriteInt32(0);
                if (highlighter)
                {
                    writer.WriteString("nib");
                    writer.WriteMap(3);
                    writer.WriteString("shape");
                    writer.WriteInt32(1);                        // Rectangle
                    writer.WriteString("aspect");
                    writer.WriteFloat32(0.125f);                 // 1:8 竖直荧光笔
                    writer.WriteString("orientation");
                    writer.WriteInt32(0);                        // Fixed
                }
                writer.WriteString("points");
                writer.WriteArray(points.Count);

                double prevX = 0, prevY = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    double x = points[i].X;
                    double y = points[i].Y;
                    // 压感直接烘焙进逐点 width（INK-8：UInk 不注册压感通道，笔锋只能体现在 width）
                    float pressure = points[i].PressureFactor;
                    float width = (float)(baseHeight * (0.5 + Math.Clamp(pressure, 0f, 1f)));
                    if (width <= 0.05f)
                        width = 0.05f;

                    writer.WriteMap(3);
                    writer.WriteString("x");
                    writer.WriteFloat32(i == 0 ? (float)x : (float)(x - prevX));
                    writer.WriteString("y");
                    writer.WriteFloat32(i == 0 ? (float)y : (float)(y - prevY));
                    writer.WriteString("width");
                    writer.WriteFloat32(width);
                    prevX = x;
                    prevY = y;
                }
                contentId++;
            }
        }
        return writer.ToArray();
    }

    public sealed class ImportResult
    {
        public int PageCount;
        public int StrokeCount;
        public int ShapeCount;
        public int MediaCount;
        public int Version;
        public string Note = "";
        public string Error = "";
        public bool Ok => Error.Length == 0;
    }

    internal sealed class CanvasAcc
    {
        public int PageIndex;
        public readonly StrokeCollection Strokes = new();
    }

    /// <summary>读取 .uink 主文件，按 pageIndex 写回本文档对应页（文件中出现的页整体替换原有墨迹）。
    /// 崩溃恢复：解码失败即停，保留此前完整对象（FILE-16/17）；不认识的块跳过（TYPE-9）。</summary>
    public static ImportResult Apply(byte[] data, IList<PageModel> pages)
    {
        var result = new ImportResult();
        int pos = 0;
        object? headerObject;
        try
        {
            headerObject = MpReader.Read(data, ref pos);
        }
        catch (Exception ex)
        {
            result.Error = "文件无法解码：" + ex.Message;
            return result;
        }

        var header = MpValues.AsList(headerObject);
        if (header is null || header.Count < 7 || MpValues.AsInt(header[0]) != 0)
        {
            result.Error = "不是 UInk 文件（首块不是 Header）。";
            return result;
        }
        int version = MpValues.AsInt(header[1]) ?? -1;
        if (version < 10 || version > 19)
        {
            result.Error = $"不支持的 UInk 版本（Header.version = {version}）。";
            return result;
        }
        result.Version = version;

        var canvases = new Dictionary<string, CanvasAcc>(StringComparer.Ordinal);
        CanvasAcc? current = null;
        int fallbackIndex = 0;

        while (pos < data.Length)
        {
            object? block;
            try
            {
                block = MpReader.Read(data, ref pos);
            }
            catch
            {
                break;   // 结尾截断/中间损坏：保留已经读完的部分
            }
            var map = MpValues.AsMap(block);
            if (map is null)
                continue;
            object? typeValue = map.TryGetValue("type", out var tv) ? tv : null;
            int type = MpValues.AsInt(typeValue) ?? -1;
            switch (type)
            {
                case 2:   // Canvas
                    {
                        string guid = MpValues.AsString(map.TryGetValue("pageGuid", out var g) ? g : null) ?? "";
                        int index = MpValues.AsInt(map.TryGetValue("pageIndex", out var pi) ? pi : null) ?? fallbackIndex;
                        fallbackIndex++;
                        if (guid.Length == 0)
                            guid = "temp-" + fallbackIndex;
                        var acc = new CanvasAcc { PageIndex = index };
                        canvases[guid] = acc;   // 同键后出现者整体替代（FILE-8）
                        current = acc;
                        break;
                    }
                case 3:   // Ink
                    if (current is null)
                        break;
                    var stroke = BuildStroke(map);
                    if (stroke is not null)
                        current.Strokes.Add(stroke);
                    break;
                case 6:   // Clear：按对象流顺序作用于此前内容
                    current?.Strokes.Clear();
                    break;
                case 5:
                    result.ShapeCount++;
                    break;
                case 4:
                    result.MediaCount++;
                    break;
            }
        }

        var ordered = new List<CanvasAcc>();
        foreach (var acc in canvases.Values)
            ordered.Add(acc);
        ordered.Sort((a, b) => a.PageIndex.CompareTo(b.PageIndex));

        foreach (var acc in ordered)
        {
            if (acc.PageIndex < 0 || acc.PageIndex >= pages.Count || acc.Strokes.Count == 0)
                continue;
            pages[acc.PageIndex].Strokes.Clear();
            foreach (var stroke in acc.Strokes)
                pages[acc.PageIndex].Strokes.Add(stroke);
            result.PageCount++;
            result.StrokeCount += acc.Strokes.Count;
        }

        if (result.PageCount == 0)
            result.Error = "文件里没有可落到本文档任何页的笔迹。";
        else if (result.ShapeCount > 0 || result.MediaCount > 0)
            result.Note = $"（另有 {result.ShapeCount} 个图形、{result.MediaCount} 个媒体已跳过：本应用按核心级读取）";
        return result;
    }

    /// <summary>把一条 Ink 块还原为 WPF Stroke：点按 INK-4 逐点累加位移（float64 防误差），
    /// 宽度取整条平均作为 DrawingAttributes（WPF 不支持逐点变宽，压感用宽度相对值近似回填）。</summary>
    private static Stroke? BuildStroke(Dictionary<string, object?> map)
    {
        int inkType = MpValues.AsInt(map.TryGetValue("inkType", out var it) ? it : null) ?? 1;
        if (inkType == 0)
            return null;   // Erase Ink：未实现 Inkeys 附加级，按 INK-11 跳过

        var pointsNode = MpValues.AsList(map.TryGetValue("points", out var pn) ? pn : null);
        if (pointsNode is null || pointsNode.Count == 0)
            return null;

        int colorValue = 0;
        float opacity = 1f;
        if (MpValues.AsMap(map.TryGetValue("color", out var cn) ? cn : null) is { } colorMap)
        {
            int? fallback = MpValues.AsInt(colorMap.TryGetValue("fallback", out var fb) ? fb : null);
            if (fallback is not null)
                colorValue = fallback.Value & 0xFFFFFF;
        }
        if (MpValues.AsFloat(map.TryGetValue("opacity", out var op) ? op : null) is { } opv)
            opacity = Math.Clamp(opv, 0f, 1f);

        bool highlighter = inkType is 2 or 3;
        float aspect = 0.125f;
        if (MpValues.AsMap(map.TryGetValue("nib", out var nb) ? nb : null) is { } nibMap)
        {
            int shape = MpValues.AsInt(nibMap.TryGetValue("shape", out var sh) ? sh : null) ?? 0;
            if (shape == 1
                && MpValues.AsFloat(nibMap.TryGetValue("aspect", out var asp) ? asp : null) is { } a
                && a > 0)
                aspect = a;
        }

        var raw = new List<(double X, double Y, double W)>(pointsNode.Count);
        double x = 0, y = 0;
        double sumWidth = 0;
        double maxWidth = 0;
        foreach (var node in pointsNode)
        {
            var pm = MpValues.AsMap(node);
            if (pm is null)
                continue;
            double dx = MpValues.AsDouble(pm.TryGetValue("x", out var px) ? px : null) ?? 0;
            double dy = MpValues.AsDouble(pm.TryGetValue("y", out var py) ? py : null) ?? 0;
            double pw = MpValues.AsDouble(pm.TryGetValue("width", out var wv) ? wv : null) ?? 1;
            x += dx;
            y += dy;
            if (pw <= 0)
                pw = 1;
            raw.Add((x, y, pw));
            sumWidth += pw;
            if (pw > maxWidth)
                maxWidth = pw;
        }
        if (raw.Count == 0)
            return null;

        double meanWidth = sumWidth / raw.Count;

        var pointCollection = new StylusPointCollection();
        foreach (var (px, py, pw) in raw)
        {
            float pressure = maxWidth > 0
                ? (float)Math.Clamp(pw / maxWidth, 0.05, 1.0)
                : 0.5f;
            pointCollection.Add(new StylusPoint(px, py, pressure));
        }

        var attributes = new DrawingAttributes
        {
            Color = Color.FromRgb((byte)((colorValue >> 16) & 0xFF), (byte)((colorValue >> 8) & 0xFF), (byte)(colorValue & 0xFF)),
            FitToCurve = true
        };
        if (highlighter)
        {
            attributes.IsHighlighter = true;
            attributes.IgnorePressure = true;
            attributes.Color = Color.FromArgb(
                (byte)Math.Round(Math.Clamp(opacity, 0.02f, 1f) * 255),
                attributes.Color.R, attributes.Color.G, attributes.Color.B);
            attributes.Height = Math.Max(0.5, meanWidth);
            attributes.Width = Math.Max(0.5, meanWidth * Math.Max(0.02, aspect));
        }
        else
        {
            attributes.Width = Math.Max(0.5, meanWidth);
            attributes.Height = Math.Max(0.5, meanWidth);
        }
        return new Stroke(pointCollection, attributes);
    }

    /// <summary>建议文件名：<pdf 名>.uink。</summary>
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
