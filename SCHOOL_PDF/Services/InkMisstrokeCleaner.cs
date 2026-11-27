using System;
using System.Collections.Generic;
using System.Windows.Ink;

namespace SchoolPdf.Services;

/// <summary>误迹查杀（DDB：Draw Dot Blocker）——扫描墨迹里的误触残留并删除。
/// 典型来源：点浮动栏按钮时被提升成笔事件、手掌擦边、笔尖悬停误落。
/// 判定只看"小"和"点少"，不碰正常书写：正常一笔长度/包围盒都远超阈值。
/// 阈值用页面坐标（DIP ÷ 页面缩放），所以缩放变化不影响判定尺度。</summary>
public static class InkMisstrokeCleaner
{
    /// <summary>单点/抖动阈值（页面坐标）：包围盒对角线 ≤ 12 且采样点 ≤ 4。</summary>
    private const double DotDiagonal = 12.0;
    private const int DotPointLimit = 4;

    /// <summary>短划痕阈值：包围盒对角线 ≤ 6 且采样点 ≤ 12。</summary>
    private const double ScratchDiagonal = 6.0;
    private const int ScratchPointLimit = 12;

    /// <summary>判定是否为误迹（不修改任何数据）。</summary>
    public static bool LooksLikeMisstroke(Stroke stroke)
    {
        var bounds = stroke.GetBounds();
        double diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        int count = stroke.StylusPoints.Count;
        if (count <= DotPointLimit && diagonal <= DotDiagonal)
            return true;
        return count <= ScratchPointLimit && diagonal <= ScratchDiagonal;
    }

    /// <summary>清理一页墨迹，返回删除条数。extraRemove 可附加额外判定（例如整条落在浮动栏内）。</summary>
    public static int CleanPage(StrokeCollection strokes, Func<Stroke, bool>? extraRemove = null)
    {
        var victims = new List<Stroke>();
        foreach (var stroke in strokes)
        {
            if (LooksLikeMisstroke(stroke) || (extraRemove?.Invoke(stroke) ?? false))
                victims.Add(stroke);
        }
        foreach (var stroke in victims)
            strokes.Remove(stroke);
        return victims.Count;
    }
}
