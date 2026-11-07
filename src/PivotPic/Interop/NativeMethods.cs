using System.Runtime.InteropServices;

namespace PivotPic.Interop;

/// <summary>本程序用到的 Win32 / DWM 原生方法声明与常量。</summary>
internal static class NativeMethods
{
    // ---- 窗口样式 ----
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_MINIMIZEBOX = 0x00020000L;
    internal const long WS_MAXIMIZEBOX = 0x00010000L;
    internal const long WS_SYSMENU = 0x00080000L;
    internal const long WS_THICKFRAME = 0x00040000L;

    // ---- 命中测试 ----
    internal const int WM_NCHITTEST = 0x0084;
    internal const int HTCLIENT = 1;
    internal const int HTCAPTION = 2;
    internal const int HTMAXBUTTON = 9;

    // ---- 窗口消息 ----
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int WM_GETMINMAXINFO = 0x0024;
    internal const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    // ---- ShowWindow ----
    internal const int SW_RESTORE = 9;
    internal const int SW_SHOW = 5;

    // ---- DWM ----
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    internal const int DWMWCP_DEFAULT = 0;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMWCP_ROUNDSMALL = 3;
    internal const int DWMSBT_AUTO = 0;
    internal const int DWMSBT_NONE = 1;
    internal const int DWMSBT_MAINWINDOW = 2;   // Mica
    internal const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    internal const int DWMSBT_TABBEDWINDOW = 4;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    internal const int WM_NCLBUTTONUP = 0x00A2;
    internal const int WM_SYSCOMMAND = 0x0112;
    internal const int SC_MAXIMIZE = 0xF030;
    internal const int SC_RESTORE = 0xF120;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>设置窗口圆角（Windows 11 有效，旧系统静默失败）。</summary>
    internal static void TryEnableRoundedCorners(IntPtr hwnd)
    {
        var pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>设置 Mica 背景（Windows 11 22H2+ 有效，旧系统静默失败）。</summary>
    internal static void TryEnableMica(IntPtr hwnd)
    {
        var backdrop = DWMSBT_MAINWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    /// <summary>切换标题栏深色模式。</summary>
    internal static void TrySetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
