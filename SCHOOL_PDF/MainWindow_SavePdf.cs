using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;
using SchoolPdf.ViewModels;

namespace SchoolPdf;

public partial class MainWindow
{
    /// <summary>加载页文案（MainWindow.xaml 里是固定文本“正在打开”）：保存批注复用同一个全屏加载页，
    /// 默认文案在保存期间是错的——保存开始时临时改成“正在保存”，结束（含失败）后恢复。</summary>
    private const string OpeningCaption = "正在打开";
    private const string SavingCaption = "正在保存";
    private const string SavingTitle = "正在保存 - SCHOOL_TOOLS_PDF";

    private async void SaveAnnotationsToPdf_Click(object sender, RoutedEventArgs e)
    {
        // 文案与标题在调用前就换好：SaveAnnotationsToOriginalPdfAsync 内部会立即显示加载页
        var caption = FindOpeningCaption();
        if (caption != null)
            caption.Text = SavingCaption;
        ApplySavingTitle();
        try
        {
            int savedPages = await _viewModel.SaveAnnotationsToOriginalPdfAsync();
            ShowFloatingTip(savedPages == 0
                ? "当前没有待保存的批注"
                : $"已将 {savedPages} 页批注保存到原 PDF", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            App.Log("保存批注到原 PDF", ex);
            ShowFloatingTip("保存失败，原 PDF 未更改。请查看错误日志。", InfoBarSeverity.Error);
        }
        finally
        {
            if (caption != null)
                caption.Text = OpeningCaption;
            RestoreTitleBinding();
        }
    }

    /// <summary>标题栏临时显示“正在保存”：摘掉 XAML 的 {Binding WindowTitle} 后写本地值，
    /// 否则保存期间任务栏/标题栏还是“正在打开”。恢复见 <see cref="RestoreTitleBinding"/>。</summary>
    private void ApplySavingTitle()
    {
        BindingOperations.ClearBinding(this, TitleProperty);
        Title = SavingTitle;
    }

    /// <summary>重建与 XAML 等价的标题绑定（Source 直接用 _viewModel，不依赖当时的 DataContext）。</summary>
    private void RestoreTitleBinding()
    {
        BindingOperations.SetBinding(this, TitleProperty,
            new Binding(nameof(MainViewModel.WindowTitle))
            {
                Source = _viewModel,
                Mode = BindingMode.OneWay,
            });
    }

    /// <summary>找加载页里的“正在打开”文案（XAML 未给它命名，按文本内容定位）。
    /// 找不到返回 null——将来文案若改，这里静默跳过，不影响保存流程。</summary>
    private TextBlock? FindOpeningCaption() => FindCaptionIn(this);

    private static TextBlock? FindCaptionIn(DependencyObject parent)
    {
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(parent); i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock block && block.Text == OpeningCaption)
                return block;
            if (FindCaptionIn(child) is { } nested)
                return nested;
        }
        return null;
    }
}
