using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sempulse.Native;

namespace Sempulse.Views;

// The always-on-top panel window. Its content is a PanelView; placement is done by the app in physical pixels.
internal sealed class PanelWindow : Window
{
    public event Action? DragFinished;
    public event Action? DpiChangedAfterMove;

    public bool Draggable { get; set; }

    public PanelWindow()
    {
        Title = "Sempulse";
        WindowStyle = WindowStyle.None;
        // See-through background from the desktop compositor rather than a layered window: WPF keeps rendering
        // on the GPU, so animations do not copy the whole window through system memory on every frame.
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 1;
        Height = 1;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.MakeToolWindow(this);
            DwmTransparency.Enable(this);
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Draggable || e.Handled) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        DragFinished?.Invoke();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes the window for the new DPI; the app puts the exact pixel bounds back.
        Dispatcher.BeginInvoke(() => DpiChangedAfterMove?.Invoke());
    }
}
