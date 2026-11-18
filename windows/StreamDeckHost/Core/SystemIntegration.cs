using System.Diagnostics;
using Microsoft.Win32;

namespace StreamDeckHost.Core;

public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "StreamDeckHost";

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        if (k == null) return;
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(ValueName, false);
    }

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(ValueName) != null;
    }
}

/// <summary>Android USB connection: forwards the phone's localhost:port to this PC with "adb reverse".</summary>
public static class UsbBridge
{
    public static string? FindAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        };
        var env = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrEmpty(env)) candidates.Add(Path.Combine(env, "platform-tools", "adb.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { candidates.Add(Path.Combine(dir.Trim(), "adb.exe")); } catch { }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static (bool ok, string output) Reverse(int port)
    {
        var adb = FindAdb();
        if (adb == null) return (false, "adb.exe not found. Install Android platform-tools and put adb on PATH.");
        try
        {
            var psi = new ProcessStartInfo(adb, $"reverse tcp:{port} tcp:{port}")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return (false, "adb timed out"); }
            return (p.ExitCode == 0, o.Length == 0 ? "USB forwarding active" : o);
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
