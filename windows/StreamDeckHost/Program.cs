using StreamDeckHost.Core;
using StreamDeckHost.UI;

namespace StreamDeckHost;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\StreamDeckHost.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("StreamDeck Host is already running (check the system tray).", "StreamDeck Host");
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var store = new SettingsStore();
        store.Load();
        var server = new HostServer(store);

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
        using var form = new MainForm(store, server, startHidden);
        Application.Run(form);
        server.Dispose();
    }
}
