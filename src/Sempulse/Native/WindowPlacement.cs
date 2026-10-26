using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Sempulse.Layout;

namespace Sempulse.Native;

// Physical-pixel window placement and monitor queries, independent of WPF's per-monitor DPI units.
internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    // Tool windows stay out of Alt+Tab and the taskbar.
    public static void MakeToolWindow(Window window)
    {
        IntPtr hwnd = Handle(window);
        SetWindowLong(hwnd, GWL_EXSTYLE, (GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
    }

    public static void SetBounds(Window window, PixelRect rect) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE);

    public static void SetPosition(Window window, int x, int y) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    public static void BringToTop(Window window) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static PixelRect GetBounds(Window window)
    {
        GetWindowRect(Handle(window), out RECT rect);
        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static double ScaleAt(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
        try { return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1; }
        catch (Exception) { return 1; }
    }

    static PixelRect From(System.Drawing.Rectangle rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    public static (DisplayInfo Primary, DisplayInfo? Secondary) Displays()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var primaryScreen = screens.FirstOrDefault(screen => screen.Primary) ?? screens[0];
        var secondaryScreen = screens.FirstOrDefault(screen => !screen.Primary);
        DisplayInfo Info(System.Windows.Forms.Screen screen) =>
            new(From(screen.Bounds), From(screen.WorkingArea), ScaleAt(screen.Bounds.X + 1, screen.Bounds.Y + 1));
        return (Info(primaryScreen), secondaryScreen is null ? null : Info(secondaryScreen));
    }

    public static PixelRect WorkAreaAt(PixelRect rect)
    {
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height)));
        return From(screen.WorkingArea);
    }
}
