using System.Management;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace StreamDeckHost.Core;

/// <summary>Master volume + microphone via Windows Core Audio.</summary>
public static class AudioControl
{
    public static int GetVolume()
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
    }

    public static void SetVolume(int percent)
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        if (percent > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
    }

    public static bool GetMuted()
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return d.AudioEndpointVolume.Mute;
    }

    public static void SetMuted(bool mute)
    {
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        d.AudioEndpointVolume.Mute = mute;
    }

    /// <summary>null when the PC has no active microphone.</summary>
    public static bool? GetMicMuted()
    {
        using var en = new MMDeviceEnumerator();
        var devs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        if (devs.Count == 0) return null;
        bool all = true;
        foreach (var d in devs) { if (!d.AudioEndpointVolume.Mute) all = false; d.Dispose(); }
        return all;
    }

    public static void SetMicMuted(bool mute)
    {
        using var en = new MMDeviceEnumerator();
        var devs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        if (devs.Count == 0) throw new InvalidOperationException("No active microphone found");
        foreach (var d in devs) { d.AudioEndpointVolume.Mute = mute; d.Dispose(); }
    }
}

/// <summary>Display brightness: laptop panels via WMI, external monitors via DDC/CI (dxva2).</summary>
public static class BrightnessControl
{
    // ---- WMI (internal panels) ----
    static int WmiGet()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    static bool WmiSet(int level)
    {
        try
        {
            bool any = false;
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    // ---- DDC/CI ----
    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc;
    }
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);

    static void ForEachPhysicalMonitor(Func<IntPtr, bool> visit)
    {
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var n) || n == 0) return true;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
            try { foreach (var m in arr) if (!visit(m.hPhysicalMonitor)) return false; }
            finally { DestroyPhysicalMonitors(n, arr); }
            return true;
        }, IntPtr.Zero);
    }

    static int DdcGet()
    {
        int result = -1;
        try
        {
            ForEachPhysicalMonitor(h =>
            {
                if (GetMonitorBrightness(h, out var min, out var cur, out var max) && max > min)
                { result = (int)Math.Round((cur - min) * 100.0 / (max - min)); return false; }
                return true;
            });
        }
        catch { }
        return result;
    }

    static bool DdcSet(int level)
    {
        bool any = false;
        try
        {
            ForEachPhysicalMonitor(h =>
            {
                if (GetMonitorBrightness(h, out var min, out _, out var max) && max > min)
                    if (SetMonitorBrightness(h, (uint)(min + (max - min) * level / 100.0))) any = true;
                return true;
            });
        }
        catch { }
        return any;
    }

    /// <summary>0..100, or -1 when no controllable display exists.</summary>
    public static int Get()
    {
        int v = WmiGet();
        return v >= 0 ? v : DdcGet();
    }

    public static void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        bool a = WmiSet(percent);
        bool b = DdcSet(percent);
        if (!a && !b) throw new InvalidOperationException("This display does not support software brightness control");
    }
}
