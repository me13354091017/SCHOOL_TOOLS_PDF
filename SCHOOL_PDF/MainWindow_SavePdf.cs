using System;
using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;

namespace SchoolPdf;

public partial class MainWindow
{
    private async void SaveAnnotationsToPdf_Click(object sender, RoutedEventArgs e)
    {
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
    }
}