using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sempulse.Services;

// Reports whether the top visible application on the primary display occupies the whole monitor.
// The foreground window may be on another display, so this walks the Z order instead.
public sealed class FullscreenMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const int WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    // Capture tools cover the display with a temporary selection overlay.
    static readonly string[] ScreenshotTools =
    [
        "ShareX", "Greenshot", "Lightshot", "Snipaste", "flameshot", "PicPick", "Screenpresso",
        "FSCapture", "SnagitCapture", "Snagit32", "SnippingTool", "ScreenClippingHost", "ScreenSketch",
        "PowerToys.TextExtractor", "Lightscreen", "Gyazowin", "Gyazo",
    ];

    readonly uint ownProcessId = (uint)Environment.ProcessId;
    readonly CancellationTokenSource stop = new();

    public event Action<bool>? Changed;
    public bool IsFullscreen { get; private set; }

    public void Start()
    {
        new Thread(Run) { IsBackground = true, Name = "Sempulse fullscreen", Priority = ThreadPriority.BelowNormal }.Start();
    }

    void Run()
    {
        var debounce = new FullscreenDebounce();
        while (!stop.Token.WaitHandle.WaitOne(1000))
        {
            bool current = false;
            try { current = IsPrimaryFullscreen(); }
            catch (Exception) { /* An inaccessible window must not stop monitoring. */ }
            if (debounce.Push(current) is bool changed)
            {
                IsFullscreen = changed;
                Changed?.Invoke(changed);
            }
        }
    }

    bool IsPrimaryFullscreen()
    {
        IntPtr monitor = MonitorFromPoint(new POINT(), 1);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        bool found = false, fullscreen = false;
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == ownProcessId) return true;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT)) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;

            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            string cls = className.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, Marshal.SizeOf<RECT>()) != 0 &&
                !GetWindowRect(hwnd, out bounds)) return true;
            int overlapWidth = Math.Min(bounds.Right, info.rcMonitor.Right) - Math.Max(bounds.Left, info.rcMonitor.Left);
            int overlapHeight = Math.Min(bounds.Bottom, info.rcMonitor.Bottom) - Math.Max(bounds.Top, info.rcMonitor.Top);
            if (overlapWidth < 32 || overlapHeight < 32) return true;
            if (IsScreenshotTool(processId)) return true;

            found = true;
            bool borderless = (GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == 0;
            fullscreen = IsFullscreenWindow(bounds, info.rcMonitor, info.rcWork, borderless, IsZoomed(hwnd));
            return false; // First relevant window in Z order on this display.
        }, IntPtr.Zero);
        return found && fullscreen;
    }

    public static bool IsFullscreenWindow(RECT window, RECT monitor, RECT work, bool borderless, bool maximized)
    {
        bool monitorCovered = Covers(window, monitor);
        bool workAreaCovered = borderless && Covers(window, work);
        return (monitorCovered || workAreaCovered) && (!maximized || borderless);
    }

    static bool Covers(RECT window, RECT area)
    {
        const int tolerance = 3;
        return window.Left <= area.Left + tolerance && window.Top <= area.Top + tolerance &&
               window.Right >= area.Right - tolerance && window.Bottom >= area.Bottom - tolerance;
    }

    static bool IsScreenshotTool(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return ScreenshotTools.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    public void Dispose() => stop.Cancel();
}

// A state change counts only after two identical samples in a row, so brief overlays do not flip the layout.
public sealed class FullscreenDebounce
{
    bool last, pending;
    int consecutive;

    public bool? Push(bool current)
    {
        if (current == pending) consecutive++;
        else { pending = current; consecutive = 1; }
        if (consecutive < 2 || current == last) return null;
        last = current;
        return current;
    }
}
