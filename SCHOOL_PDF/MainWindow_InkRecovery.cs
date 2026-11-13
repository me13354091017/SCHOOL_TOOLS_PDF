using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using SchoolPdf.Controls;
using SchoolPdf.ViewModels;

namespace SchoolPdf;

/// <summary>PDF 页面墨迹自愈：双指手势会临时把各页 EditingMode 摘绑置 None（停墨），抬完所有指再由
/// PreviewTouchUp 恢复。但触摸抬指事件可能丢失（系统触摸取消、切走窗口、同一设备重复落笔），一旦丢失，
/// _pdfActiveTouchIds 残留 → RestoreSuppressedInk 的「count != 0 就返回」守卫永久阻塞，
/// 页面画布 EditingMode 永远是 None：光标仍在（Cursor 是静态 Pen），但笔和手指都写不出墨迹，直到重启。
/// 这里补三个自愈入口：新手指落下 / 触控笔落下 / 窗口失焦——处于抑制态就强制恢复并重建 EditingMode 绑定。</summary>
public partial class MainWindow
{
    private bool _inkRecoveryWired;
    private readonly Dictionary<int, TouchDevice> _recoveryDevices = new();   // 触摸 id → 设备对象（校验是否仍活跃）

    /// <summary>挂自愈入口（幂等；App 在首次 Activated 时调用一次）。</summary>
    public void EnableInkRecovery()
    {
        if (_inkRecoveryWired)
            return;
        _inkRecoveryWired = true;

        // 新手指落下：顺手清理抬指丢失留下的僵尸触点
        PagesScrollViewer.AddHandler(UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(InkRecovery_PreviewTouchDown), true);
        PagesScrollViewer.AddHandler(UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(InkRecovery_PreviewTouchUp), true);

        // 触控笔落下：双指手势不可能与笔同时进行，说明抑制态已过期
        AddHandler(UIElement.PreviewStylusDownEvent,
            new StylusDownEventHandler(InkRecovery_PreviewStylusDown), true);

        // 窗口失焦：手指抬起时事件常落到别的窗口，是抬指丢失最常见的场景
        Deactivated += (_, _) => ForceRecoverInkLock();
    }

    private void InkRecovery_PreviewTouchDown(object? sender, TouchEventArgs e)
    {
        int id = e.TouchDevice.Id;
        bool repeatedDown = _recoveryDevices.ContainsKey(id);   // 同一设备号再次落笔 = 上一次抬指丢了
        _recoveryDevices[id] = e.TouchDevice;

        // 清理已失效的触点（只碰记录过设备的 id，不敢动别处的状态）
        bool cleaned = false;
        foreach (int stale in _pdfActiveTouchIds.ToList())
        {
            if (stale == id)
                continue;
            TouchDevice? device = _recoveryDevices.TryGetValue(stale, out TouchDevice? found) ? found : null;
            if (device is { IsActive: true })
                continue;
            _pdfActiveTouchIds.Remove(stale);
            _pdfTouchPoints.Remove(stale);
            _recoveryDevices.Remove(stale);
            cleaned = true;
        }

        if (repeatedDown)
        {
            _pdfActiveTouchIds.Remove(id);
            _pdfTouchPoints.Remove(id);
            cleaned = true;
        }

        if (!cleaned && !_inkGestureSuppressed)
            return;

        // 先把计数清零交给 RestoreSuppressedInk（它要求 count == 0 才动手），再把当前这一指重新挂上
        _pdfActiveTouchIds.Clear();
        _pdfTouchPoints.Clear();
        _pdfTouchSetChanged = false;
        _pdfFirstTouchDownMs = 0;
        RestoreSuppressedInk();
        RepairLostEditingBindings();

        _pdfActiveTouchIds.Add(id);
        _pdfTouchPoints[id] = e.TouchDevice.GetTouchPoint(PagesScrollViewer).Position;
        _pdfFirstTouchDownMs = Environment.TickCount64;
        _pdfTouchSetChanged = true;
    }

    private void InkRecovery_PreviewTouchUp(object? sender, TouchEventArgs e)
    {
        _recoveryDevices.Remove(e.TouchDevice.Id);
    }

    private void InkRecovery_PreviewStylusDown(object? sender, StylusDownEventArgs e)
    {
        if (!_inkGestureSuppressed)
            return;
        // 笔落下 = 此刻用户是在写字而不是做双指手势：上一轮手势的收尾一定丢了
        _pdfActiveTouchIds.Clear();
        _pdfTouchPoints.Clear();
        _pdfTouchSetChanged = false;
        _pdfFirstTouchDownMs = 0;
        RestoreSuppressedInk();
        RepairLostEditingBindings();
    }

    /// <summary>强制解除停墨抑制并接回 EditingMode 绑定（无副作用：正常状态下直接返回）。</summary>
    private void ForceRecoverInkLock()
    {
        if (!_inkGestureSuppressed && _suppressedEditingBindings.Count == 0)
            return;
        _pdfActiveTouchIds.Clear();
        _pdfTouchPoints.Clear();
        _pdfTouchSetChanged = false;
        _pdfFirstTouchDownMs = 0;
        _pendingInkRestore = false;
        RestoreSuppressedInk();
        RepairLostEditingBindings();
    }

    /// <summary>EditingMode 绑定如果被摘掉没接回（画布停在 None、也找不到绑定表达式），按 XAML 原义重建一条等价单程绑定。</summary>
    private void RepairLostEditingBindings()
    {
        if (_viewModel.InkTool.ActiveTool != InkToolMode.Pen || _viewModel.InkTool.ShapeDrawMode != 0)
            return;   // 非画笔/形状模式本来就该是 None，别乱接
        foreach (InkCanvas canvas in _pageInkCanvases)
        {
            if (canvas is not PageInkCanvas pageCanvas)
                continue;
            if (_selfDrawBindings.ContainsKey(pageCanvas))
                continue;   // 多指书写全自绘：None 是设计如此
            if (pageCanvas.EditingMode != InkCanvasEditingMode.None)
                continue;
            if (BindingOperations.GetBindingExpression(pageCanvas, InkCanvas.EditingModeProperty) != null)
                continue;   // 绑定还在，只是求值成 None：交给正常刷新
            BindingOperations.SetBinding(pageCanvas, InkCanvas.EditingModeProperty,
                new Binding("DataContext.InkTool.EditingMode")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
                    Mode = BindingMode.OneWay
                });
        }
    }
}
