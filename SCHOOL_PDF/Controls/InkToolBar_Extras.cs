using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using SchoolPdf.Services;
using SchoolPdf.ViewModels;

namespace SchoolPdf.Controls;

/// <summary>墨迹栏 0.1.23 增量：激光笔、UInk 导入导出、误迹查杀（DDB）、智能擦开关、文件菜单。
/// 与主文件同 partial：只碰新控件，不改原有工具切换逻辑。</summary>
public partial class InkToolBar
{
    private bool _extrasInited;
    private bool _laserHooked;
    private bool _laserModeSubscribed;
    private Window? _laserWindow;
    private bool _moreOutsideHooked;
    private long _lastStylusTick;

    /// <summary>构造器调用：文件菜单定位/动画/关闭回调。</summary>
    private void InitExtras()
    {
        if (_extrasInited)
            return;
        _extrasInited = true;

        MorePalette.CustomPopupPlacementCallback =
            (popupSize, targetSize, offset) => new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                    PopupPrimaryAxis.Vertical)
            };
        MorePalette.Opened += (_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => PopupFlyAnimator.FlyIn(MorePalette, MorePaletteRoot)));
        MorePalette.Closed += (_, _) =>
        {
            UnhookMoreOutsideClose();
            UpdateBoardToolState(Vm?.InkTool.ActiveTool switch
            {
                InkToolMode.Pen => "pen",
                InkToolMode.Eraser => "eraser",
                InkToolMode.Select => "select",
                InkToolMode.Laser => "laser",
                _ => "mouse"
            });
        };
    }

    /// <summary>Loaded 调用：激光层状态与窗口级取点挂接。
    /// 只在激光模式开启期间订阅窗口级笔/鼠标事件——平时书写不经过激光处理器，笔迹延迟更低。</summary>
    private void InitLaserHost()
    {
        LaserLayer.IsActive = Vm?.InkTool.IsLaserOn ?? false;
        if (!_laserModeSubscribed && Vm is { } vm)
        {
            _laserModeSubscribed = true;
            vm.InkTool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(InkToolViewModel.IsLaserOn))
                    return;
                LaserLayer.IsActive = vm.InkTool.IsLaserOn;
                ApplyLaserHooks(LaserLayer.IsActive);
            };
        }
        if (Window.GetWindow(this) is { } window && _laserWindow != window)
        {
            _laserWindow = window;
            Unloaded += (_, _) => DetachLaserHost(window);
        }
        ApplyLaserHooks(LaserLayer.IsActive);
    }

    /// <summary>按需挂/摘窗口级激光取点处理器（幂等）。</summary>
    private void ApplyLaserHooks(bool active)
    {
        if (_laserWindow is not { } window)
            return;
        if (active && !_laserHooked)
        {
            window.PreviewMouseDown += LaserWindowMouseDown;
            window.PreviewMouseMove += LaserWindowMouseMove;
            window.PreviewMouseUp += LaserWindowMouseUp;
            window.PreviewStylusDown += LaserWindowStylusDown;
            window.PreviewStylusMove += LaserWindowStylusMove;
            window.PreviewStylusUp += LaserWindowStylusUp;
            _laserHooked = true;
        }
        else if (!active && _laserHooked)
        {
            UnhookLaserWindow(window);
        }
    }

    private void UnhookLaserWindow(Window window)
    {
        window.PreviewMouseDown -= LaserWindowMouseDown;
        window.PreviewMouseMove -= LaserWindowMouseMove;
        window.PreviewMouseUp -= LaserWindowMouseUp;
        window.PreviewStylusDown -= LaserWindowStylusDown;
        window.PreviewStylusMove -= LaserWindowStylusMove;
        window.PreviewStylusUp -= LaserWindowStylusUp;
        _laserHooked = false;
    }

    private void DetachLaserHost(Window window)
    {
        if (_laserHooked)
            UnhookLaserWindow(window);
        if (_laserWindow == window)
            _laserWindow = null;
        LaserLayer.IsActive = false;
    }

    // ────────── 激光笔（只画轨迹，不落墨）──────────

    private bool LaserInterceptable(Point pointInLayer)
    {
        if (Vm?.InkTool.IsLaserOn != true)
            return false;
        // 点在墨迹栏本体上不画（那是按钮操作）
        var bar = BorderFloatingBarMainControls;
        if (bar is { IsVisible: true } && bar.ActualWidth > 4)
        {
            try
            {
                var p = LaserLayer.TranslatePoint(pointInLayer, bar);
                if (new Rect(0, 0, bar.ActualWidth, bar.ActualHeight).Contains(p))
                    return false;
            }
            catch
            {
                // 变换失败按"可画"处理
            }
        }
        return true;
    }

    private void LaserWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice != null)   // 笔/触摸会另走 Stylus 通道，避免双份
            return;
        if (Environment.TickCount64 - _lastStylusTick < 140)
            return;
        var point = e.GetPosition(LaserLayer);
        if (!LaserInterceptable(point))
            return;
        LaserLayer.Begin(point);
    }

    private void LaserWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice != null || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (Environment.TickCount64 - _lastStylusTick < 140)
            return;
        if (Vm?.InkTool.IsLaserOn != true || !LaserLayer.IsDrawing)
            return;
        LaserLayer.Extend(e.GetPosition(LaserLayer));
    }

    private void LaserWindowMouseUp(object sender, MouseButtonEventArgs e) => LaserLayer.End();

    private void LaserWindowStylusDown(object sender, StylusEventArgs e)
    {
        _lastStylusTick = Environment.TickCount64;
        var point = e.GetPosition(LaserLayer);
        if (!LaserInterceptable(point))
            return;
        LaserLayer.Begin(point);
    }

    private void LaserWindowStylusMove(object sender, StylusEventArgs e)
    {
        _lastStylusTick = Environment.TickCount64;
        if (Vm?.InkTool.IsLaserOn != true)
            return;
        LaserLayer.Extend(e.GetPosition(LaserLayer));
    }

    private void LaserWindowStylusUp(object sender, StylusEventArgs e)
    {
        _lastStylusTick = Environment.TickCount64;
        LaserLayer.End();
    }

    /// <summary>激光按钮：进入/退出激光模式（再点一次回到浏览，不弹任何菜单）。</summary>
    private void LaserIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (vm.InkTool.ActiveTool == InkToolMode.Laser)
        {
            vm.InkTool.SetActiveTool(InkToolMode.None);
            LaserLayer.IsActive = false;
            ApplyLaserHooks(false);
            LaserLayer.Clear();
            UpdateBoardToolState("mouse");
            return;
        }
        vm.InkTool.SetActiveTool(InkToolMode.Laser);
        LaserLayer.IsActive = true;
        ApplyLaserHooks(true);
        UpdateBoardToolState("laser");
    }

    // ────────── 文件菜单（UInk 导入导出 + 误迹查杀）──────────

    private void MoreIcon_Click(object sender, RoutedEventArgs e)
    {
        if (MorePalette.IsOpen)
        {
            CloseMorePalette();
            return;
        }
        PopupFlyAnimator.EnsureMeasured(MorePaletteRoot);
        MorePaletteRoot.RenderTransform = new TranslateTransform(0, MorePaletteRoot.ActualHeight);
        MorePaletteRoot.RenderTransformOrigin = new Point(0.5, 1);
        MorePalette.IsOpen = true;
        UpdateBoardToolState("more");
        HookMoreOutsideClose();
    }

    private void CloseMorePalette()
    {
        PopupFlyAnimator.FlyOut(MorePalette, MorePaletteRoot);
        UpdateBoardToolState(Vm?.InkTool.ActiveTool switch
        {
            InkToolMode.Pen => "pen",
            InkToolMode.Eraser => "eraser",
            InkToolMode.Select => "select",
            InkToolMode.Laser => "laser",
            _ => "mouse"
        });
    }

    private void HookMoreOutsideClose()
    {
        if (_moreOutsideHooked)
            return;
        if (Window.GetWindow(this) is not { } window)
            return;
        _moreOutsideHooked = true;
        window.AddHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)MoreOutsideDown, true);
        window.AddHandler(UIElement.PreviewTouchDownEvent,
            (EventHandler<TouchEventArgs>)MoreOutsideTouch, true);
    }

    private void UnhookMoreOutsideClose()
    {
        if (!_moreOutsideHooked)
            return;
        if (Window.GetWindow(this) is { } window)
        {
            window.RemoveHandler(UIElement.PreviewMouseDownEvent, (MouseButtonEventHandler)MoreOutsideDown);
            window.RemoveHandler(UIElement.PreviewTouchDownEvent,
                (EventHandler<TouchEventArgs>)MoreOutsideTouch);
        }
        _moreOutsideHooked = false;
    }

    private void MoreOutsideDown(object sender, MouseButtonEventArgs e)
    {
        if (!MorePalette.IsOpen)
            return;
        if (IsDescendantOf(e.OriginalSource as DependencyObject, MorePaletteRoot)
            || IsDescendantOf(e.OriginalSource as DependencyObject, BtnMore))
            return;
        CloseMorePalette();
        if (e.StylusDevice != null)
            e.Handled = true;   // 只收菜单，别让这笔继续落到页面
    }

    private void MoreOutsideTouch(object? sender, TouchEventArgs e)
    {
        if (!MorePalette.IsOpen)
            return;
        if (IsDescendantOf(e.OriginalSource as DependencyObject, MorePaletteRoot)
            || IsDescendantOf(e.OriginalSource as DependencyObject, BtnMore))
            return;
        CloseMorePalette();
        e.Handled = true;
    }

    private void MoreRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border row)
            row.Background = new SolidColorBrush(ThemeService.IsDark
                ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
    }

    private void MoreRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border row)
            row.Background = Brushes.Transparent;
    }

    /// <summary>导出 UInk：整份文档的墨迹写成一个 .uink（JSON）。</summary>
    private void MoreRowExport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseMorePalette();
        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            ShowHint("还没打开 PDF。", "导出 UInk");
            return;
        }
        int strokeCount = 0;
        foreach (var page in vm.Pages)
            strokeCount += page.Strokes.Count;
        if (strokeCount == 0)
        {
            ShowHint("这份文档还没有批注。", "导出 UInk");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "UInk 墨迹 (*.uink)|*.uink|JSON 文件 (*.json)|*.json",
            Title = "导出 UInk 墨迹",
            DefaultExt = UInkSerializer.FileExtension,
            FileName = UInkSerializer.SuggestFileName(vm.PdfName)
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllBytes(dialog.FileName, UInkSerializer.Build(vm.Pages, vm.PdfName));
                ShowHint($"已导出 {strokeCount} 笔墨迹（{UInkSerializer.WriteLevelText}，Inkeys / ICC-CE 可直接打开）。", "导出 UInk");
            }
            catch (Exception ex)
            {
                ShowHint("导出失败：" + ex.Message, "导出 UInk");
            }
        }
    }

    /// <summary>导入 UInk：按页号把墨迹写回本文档（同页原有墨迹被替换）。</summary>
    private void MoreRowImport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseMorePalette();
        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            ShowHint("先打开 PDF，再导入墨迹。", "导入 UInk");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Filter = "UInk 墨迹 (*.uink)|*.uink|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "导入 UInk 墨迹"
        };
        if (dialog.ShowDialog() != true)
            return;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowHint("读不出这个文件：" + ex.Message, "导入 UInk");
            return;
        }
        var result = UInkSerializer.Apply(data, vm.Pages);
        ShowHint(result.Ok
            ? $"导入完成：{result.PageCount} 页、{result.StrokeCount} 笔墨迹（{UInkSerializer.ReadLevelText}）{result.Note}"
            : "导入失败：" + result.Error, "导入 UInk");
    }

    /// <summary>误迹查杀（DDB）：只删极小孤立墨点，正常笔画一律不动。</summary>
    private void MoreRowClean_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseMorePalette();
        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            ShowHint("先打开 PDF，再查杀误迹。", "误迹查杀");
            return;
        }
        int removed = 0;
        foreach (var page in vm.Pages)
            removed += InkMisstrokeCleaner.CleanPage(page.Strokes);
        ShowHint(removed > 0
            ? $"查杀完成：清掉 {removed} 处误迹墨点。"
            : "很干净，没找到误迹墨点。", "误迹查杀");
    }

    /// <summary>智能擦开关（面积擦页签）：写 VM → 同步页面画布静态开关 → 持久化。</summary>
    private void ToggleSwitchSmartErase_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.InkTool.IsSmartEraser = ToggleSwitchSmartErase.IsOn;
        PageInkCanvas.SmartEraseEnabled = vm.InkTool.IsSmartEraser;
    }

    // ────────── 另存为（PDF / Word / UInk）──────────

    private async void MoreRowSavePdf_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseMorePalette();
        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            ShowHint("先打开 PDF，再另存。", "另存为 PDF");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            Title = "另存为 PDF（含批注）",
            DefaultExt = ".pdf",
            FileName = vm.PdfName + "-批注.pdf"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            int pages = await vm.SaveAnnotatedCopyAsync(dialog.FileName);
            ShowHint(pages == 0
                ? "当前没有批注，已原样另存。"
                : $"已另存为 PDF：{pages} 页批注已烘进新文件。", "另存为 PDF");
        }
        catch (Exception ex)
        {
            App.Log("另存为 PDF", ex);
            ShowHint("另存失败：" + ex.Message, "另存为 PDF");
        }
    }

    private async void MoreRowSaveWord_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseMorePalette();
        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            ShowHint("先打开 PDF，再另存为 Word。", "另存为 Word");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "Word 文档 (*.docx)|*.docx",
            Title = "另存为 Word",
            DefaultExt = ".docx",
            FileName = vm.PdfName + ".docx"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            int pages = await vm.ExportWordAsync(dialog.FileName);
            ShowHint($"已另存为 Word：{pages} 页（带批注，150dpi 图片）。", "另存为 Word");
        }
        catch (Exception ex)
        {
            App.Log("另存为 Word", ex);
            ShowHint("另存失败：" + ex.Message, "另存为 Word");
        }
    }

    private void MoreRowSaveUink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CloseMorePalette();
        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            ShowHint("先打开 PDF，再另存 UInk。", "另存为 UInk");
            return;
        }
        int strokeCount = 0;
        foreach (var page in vm.Pages)
            strokeCount += page.Strokes.Count;
        if (strokeCount == 0)
        {
            ShowHint("这份文档还没有批注。", "另存为 UInk");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "UInk 墨迹 (*.uink)|*.uink",
            Title = "另存为 UInk 墨迹",
            DefaultExt = UInkSerializer.FileExtension,
            FileName = UInkSerializer.SuggestFileName(vm.PdfName)
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllBytes(dialog.FileName, UInkSerializer.Build(vm.Pages, vm.PdfName));
            ShowHint($"已另存 {strokeCount} 笔墨迹（{UInkSerializer.WriteLevelText}）。", "另存为 UInk");
        }
        catch (Exception ex)
        {
            ShowHint("另存失败：" + ex.Message, "另存为 UInk");
        }
    }

    /// <summary>轻提示（浮动栏没有状态栏，用消息框统一出面）。</summary>
    private void ShowHint(string message, string title)
    {
        if (Window.GetWindow(this) is { } owner)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
