using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SchoolPdf.Controls;

/// <summary>激光笔轨迹层（整窗铺满、不拦输入）：只画发光轨迹，笔停下留光点，
/// 超过 HoldMs 的段落渐隐。不落墨、不进撤销栈、不写 PDF。
/// 用自绘 FrameworkElement（OnRender）而不是 InkCanvas：没有笔画集合开销，逐段按年龄算透明度。</summary>
public sealed class LaserPointerOverlay : FrameworkElement
{
    /// <summary>轨迹保留时长（ms）：超时整段消失。</summary>
    private const long HoldMs = 1100;

    /// <summary>单条轨迹最多采样点（防长时间按笔累积几何）。</summary>
    private const int MaxPointsPerTrail = 1200;

    private sealed class Trail
    {
        public readonly List<Point> Points = new();
        public readonly List<long> Times = new();
    }

    private readonly List<Trail> _trails = new();
    private readonly DispatcherTimer _timer;
    private Trail? _current;
    private bool _active;

    // 按透明度分档缓存的画笔（0=最淡 … 7=最实）
    private readonly Pen[] _glowPens = new Pen[8];
    private readonly Pen[] _corePens = new Pen[8];
    private readonly SolidColorBrush[] _dotBrushes = new SolidColorBrush[8];

    public LaserPointerOverlay()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        for (int i = 0; i < 8; i++)
        {
            double t = (i + 1) / 8.0;
            _glowPens[i] = MakePen(Color.FromArgb((byte)(70 * t), 255, 42, 64), 11);
            _corePens[i] = MakePen(Color.FromArgb((byte)(235 * t), 255, 86, 96), 3.6);
            var dot = new SolidColorBrush(Color.FromArgb((byte)(235 * t), 255, 255, 255));
            dot.Freeze();
            _dotBrushes[i] = dot;
        }
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _timer.Tick += (_, _) => Tick();
    }

    private static Pen MakePen(Color color, double width)
    {
        var pen = new Pen(new SolidColorBrush(color), width);
        pen.Freeze();
        return pen;
    }

    /// <summary>激光模式开关（关时清空轨迹并停表）。</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            if (!_active)
                Clear();
            else
                _timer.Start();
        }
    }

    public void Begin(Point point)
    {
        if (!_active)
            return;
        var trail = new Trail();
        trail.Points.Add(point);
        trail.Times.Add(Environment.TickCount64);
        _trails.Add(trail);
        _current = trail;
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void Extend(Point point)
    {
        if (!_active || _current is null)
            return;
        var trail = _current;
        if (trail.Points.Count > 0)
        {
            Point last = trail.Points[^1];
            double dx = point.X - last.X;
            double dy = point.Y - last.Y;
            if (dx * dx + dy * dy < 9)   // 位移过小不记点（3px 以下不采，省几何与绘制）
                return;
        }
        trail.Points.Add(point);
        trail.Times.Add(Environment.TickCount64);
        if (trail.Points.Count > MaxPointsPerTrail)
        {
            trail.Points.RemoveAt(0);
            trail.Times.RemoveAt(0);
        }
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    /// <summary>是否正在一条连续轨迹里（按下未抬）。</summary>
    public bool IsDrawing => _current is not null;

    public void End() => _current = null;

    public void Clear()
    {
        _trails.Clear();
        _current = null;
        if (_timer.IsEnabled && !_active)
            _timer.Stop();
        InvalidateVisual();
    }

    private void Tick()
    {
        if (!_active)
        {
            _timer.Stop();
            return;
        }
        long now = Environment.TickCount64;
        bool any = false;
        for (int i = _trails.Count - 1; i >= 0; i--)
        {
            var trail = _trails[i];
            if (trail.Times.Count > 0 && now - trail.Times[^1] > HoldMs)
                _trails.RemoveAt(i);
            else
                any = true;
        }
        if (!any && _current is null && _trails.Count == 0)
        {
            // 静默期：没轨迹就停表，等下一次落笔再启动
            _timer.Stop();
            InvalidateVisual();
            return;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_trails.Count == 0)
            return;
        long now = Environment.TickCount64;
        foreach (var trail in _trails)
        {
            int count = trail.Points.Count;
            for (int i = 1; i < count; i++)
            {
                long age = now - trail.Times[i];
                if (age >= HoldMs)
                    continue;
                double life = 1.0 - age / (double)HoldMs;
                int band = (int)Math.Clamp(life * 8 - 0.001, 0, 7);
                dc.DrawLine(_glowPens[band], trail.Points[i - 1], trail.Points[i]);
                dc.DrawLine(_corePens[band], trail.Points[i - 1], trail.Points[i]);
            }
            // 笔尖光点：最后一个点画个小圆（停下也能看见指哪儿）
            if (count > 0)
            {
                long age = now - trail.Times[count - 1];
                if (age < HoldMs)
                {
                    double life = 1.0 - age / (double)HoldMs;
                    int band = (int)Math.Clamp(life * 8 - 0.001, 0, 7);
                    dc.DrawEllipse(_dotBrushes[band], null, trail.Points[count - 1], 2.6, 2.6);
                    dc.DrawEllipse(null, _corePens[band], trail.Points[count - 1], 5.4, 5.4);
                }
            }
        }

        if (!_timer.IsEnabled && _active)
            _timer.Start();
    }
}
