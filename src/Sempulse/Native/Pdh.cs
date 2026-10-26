using System.Runtime.InteropServices;

namespace Sempulse.Native;

// Thin wrapper over one long-lived PDH query. Counter paths use English names so they work on any UI language.
internal sealed class PdhQuery : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct CounterValue { public uint Status; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    struct CounterItem { public IntPtr Name; public CounterValue Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_MORE_DATA = 0x800007D2;

    IntPtr query;

    public PdhQuery()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) throw new InvalidOperationException("PDH query unavailable");
    }

    // Returns IntPtr.Zero when the counter does not exist on this machine.
    public IntPtr TryAdd(string path) =>
        PdhAddEnglishCounter(query, path, IntPtr.Zero, out IntPtr counter) == 0 ? counter : IntPtr.Zero;

    public bool Collect() => PdhCollectQueryData(query) == 0;

    // Every instance of a wildcard counter, skipping failed or negative samples.
    public Dictionary<string, double>? ReadInstances(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        uint bytes = 0, count = 0;
        uint status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref bytes, ref count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || bytes == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref bytes, ref count, buffer) != 0) return null;
            var values = new Dictionary<string, double>();
            int itemSize = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * itemSize));
                if (item.Value.Status != 0 || double.IsNaN(item.Value.Value) || item.Value.Value < 0) continue;
                values[Marshal.PtrToStringUni(item.Name) ?? ""] = item.Value.Value;
            }
            return values;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (query == IntPtr.Zero) return;
        PdhCloseQuery(query);
        query = IntPtr.Zero;
    }
}
