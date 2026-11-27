using System;
using System.Runtime.InteropServices;

namespace SchoolPdf.Controls;

/// <summary>屏幕取色用的 Win32 入口（吸管取页面任意像素，坐标意为物理像素，与 DPI 缩放无关）。</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    internal static extern uint GetPixel(IntPtr hDc, int x, int y);
}
