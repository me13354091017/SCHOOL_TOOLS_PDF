using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using SchoolPdf.Services;

namespace SchoolPdf.Controls;

/// <summary>PDF 页面画布：装配时间笔锋。
/// InkCanvas.DynamicRenderer 是 protected 属性，只能在子类构造函数内替换（替换会自动把自定义 DR 挂到内部 InkPresenter）。
/// 采样由 StylusPlugIn（笔线程原始包）完成，AppendSamples 内逐点拒绝帧内历史回放点。</summary>
public class PageInkCanvas : InkCanvas
{
    private readonly TimePressureDynamicRenderer _renderer;

    public PageInkCanvas()
    {
        Controller = new TimePressureController(this);
        _renderer = new TimePressureDynamicRenderer(Controller);
        DynamicRenderer = _renderer;   // InkCanvas 负责移除旧 DR、把新 DR 追加到 StylusPlugIns 末尾并挂接 RootVisual
        StylusPlugIns.Add(new TimePressurePlugIn(Controller));
        // 丢失笔捕获 = 抬笔事件可能没送达：只清"没有抬笔记录"的僵死会话（正常抬笔已记录时刻，不受影响）
        LostStylusCapture += (_, e) =>
        {
            if (e.StylusDevice is { } device)
                Controller.AbortStrokeIfPending(device.Id);
        };
        // 浮动栏防穿透兜底：落在浮动栏矩形内的墨迹（多为点按钮时原地留下的墨点）即时丢弃
        Strokes.StrokesChanged += OnStrokesChanged;
    }

    private bool _droppingBarStroke;

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_droppingBarStroke || e.Added.Count == 0)
            return;
        var bar = InkToolBar.Current;
        if (bar is null || !bar.IsBarInputRegionActive)
            return;
        foreach (var stroke in e.Added)
        {
            if (!bar.IsStrokeFullyInsideBar(stroke, this))
                continue;
            _droppingBarStroke = true;
            try { Strokes.Remove(stroke); }
            finally { _droppingBarStroke = false; }
        }
    }

    /// <summary>本页的时间笔锋控制器（由 MainWindow 在加载/卸载与选项变化时驱动）。</summary>
    public TimePressureController Controller { get; }
}