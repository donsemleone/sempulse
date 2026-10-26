using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Screen = System.Windows.Forms.Screen;

namespace Sempulse.Services;

public sealed record DesktopIconResult(bool Ok, string Code, int Count, bool CanUndo);

// Changes Explorer's desktop icon positions only. It never moves files.
public sealed class DesktopIconService(string snapshotFile)
{
    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["NO_SECONDARY"] = "İkinci monitör bağlı değil.",
        ["AUTO_ARRANGE"] = "Masaüstüne sağ tıklayıp Görünüm > Simgeleri otomatik düzenle seçeneğini kapatın.",
        ["NO_SPACE"] = "İkinci monitörde tüm simgeler için yeterli boş alan yok.",
        ["RESTORE_OCCUPIED"] = "Eski simge konumlarından bazıları dolu. Geri almadan önce bu alanları boşaltın.",
        ["DISPLAY_CHANGED"] = "Ekran düzeni değişti. Eski konumları güvenle geri yüklemek için önceki ekran düzenine dönün.",
        ["SNAPSHOT_INVALID"] = "Kaydedilmiş masaüstü simgesi düzeni okunamadı.",
        ["DESKTOP_VIEW_UNAVAILABLE"] = "Windows masaüstü simgelerine erişilemedi. Explorer çalışıyor mu?",
        ["VERIFY_FAILED"] = "Simge taşıma doğrulanamadı; önceki düzen geri yüklendi.",
        ["HELPER_FAILED"] = "Masaüstü simgeleri işlemi tamamlanamadı.",
        ["NOTHING_TO_MOVE"] = "Birinci monitörde taşınacak dosya, klasör veya kısayol yok.",
    };

    const uint SIGDN_FILESYSPATH = 0x80058000;
    const uint SVSI_POSITIONITEM = 0x80;
    const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService([MarshalAs(UnmanagedType.LPStruct)] Guid service, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void _VtblGap1_12();
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryActiveShellView();
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        void _VtblGap1_3();
        IntPtr Item(int index);
        int ItemCount(uint flags);
        void _VtblGap2_3();
        void GetItemPosition(IntPtr pidl, out IconPoint position);
        void GetSpacing(ref IconPoint spacing);
        void _VtblGap3_1();
        [PreserveSig] int GetAutoArrange();
        void _VtblGap4_1();
        void SelectAndPositionItems(int count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IconPoint[] positions,
            uint flags);
    }

    [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView2
    {
        void _VtblGap1_21();
        uint SetCurrentFolderFlags(int mask, int flags);
        void _VtblGap2_4();
        IShellItem GetItem(int index, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void _VtblGap1_2();
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetDisplayName(uint nameType);
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder className, int capacity);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref IconPoint point);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref IconPoint point);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr pointer);

    sealed class Icon
    {
        public string? Path;
        public IntPtr Pidl;
        public IconPoint ViewPosition;
        public IconPoint ScreenPosition;
    }

    sealed class DesktopView : IDisposable
    {
        public required IFolderView Folder;
        public required IntPtr ListView;
        public required IconPoint Spacing;
        public List<Icon> Icons { get; } = [];

        public void Dispose()
        {
            foreach (var icon in Icons) CoTaskMemFree(icon.Pidl);
        }
    }

    readonly SemaphoreSlim busy = new(1, 1);

    public Task<DesktopIconResult> StatusAsync() => RunAsync(() => new DesktopIconResult(true, "STATUS", 0, HasUndo()));
    public Task<DesktopIconResult> MoveAsync() => RunAsync(Move);
    public Task<DesktopIconResult> UndoAsync() => RunAsync(Undo);

    // Shell COM objects need an STA thread; each call gets its own short-lived one.
    async Task<DesktopIconResult> RunAsync(Func<DesktopIconResult> work)
    {
        if (!await busy.WaitAsync(0)) return new DesktopIconResult(false, "BUSY", 0, false);
        try
        {
            var completion = new TaskCompletionSource<DesktopIconResult>();
            var thread = new Thread(() =>
            {
                try { completion.SetResult(work()); }
                catch (DesktopIconException exception) { completion.SetResult(new(false, exception.Code, 0, SafeHasUndo())); }
                catch (Exception) { completion.SetResult(new(false, "DESKTOP_VIEW_UNAVAILABLE", 0, SafeHasUndo())); }
            }) { IsBackground = true, Name = "Sempulse desktop icons" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        catch (TimeoutException) { return new DesktopIconResult(false, "HELPER_FAILED", 0, SafeHasUndo()); }
        finally { busy.Release(); }
    }

    bool HasUndo() => DesktopIconLayout.ReadSnapshot(snapshotFile).Count > 0;
    bool SafeHasUndo() { try { return HasUndo(); } catch (Exception) { return File.Exists(snapshotFile); } }

    static IntPtr FindDesktopListView()
    {
        IntPtr listView = IntPtr.Zero;
        var className = new StringBuilder(64);
        EnumWindows((top, _) =>
        {
            className.Clear();
            if (GetClassName(top, className, className.Capacity) == 0 || className.ToString() is not ("Progman" or "WorkerW")) return true;
            IntPtr defView = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;
            listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
            if (listView == IntPtr.Zero) listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            return listView == IntPtr.Zero;
        }, IntPtr.Zero);
        return listView;
    }

    static DesktopView OpenDesktop()
    {
        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero) throw new DesktopIconException("DESKTOP_VIEW_UNAVAILABLE");

        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new DesktopIconException("DESKTOP_VIEW_UNAVAILABLE");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic windows = shell.Windows();
        int hwnd = 0;
        object? dispatch = windows.FindWindowSW(Type.Missing, Type.Missing, SWC_DESKTOP, ref hwnd, SWFO_NEEDDISPATCH);
        if (dispatch is null) throw new DesktopIconException("DESKTOP_VIEW_UNAVAILABLE");
        var provider = (IServiceProvider)dispatch;
        var browser = (IShellBrowser)provider.QueryService(new Guid("4c96be40-915c-11cf-99d3-00aa004ae837"), typeof(IShellBrowser).GUID);
        object activeView = browser.QueryActiveShellView();
        var folder = (IFolderView)activeView;
        var folder2 = (IFolderView2)activeView;
        var spacing = new IconPoint(0, 0);
        folder.GetSpacing(ref spacing);
        if (spacing.X < 16 || spacing.Y < 16) throw new DesktopIconException("DESKTOP_VIEW_UNAVAILABLE");

        var desktop = new DesktopView { Folder = folder, ListView = listView, Spacing = spacing };
        int count = folder.ItemCount(0);
        for (int i = 0; i < count; i++)
        {
            IntPtr pidl = folder.Item(i);
            if (pidl == IntPtr.Zero) continue;
            folder.GetItemPosition(pidl, out IconPoint position);
            var icon = new Icon { Pidl = pidl, ViewPosition = position, ScreenPosition = position };
            desktop.Icons.Add(icon);
            if (!ClientToScreen(listView, ref icon.ScreenPosition)) throw new DesktopIconException("DESKTOP_VIEW_UNAVAILABLE");
            try
            {
                var item = folder2.GetItem(i, typeof(IShellItem).GUID);
                string itemPath = item.GetDisplayName(SIGDN_FILESYSPATH);
                if (File.Exists(itemPath) || Directory.Exists(itemPath)) icon.Path = itemPath;
                Marshal.ReleaseComObject(item);
            }
            catch (COMException) { /* Virtual desktop item, such as the Recycle Bin. */ }
        }
        return desktop;
    }

    static IconRect ToRect(System.Drawing.Rectangle rectangle) => new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    static IconPoint ToScreen(IntPtr window, IconPoint viewPosition)
    {
        var point = viewPosition;
        if (!ClientToScreen(window, ref point)) throw new DesktopIconException("DESKTOP_VIEW_UNAVAILABLE");
        return point;
    }

    DesktopIconResult Move()
    {
        var primary = Screen.PrimaryScreen;
        var secondary = Screen.AllScreens.FirstOrDefault(screen => !screen.Primary);
        if (primary is null || secondary is null) return new(false, "NO_SECONDARY", 0, HasUndo());
        var primaryBounds = ToRect(primary.Bounds);
        var secondaryBounds = ToRect(secondary.Bounds);

        using var desktop = OpenDesktop();
        if (desktop.Folder.GetAutoArrange() == 0) return new(false, "AUTO_ARRANGE", 0, HasUndo());
        var selected = new List<Icon>();
        var occupied = new List<IconPoint>();
        foreach (var icon in desktop.Icons)
        {
            if (icon.Path is not null && primaryBounds.Contains(icon.ScreenPosition)) selected.Add(icon);
            else if (secondaryBounds.Contains(icon.ScreenPosition)) occupied.Add(icon.ViewPosition);
        }
        if (selected.Count == 0) return new(true, "NOTHING_TO_MOVE", 0, HasUndo());

        var work = secondary.WorkingArea;
        var topLeft = new IconPoint(work.Left, work.Top);
        var bottomRight = new IconPoint(work.Right - desktop.Spacing.X, work.Bottom - desktop.Spacing.Y);
        ScreenToClient(desktop.ListView, ref topLeft);
        ScreenToClient(desktop.ListView, ref bottomRight);
        var sample = desktop.Icons.Count > 0 ? desktop.Icons[0].ViewPosition : new IconPoint(0, 0);
        var destinations = DesktopIconLayout.PlanDestinations(selected.Count, topLeft, bottomRight, desktop.Spacing, sample, occupied);
        if (destinations.Count != selected.Count) return new(false, "NO_SPACE", 0, HasUndo());

        var previous = DesktopIconLayout.ReadSnapshot(snapshotFile);
        var updated = previous.Clone();
        foreach (var icon in selected) updated.Add(icon.Path!, icon.ScreenPosition);
        var pidls = selected.Select(icon => icon.Pidl).ToArray();
        var originals = selected.Select(icon => icon.ViewPosition).ToArray();
        DesktopIconLayout.WriteSnapshot(snapshotFile, updated);
        try
        {
            desktop.Folder.SelectAndPositionItems(pidls.Length, pidls, destinations.ToArray(), SVSI_POSITIONITEM);
            foreach (IntPtr pidl in pidls)
            {
                desktop.Folder.GetItemPosition(pidl, out IconPoint actual);
                if (!secondaryBounds.Contains(ToScreen(desktop.ListView, actual))) throw new DesktopIconException("VERIFY_FAILED");
            }
        }
        catch (Exception)
        {
            desktop.Folder.SelectAndPositionItems(pidls.Length, pidls, originals, SVSI_POSITIONITEM);
            DesktopIconLayout.WriteSnapshot(snapshotFile, previous);
            return new(false, "VERIFY_FAILED", 0, previous.Count > 0);
        }
        return new(true, "MOVED", selected.Count, true);
    }

    DesktopIconResult Undo()
    {
        var snapshot = DesktopIconLayout.ReadSnapshot(snapshotFile);
        if (snapshot.Count == 0) return new(true, "NOTHING_TO_UNDO", 0, false);
        var primary = Screen.PrimaryScreen;
        if (primary is null) return new(false, "DISPLAY_CHANGED", 0, true);
        var primaryBounds = ToRect(primary.Bounds);

        using var desktop = OpenDesktop();
        if (desktop.Folder.GetAutoArrange() == 0) return new(false, "AUTO_ARRANGE", 0, true);
        var restoring = new List<Icon>();
        foreach (var icon in desktop.Icons)
        {
            if (icon.Path is null || !snapshot.Positions.TryGetValue(icon.Path, out var original)) continue;
            if (!primaryBounds.Contains(original)) return new(false, "DISPLAY_CHANGED", 0, true);
            restoring.Add(icon);
        }
        restoring.Sort((first, second) => snapshot.Order.IndexOf(first.Path!).CompareTo(snapshot.Order.IndexOf(second.Path!)));
        var occupied = desktop.Icons
            .Where(icon => !restoring.Contains(icon) && primaryBounds.Contains(icon.ScreenPosition))
            .Select(icon => icon.ScreenPosition).ToList();

        var work = ToRect(primary.WorkingArea);
        var targets = new IconPoint[restoring.Count];
        for (int i = 0; i < restoring.Count; i++)
        {
            var slot = DesktopIconLayout.FindRestoreSlot(snapshot.Positions[restoring[i].Path!], work, desktop.Spacing, occupied);
            if (slot is null) return new(false, "RESTORE_OCCUPIED", 0, true);
            occupied.Add(slot.Value);
            targets[i] = slot.Value;
            if (!ScreenToClient(desktop.ListView, ref targets[i])) return new(false, "DESKTOP_VIEW_UNAVAILABLE", 0, true);
        }
        var pidls = restoring.Select(icon => icon.Pidl).ToArray();
        var before = restoring.Select(icon => icon.ViewPosition).ToArray();
        try
        {
            if (pidls.Length > 0) desktop.Folder.SelectAndPositionItems(pidls.Length, pidls, targets, SVSI_POSITIONITEM);
            foreach (IntPtr pidl in pidls)
            {
                desktop.Folder.GetItemPosition(pidl, out IconPoint actual);
                if (!primaryBounds.Contains(ToScreen(desktop.ListView, actual))) throw new DesktopIconException("VERIFY_FAILED");
            }
        }
        catch (Exception)
        {
            if (pidls.Length > 0) desktop.Folder.SelectAndPositionItems(pidls.Length, pidls, before, SVSI_POSITIONITEM);
            return new(false, "VERIFY_FAILED", 0, true);
        }
        File.Delete(snapshotFile);
        return new(true, "RESTORED", restoring.Count, false);
    }
}
