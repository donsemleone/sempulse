using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Sempulse.Services;

// Reserves a strip of the desktop work area so maximized windows stop at the bar.
// An invisible helper window owns the reservation; the panel window itself is positioned separately.
public sealed class AppBar : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;

    HwndSource? window;
    string? key;

    public bool IsRegistered => window is not null;

    // Bounds are physical pixels of the monitor; thickness is in physical pixels too.
    public void Apply(BarEdge edge, int thickness, int x, int y, int width, int height)
    {
        string next = string.Join(':', edge, thickness, x, y, width, height);
        if (next == key && window is not null) return;
        Release();
        key = next;
        window = new HwndSource(new HwndSourceParameters("Sempulse AppBar")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x80,               // WS_EX_TOOLWINDOW
            PositionX = x, PositionY = y, Width = 1, Height = 1,
        });
        var data = NewData();
        SHAppBarMessage(ABM_NEW, ref data);
        int right = x + width, bottom = y + height;
        data.uEdge = edge switch { BarEdge.Left => 0u, BarEdge.Top => 1u, BarEdge.Right => 2u, _ => 3u };
        data.rc = edge switch
        {
            BarEdge.Top => new RECT { Left = x, Top = y, Right = right, Bottom = y + thickness },
            BarEdge.Bottom => new RECT { Left = x, Top = bottom - thickness, Right = right, Bottom = bottom },
            BarEdge.Left => new RECT { Left = x, Top = y, Right = x + thickness, Bottom = bottom },
            _ => new RECT { Left = right - thickness, Top = y, Right = right, Bottom = bottom },
        };
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        SHAppBarMessage(ABM_SETPOS, ref data);
    }

    public void Release()
    {
        key = null;
        if (window is null) return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        window.Dispose();
        window = null;
    }

    APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = window!.Handle };

    public void Dispose() => Release();
}
