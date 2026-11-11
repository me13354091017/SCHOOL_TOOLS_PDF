# SCHOOL_TOOLS_PDF

SCHOOL_TOOLS_PDF 是一款面向课堂讲授与教学资料批注的 Windows PDF 应用。它把 PDF 阅读、触控书写和页面标注整合在同一工作流中，让讲解时的重点圈画、公式推演和图形示意无需切换到另一款白板工具。

应用基于 WPF 与 PDFium 构建，并使用 WinRT 墨迹能力处理手写输入。无论是翻页讲解、用荧光笔标记重点，还是用直线、箭头等图形梳理思路，都可以直接在 PDF 页面上完成；笔迹支持撤销、重做与持久化，方便继续授课或复习整理。

## 功能

- 打开 PDF、查看页面缩略图、缩放和翻页
- 使用手写笔、橡皮和荧光笔进行页面批注
- 使用直线工具绘制下划线式标记
- 撤销、重做、保存批注和打印
- 支持触控输入、墨迹持久化及深浅主题

## 运行

需要 Windows 10 19041 或更高版本，以及 .NET 10 Desktop Runtime。

使用 .NET SDK 构建和运行：

```powershell
dotnet run --project SCHOOL_TOOLS_PDF.csproj
```

## 来源与许可

本项目从 [Eason-2012/InkCanvasForPDF](https://github.com/Eason-2012/InkCanvasForPDF) 派生，保留其 GPL-3.0 许可证及原始 `LICENSE` 文件。修改和再分发请遵循 GPL-3.0。
