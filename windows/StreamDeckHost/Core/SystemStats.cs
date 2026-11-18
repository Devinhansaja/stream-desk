using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>CPU %, RAM %, uptime and free disk space for the live tiles on the phone.</summary>
public static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint lo, hi; }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    static ulong U(FILETIME f) => ((ulong)f.hi << 32) | f.lo;
    static ulong lastIdle, lastTotal;
    static int lastCpu;
    static readonly object gate = new();
    static JsonObject? disks;
    static DateTime disksAt = DateTime.MinValue;

    public static int Cpu()
    {
        lock (gate)
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return lastCpu;
            ulong i = U(idle), total = U(kernel) + U(user); // kernel time includes idle
            if (lastTotal != 0 && total > lastTotal)
                lastCpu = (int)Math.Round(100.0 * (1.0 - (double)(i - lastIdle) / (total - lastTotal)));
            lastIdle = i; lastTotal = total;
            return Math.Clamp(lastCpu, 0, 100);
        }
    }

    public static int Ram()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (int)m.dwMemoryLoad : 0;
    }

    public static long UptimeSeconds() => Environment.TickCount64 / 1000;

    public static JsonObject Disks()
    {
        lock (gate)
        {
            if (disks != null && DateTime.UtcNow - disksAt < TimeSpan.FromSeconds(30)) return (JsonObject)disks.DeepClone();
            var o = new JsonObject();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    o[d.Name.TrimEnd('\\', ':')] = new JsonObject
                    {
                        ["free"] = (int)(d.AvailableFreeSpace / (1L << 30)),
                        ["total"] = (int)(d.TotalSize / (1L << 30)),
                    };
                }
                catch { }
            }
            disks = o; disksAt = DateTime.UtcNow;
            return (JsonObject)o.DeepClone();
        }
    }
}
