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

    public static (double usedGb, double totalGb) RamInfo()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        return ((m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0, m.ullTotalPhys / 1073741824.0);
    }

    // ---- network: current speed and data used since the host started (all real adapters added together)
    static readonly Dictionary<string, (long rx, long tx)> netLast = new();
    static DateTime netAt = DateTime.MinValue;
    static double netDown, netUp, netTotRx, netTotTx;

    /// <summary>(download B/s, upload B/s, downloaded bytes, uploaded bytes) since the host started.</summary>
    public static (double down, double up, double totalDown, double totalUp) Net()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow; var dt = (now - netAt).TotalSeconds;
            if (netAt != DateTime.MinValue && dt < 0.8) return (netDown, netUp, netTotRx, netTotTx);
            long dRx = 0, dTx = 0;
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    var st = nic.GetIPv4Statistics(); long rx = st.BytesReceived, tx = st.BytesSent;
                    if (netLast.TryGetValue(nic.Id, out var p) && rx >= p.rx && tx >= p.tx) { dRx += rx - p.rx; dTx += tx - p.tx; }
                    netLast[nic.Id] = (rx, tx);
                }
            }
            catch { }
            if (netAt != DateTime.MinValue && dt > 0) { netDown = dRx / dt; netUp = dTx / dt; netTotRx += dRx; netTotTx += dTx; }
            netAt = now;
            return (netDown, netUp, netTotRx, netTotTx);
        }
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
