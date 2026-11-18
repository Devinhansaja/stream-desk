using System.Diagnostics;

namespace StreamDeckHost.Core;

/// <summary>Windows Firewall rule so the phone can reach this PC. Without it, pairing silently times out.</summary>
public static class Firewall
{
    const string RuleTcp = "StreamDeck Host (TCP)";
    const string RuleUdp = "StreamDeck Host (discovery)";

    static (int code, string output) Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return (p.ExitCode, o);
        }
        catch (Exception e) { return (-1, e.Message); }
    }

    public static bool RuleExists(int port)
    {
        var (code, o) = Netsh($"advfirewall firewall show rule name=\"{RuleTcp}\"");
        return code == 0 && o.Contains(port.ToString());
    }

    /// <summary>Adds the rules through a UAC prompt. Returns true when the rules exist afterwards.</summary>
    public static bool AddRulesElevated(int port)
    {
        string cmd =
            $"netsh advfirewall firewall delete rule name=\"{RuleTcp}\" & netsh advfirewall firewall delete rule name=\"{RuleUdp}\" & " +
            $"netsh advfirewall firewall add rule name=\"{RuleTcp}\" dir=in action=allow protocol=TCP localport={port} profile=any & " +
            $"netsh advfirewall firewall add rule name=\"{RuleUdp}\" dir=in action=allow protocol=UDP localport={HostServer.DiscoveryPort} profile=any";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
        }
        catch { return false; } // user declined UAC
        return RuleExists(port);
    }
}
