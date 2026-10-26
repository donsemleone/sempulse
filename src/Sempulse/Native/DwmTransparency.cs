using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Sempulse.Native;

// Per-pixel transparency through the desktop compositor instead of a layered window. WPF then renders
// on the GPU and animations no longer copy the whole window through system memory on every frame.
internal static class DwmTransparency
{
    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;
    const int DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    // Call from SourceInitialized on a window with AllowsTransparency = false and a transparent background.
    public static void Enable(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        // Windows 11 would otherwise round the corners and draw a thin border around the bar.
        int corner = DWMWCP_DONOTROUND, border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }
}
