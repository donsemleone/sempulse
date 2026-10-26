using System.Globalization;
using System.Runtime.InteropServices;

namespace Sempulse.Services;

// Pure helpers for the GPU counters, kept separate so they can be unit tested.
public static class GpuCounters
{
    // Sums every process using the same engine (the "_luid_..." suffix) and returns the busiest engine.
    public static double? BusiestEngine(IReadOnlyDictionary<string, double>? instances)
    {
        if (instances is null) return null;
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in instances)
        {
            int luid = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0) continue;
            string engine = name[luid..];
            engines[engine] = engines.GetValueOrDefault(engine) + value;
        }
        return engines.Count == 0 ? null : Math.Min(100, engines.Values.Max());
    }

    // Picks the adapter with dedicated VRAM that has the most memory in use. Totals are keyed by "luid_0x..._0x...".
    public static (ulong Used, ulong Total)? BusiestVram(IReadOnlyDictionary<string, double>? usage, IReadOnlyDictionary<string, ulong> totals)
    {
        if (usage is null) return null;
        string? bestLuid = null;
        double bestUsed = -1;
        foreach (var (name, used) in usage)
        {
            int phys = name.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            string luid = (phys < 0 ? name : name[..phys]).ToLowerInvariant();
            if (!totals.TryGetValue(luid, out ulong total) || total == 0) continue; // Software or integrated adapters.
            if (used > bestUsed) { bestUsed = used; bestLuid = luid; }
        }
        return bestLuid is null ? null : ((ulong)bestUsed, totals[bestLuid]);
    }

    public static string LuidKey(int high, uint low) =>
        string.Format(CultureInfo.InvariantCulture, "luid_0x{0:x8}_0x{1:x8}", high, low);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Vtable order matters: IDXGIObject slots come first, then IDXGIFactory, then IDXGIFactory1.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    public static Dictionary<string, ulong> ReadDedicatedMemoryTotals()
    {
        var totals = new Dictionary<string, ulong>();
        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out object factoryObject) != 0) return totals;
            var factory = (IDXGIFactory1)factoryObject;
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out IDXGIAdapter1 adapter) != 0) break;
                if (adapter.GetDesc1(out AdapterDesc1 desc) == 0)
                    totals[LuidKey(desc.LuidHigh, desc.LuidLow)] = desc.DedicatedVideoMemory.ToUInt64();
                Marshal.ReleaseComObject(adapter);
            }
            Marshal.ReleaseComObject(factory);
        }
        catch (Exception) { /* No DXGI: VRAM shows as unavailable. */ }
        return totals;
    }
}
