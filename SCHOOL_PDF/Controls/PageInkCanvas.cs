using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using SchoolPdf.Services;

namespace SchoolPdf.Controls;

/// <summary>PDF 页面画布：装配时间笔锋。
/// InkCanvas.DynamicRenderer 是 protected 属性，只能在子类构造函数内替换（替换会自动把自定义 DR 挂到内部 InkPresenter）。
/// 采样由 StylusPlugIn（笔线程原始包）完成，AppendSamples 内逐点拒绝帧内历史回放点。</summary>
public class PageInkCanvas : InkCanvas
{
    /// <summary>浮动栏防穿透：笔落在栏体上却被打到本画布时，吞掉输入并让本笔不落湿墨。
    /// 正常情况下栏体自己会命中，本兜底只在命中失效（缩放/变换后错位）时生效。</summary>
    private bool TryBlockBarPenInput(Point point)
    {
        var bar = InkToolBar.Current;
        if (bar is null || !bar.IsBarInputRegionActive || !bar.IsPointInBarInputRegion(this, point))
            return false;
        Controller.BlockStrokeForBar();
        return true;
    }

    protected override void OnPreviewStylusDown(StylusDownEventArgs e)
    {
        if (TryBlockBarPenInput(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewStylusDown(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (TryBlockBarPenInput(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    /// <summary>智能擦总开关（墨迹栏橡皮菜单设置；仅面积擦生效）。</summary>
    public static bool SmartEraseEnabled { get; set; } = true;

    /// <summary>离视口超过几个屏高就挂起绘制（块级懒加载缓冲）。</summary>
    private const double LazyBufferScreens = 2.0;

    private readonly TimePressureDynamicRenderer _renderer;
    private ScrollViewer? _hostScroller;
    private bool _suspended;
    private bool _mutating;

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
        // 浮动栏防穿透兜底 + 智能擦（同一条 StrokesChanged 通道，按增删形状分派）
        Strokes.StrokesChanged += OnStrokesChanged;
        // 块级懒加载：离视口足够远的页面挂起绘制（裁剪为空），滚回来立即恢复
        Loaded += OnLazyLoaded;
        Unloaded += OnLazyUnloaded;
    }

    // ══════════ 块级懒加载（页级挂起）══════════

    private void OnLazyLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostScroller != null)
            return;
        _hostScroller = FindHostScroller(this);
        if (_hostScroller == null)
            return;
        _hostScroller.ScrollChanged += HostScroller_ScrollChanged;
        _hostScroller.SizeChanged += HostScroller_SizeChanged;
        UpdateViewportState();
    }

    private void OnLazyUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostScroller == null)
            return;
        _hostScroller.ScrollChanged -= HostScroller_ScrollChanged;
        _hostScroller.SizeChanged -= HostScroller_SizeChanged;
        _hostScroller = null;
        ResetSuspension();
    }

    private void HostScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateViewportState();

    private void HostScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewportState();

    private static ScrollViewer? FindHostScroller(DependencyObject start)
    {
        var current = start;
        for (int depth = 0; depth < 12; depth++)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is null)
                return null;
            if (current is ScrollViewer viewer)
                return viewer;
        }
        return null;
    }

    /// <summary>按自身在滚动视口中的位置决定"挂起/恢复"。挂起用空裁剪实现：元素位置与布局不变，
    /// 只是整棵墨迹子树不参与绘制（空 Clip = WPF 强裁剪），比 Collapsed 安全（不破坏定位换算）。</summary>
    private void UpdateViewportState()
    {
        if (_hostScroller == null || ActualHeight <= 0)
            return;
        double viewport = _hostScroller.ViewportHeight;
        if (viewport <= 0)
            return;
        Point topLeft;
        try
        {
            topLeft = TranslatePoint(new Point(0, 0), _hostScroller);
        }
        catch
        {
            return;
        }
        double top = topLeft.Y;
        double bottom = top + ActualHeight;
        bool nearViewport = bottom > -LazyBufferScreens * viewport
                            && top < (1 + LazyBufferScreens) * viewport;
        if (nearViewport == !_suspended)
            return;
        _suspended = !nearViewport;
        if (_suspended)
            Clip = new RectangleGeometry(new Rect(0, 0, 0, 0));
        else
            Clip = null;
    }

    private void ResetSuspension()
    {
        _suspended = false;
        Clip = null;
    }

    // ══════════ 墨迹变更：浮动栏防穿透 + 智能擦 ══════════

    private void OnStrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (_mutating)
            return;
        if (e.Added.Count == 0 && e.Removed.Count == 0)
            return;

        // ① 新墨迹：丢弃整条落在浮动栏矩形内的（多为点按钮时原地留下的墨点）
        if (e.Added.Count > 0 && e.Removed.Count == 0)
        {
            var bar = InkToolBar.Current;
            if (bar is null || !bar.IsBarInputRegionActive || !bar.IsPointerOverBarBody)
                return;
            foreach (var stroke in e.Added)
            {
                if (!bar.IsStrokeFullyInsideBar(stroke, this))
                    continue;
                _mutating = true;
                try { Strokes.Remove(stroke); }
                finally { _mutating = false; }
            }
            return;
        }

        // ② 智能擦：面积擦把一条笔画擦断后，若残段总长只剩一点点，直接整笔清除，不留碎渣
        if (!SmartEraseEnabled || e.Removed.Count != 1 || e.Added.Count == 0)
            return;
        double before = StrokeLength(e.Removed[0]);
        double after = 0;
        foreach (var piece in e.Added)
            after += StrokeLength(piece);
        bool wipe = before < 10 || after < before * 0.32;
        if (!wipe)
            return;
        _mutating = true;
        try
        {
            foreach (var piece in e.Added)
                Strokes.Remove(piece);
        }
        finally
        {
            _mutating = false;
        }
    }

    /// <summary>折线长度（按采样点累加，足够判定"擦掉大半"）。</summary>
    private static double StrokeLength(Stroke stroke)
    {
        StylusPointCollection points = stroke.StylusPoints;
        double length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double dx = points[i].X - points[i - 1].X;
            double dy = points[i].Y - points[i - 1].Y;
            length += Math.Sqrt(dx * dx + dy * dy);
        }
        return length;
    }

    /// <summary>本页的时间笔锋控制器（由 MainWindow 在加载/卸载与选项变化时驱动）。</summary>
    public TimePressureController Controller { get; }
}
