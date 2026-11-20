using System.Diagnostics;
using StreamDeckHost.Core;
using StreamDeckHost.UI;

namespace StreamDeckHost;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Elevated helper that counts game frames (started by the host, see FpsHelper).
        var fi = Array.IndexOf(args, "--fps-helper");
        if (fi >= 0 && fi + 2 < args.Length && int.TryParse(args[fi + 2], out var fpid)) { Environment.ExitCode = FpsHelper.Run(args[fi + 1], fpid); return; }

        // "Unlock PC" helper modes (see UnlockHelper): SYSTEM helper, lock-screen typer, elevated install / remove.
        if (args.Contains("--unlock-helper")) { Environment.ExitCode = UnlockHelper.RunHelper(); return; }
        if (args.Contains("--unlock-type")) { Environment.ExitCode = UnlockHelper.RunTyper(); return; }
        var ii = Array.IndexOf(args, "--install-unlock-helper");
        if (ii >= 0 && ii + 1 < args.Length) { Environment.ExitCode = UnlockHelper.RunInstall(args[ii + 1]); return; }
        if (args.Contains("--uninstall-unlock-helper")) { Environment.ExitCode = UnlockHelper.RunUninstall(); return; }

        // After a self-update the old process is still shutting down: wait for it, then take the single-instance lock.
        var wi = Array.IndexOf(args, "--wait-pid");
        if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); } catch { }
        }

        // Test runs (SDH_DATA_DIR set) get their own lock so they can run next to the real, installed host.
        var testDir = Environment.GetEnvironmentVariable("SDH_DATA_DIR");
        var lockName = @"Local\StreamDeckHost.SingleInstance" + (string.IsNullOrEmpty(testDir) ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(testDir)))[..8]);
        using var mutex = new Mutex(true, lockName, out bool first);
        if (!first)
        {
            MessageBox.Show("StreamDeck Host is already running (check the system tray).", "StreamDeck Host");
            return;
        }

        Updater.CleanupOld();
        // An update downloaded last time is installed now, before anything else starts.
        if (Updater.HasStaged() && Updater.CanWriteHere() && Updater.ApplyStagedAndRestart()) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var store = new SettingsStore();
        store.Load();
        var layout = new LayoutStore();
        layout.Load();
        var server = new HostServer(store, layout);

        // Developer/test switch only: writes the live pairing payload to a file so automated tests can pair.
        var devIdx = Array.IndexOf(args, "--dev-pairing-file");
        if (devIdx >= 0 && devIdx + 1 < args.Length) server.DevPairingFile = args[devIdx + 1];

        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var port)) store.Data.Port = port;

        try { server.StartAsync().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            MessageBox.Show($"Could not start the server on port {store.Data.Port}:\n{e.Message}\n\nIs another program using the port?",
                "StreamDeck Host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool startHidden = args.Contains("--tray");
        bool noUpdate = args.Contains("--no-update");
        using var form = new MainForm(store, server, startHidden, noUpdate);
        Application.Run(form);
        server.Dispose();
    }
}
