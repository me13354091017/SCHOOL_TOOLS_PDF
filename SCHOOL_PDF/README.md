# SCHOOL_TOOLS_PDF

SCHOOL_TOOLS_PDF 是一个面向课堂使用的 Windows PDF 阅读与批注应用，基于 WPF、PDFium 与 WinRT 墨迹能力构建。

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
