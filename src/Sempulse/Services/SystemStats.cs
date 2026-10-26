using System.Runtime.InteropServices;
using Sempulse.Native;

namespace Sempulse.Services;

public sealed record StatsSnapshot(
    double CpuLoad, ulong MemUsed, ulong MemTotal, double? GpuLoad, ulong? VramUsed, ulong? VramTotal, double NetRx, double NetTx)
{
    public static readonly StatsSnapshot Empty = new(0, 0, 1, null, null, null, 0, 0);
}

// Samples CPU and network every second and the GPU every two seconds on a background thread.
public sealed class SystemStats : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    readonly Thread thread;
    readonly CancellationTokenSource stop = new();
    volatile StatsSnapshot latest = StatsSnapshot.Empty;

    public SystemStats()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Sempulse stats", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public StatsSnapshot Latest => latest;

    void Run()
    {
        double cpu = 0, rx = 0, tx = 0;
        double? gpu = null;
        (ulong Used, ulong Total)? vram = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var system = new PdhQuery();
                IntPtr cpuCounter = system.TryAdd(@"\Processor Information(_Total)\% Processor Utility");
                IntPtr rxCounter = system.TryAdd(@"\Network Interface(*)\Bytes Received/sec");
                IntPtr txCounter = system.TryAdd(@"\Network Interface(*)\Bytes Sent/sec");

                PdhQuery? gpuQuery = null;
                IntPtr engineCounter = IntPtr.Zero, vramCounter = IntPtr.Zero;
                try
                {
                    gpuQuery = new PdhQuery();
                    engineCounter = gpuQuery.TryAdd(@"\GPU Engine(*)\Utilization Percentage");
                    vramCounter = gpuQuery.TryAdd(@"\GPU Adapter Memory(*)\Dedicated Usage"); // Older drivers lack it.
                }
                catch (InvalidOperationException) { gpuQuery = null; }
                var vramTotals = GpuCounters.ReadDedicatedMemoryTotals();

                // Rate and percentage counters need two samples.
                system.Collect();
                gpuQuery?.Collect();
                for (int tick = 0; !stop.IsCancellationRequested; tick++)
                {
                    if (stop.Token.WaitHandle.WaitOne(1000)) return;
                    if (!system.Collect()) throw new InvalidOperationException("Sample failed");
                    var cpuValues = system.ReadInstances(cpuCounter);
                    if (cpuValues is { Count: > 0 }) cpu = Math.Min(100, cpuValues.Values.First());
                    rx = system.ReadInstances(rxCounter)?.Values.Sum() ?? 0;
                    tx = system.ReadInstances(txCounter)?.Values.Sum() ?? 0;

                    if (gpuQuery is not null && tick % 2 == 1 && gpuQuery.Collect())
                    {
                        gpu = GpuCounters.BusiestEngine(gpuQuery.ReadInstances(engineCounter));
                        vram = GpuCounters.BusiestVram(gpuQuery.ReadInstances(vramCounter), vramTotals);
                    }
                    Publish(cpu, gpu, vram, rx, tx);
                }
                gpuQuery?.Dispose();
            }
            catch (Exception)
            {
                // Counters can vanish (driver update, service restart); show what still works and retry later.
                Publish(cpu, null, null, 0, 0);
                if (stop.Token.WaitHandle.WaitOne(30000)) return;
            }
        }
    }

    void Publish(double cpu, double? gpu, (ulong Used, ulong Total)? vram, double rx, double tx)
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        bool memoryOk = GlobalMemoryStatusEx(ref memory);
        latest = new StatsSnapshot(
            cpu,
            memoryOk ? memory.TotalPhys - memory.AvailPhys : 0,
            memoryOk ? memory.TotalPhys : 1,
            gpu, vram?.Used, vram?.Total, rx, tx);
    }

    public void Dispose() => stop.Cancel();
}
