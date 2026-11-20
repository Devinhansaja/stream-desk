using System.Diagnostics;
using System.Drawing.Drawing2D;
using QRCoder;
using Microsoft.Web.WebView2.WinForms;
using StreamDeckHost.Core;

namespace StreamDeckHost.UI;

/// <summary>Dark neon dashboard + tray icon. Closing the window hides it; the server keeps running.</summary>
public sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(8, 8, 20);
    static readonly Color Panel = Color.FromArgb(18, 18, 40);
    static readonly Color Panel2 = Color.FromArgb(26, 26, 56);
    static readonly Color Purple = Color.FromArgb(124, 77, 255);
    static readonly Color Blue = Color.FromArgb(30, 144, 255);
    static readonly Color Green = Color.FromArgb(34, 229, 139);
    static readonly Color Fg = Color.FromArgb(235, 235, 250);
    static readonly Color Muted = Color.FromArgb(150, 150, 185);

    readonly SettingsStore store;
    readonly HostServer server;
    readonly NotifyIcon tray = new();
    readonly Dictionary<string, Control> pages = new();
    readonly Dictionary<string, Button> navButtons = new();

    readonly Label statusLabel = new();
    readonly Label connectedLabel = new();
    readonly PictureBox qrBox = new();
    readonly Label addrLabel = new();
    readonly ListView devices = new();
    readonly TextBox logBox = new();
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    readonly bool startHidden;
    bool reallyExit;
    readonly bool noUpdate;
    readonly Label fwLabel = new();
    readonly Label updLabel = new();
    ToolStripMenuItem? updateMenuItem;
    UpdateInfo? pendingUpdate;
    string shownPayload = "";

    public MainForm(SettingsStore store, HostServer server, bool startHidden, bool noUpdate = false)
    {
        this.noUpdate = noUpdate;
        this.store = store;
        this.server = server;
        this.startHidden = startHidden;

        Text = "StreamDeck Host";
        Size = new Size(1320, 820);
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 10f);
        DoubleBuffered = true;
        Icon = MakeIcon();

        BuildLayout();
        BuildTray();
        server.Log += AppendLog;
        server.Changed += () => BeginInvokeSafe(RefreshAll);
        refresh.Tick += (_, _) => RefreshAll();
        refresh.Start();
        RefreshAll();
        ShowPage(store.Data.Devices.Count > 0 ? "Editor" : "Pairing");

        if (store.Data.AutoUsb) StartUsbLoop();
        HandleCreated += (_, _) => { Osd.Init(this); _ = Task.Run(StartupTasksAsync); };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    // ------------------------------------------------------------ layout
    void BuildLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Panel };
        var title = new Label { Text = "StreamDeck", Font = new Font("Segoe UI", 20f, FontStyle.Bold), ForeColor = Fg, AutoSize = true, Location = new Point(20, 8) };
        var sub = new Label { Text = "Host · Windows companion", Font = new Font("Segoe UI", 10.5f), ForeColor = Purple, AutoSize = true, Location = new Point(23, 46) };
        statusLabel.AutoSize = true; statusLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold); statusLabel.Location = new Point(360, 16);
        connectedLabel.AutoSize = true; connectedLabel.ForeColor = Muted; connectedLabel.Location = new Point(360, 44);
        header.Controls.AddRange(new Control[] { title, sub, statusLabel, connectedLabel });
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Purple, 2);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var nav = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Panel, Padding = new Padding(12, 16, 12, 12) };
        string[] names = { "Editor", "Pairing", "Discord", "OBS", "Internet", "Devices", "Activity", "Settings" };
        for (int i = names.Length - 1; i >= 0; i--)
        {
            var name = names[i];
            var b = new Button
            {
                Text = "  " + name, Dock = DockStyle.Top, Height = 46, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Fg, BackColor = Panel, Font = new Font("Segoe UI", 11f), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 6),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => ShowPage(name);
            nav.Controls.Add(b);
            navButtons[name] = b;
        }

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(12) };
        pages["Editor"] = BuildEditorPage();
        pages["Pairing"] = BuildPairingPage();
        pages["Discord"] = BuildDiscordPage();
        pages["OBS"] = BuildObsPage();
        pages["Internet"] = BuildInternetPage();
        pages["Devices"] = BuildDevicesPage();
        pages["Activity"] = BuildActivityPage();
        pages["Settings"] = BuildSettingsPage();
        foreach (var p in pages.Values) { p.Dock = DockStyle.Fill; p.Visible = false; content.Controls.Add(p); }

        Controls.Add(content);
        Controls.Add(nav);
        Controls.Add(header);
    }

    void ShowPage(string name)
    {
        if (name == "Editor") _ = EnsureEditorAsync();
        foreach (var (k, p) in pages) p.Visible = k == name;
        foreach (var (k, b) in navButtons) b.BackColor = k == name ? Purple : Panel;
    }

    Label H(string text, int x, int y, float size = 14f, Color? c = null) =>
        new() { Text = text, AutoSize = true, Location = new Point(x, y), Font = new Font("Segoe UI", size, size > 12 ? FontStyle.Bold : FontStyle.Regular), ForeColor = c ?? Fg };

    Button Btn(string text, int x, int y, int w, Color color, EventHandler click)
    {
        var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 38), FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        b.Click += click;
        return b;
    }

    Control BuildPairingPage()
    {
        var p = new Panel();
        p.Controls.Add(H("Pair your Android phone", 0, 0));
        p.Controls.Add(H("1. Install the StreamDeck app on your phone.   2. Open it and tap  Scan QR.   3. Point it at this code.", 0, 36, 10.5f, Muted));
        qrBox.Location = new Point(0, 76); qrBox.Size = new Size(280, 280); qrBox.SizeMode = PictureBoxSizeMode.Zoom; qrBox.BackColor = Color.White;
        p.Controls.Add(qrBox);
        addrLabel.Location = new Point(310, 80); addrLabel.AutoSize = true; addrLabel.ForeColor = Fg;
        p.Controls.Add(addrLabel);
        p.Controls.Add(Btn("New pairing code", 310, 300, 170, Purple, (_, _) => { server.RotateSecret(); RefreshAll(); }));
        p.Controls.Add(Btn("Open button editor", 490, 300, 190, Blue, (_, _) => ShowPage("Editor")));
        fwLabel.Location = new Point(310, 348); fwLabel.AutoSize = true; p.Controls.Add(fwLabel);
        p.Controls.Add(Btn("Fix firewall", 310, 374, 170, Panel2, (_, _) => FixFirewall(true)));
        p.Controls.Add(H("The code is single-use and expires after 10 minutes. Phones connect over the same Wi-Fi, USB cable,\nor from any network via the Internet tab (tunnel / Tailscale / your own address).", 0, 420, 9.5f, Muted));
        return p;
    }

    readonly Label tunnelLabel = new();
    readonly Label discordLabel = new();
    readonly Label obsLabel = new();

    Control BuildDiscordPage()
    {
        var p = new Panel();
        p.Controls.Add(H("Discord call page", 0, 0));
        p.Controls.Add(H("When you join a Discord voice call, the phone jumps to a \"Discord call\" page: channel, who is in the call, who is speaking, and\nbig Mute / Deafen / Leave buttons. Discord must be running on this PC.", 0, 36, 10f, Muted));

        p.Controls.Add(H("Quick connect (no setup)", 0, 96, 12f));
        p.Controls.Add(H("Press the button, then click Authorize in the popup that appears inside the Discord app. Done.\nThis uses Discord's own StreamKit app, which is an unofficial route that Discord could change. If it stops working, use the option below.", 0, 126, 9.5f, Muted));
        p.Controls.Add(Btn("Connect my Discord", 0, 176, 200, Purple, (_, _) =>
        {
            var a = DiscordAuth.Load(); a.Enabled = true; a.ClientId = ""; a.Secret = ""; a.AccessToken = ""; a.RefreshToken = ""; a.Expires = default; a.Save();
            server.Discord.Reconfigure();
        }));
        p.Controls.Add(Btn("Disconnect", 216, 176, 150, Panel2, (_, _) =>
        {
            var a = DiscordAuth.Load(); a.Enabled = false; a.AccessToken = ""; a.RefreshToken = ""; a.Expires = default; a.Save();
            server.Discord.Reconfigure();
        }));
        discordLabel.Location = new Point(0, 224); discordLabel.AutoSize = true; discordLabel.ForeColor = Fg;
        p.Controls.Add(discordLabel);

        p.Controls.Add(H("Advanced: use your own Discord application (the supported route)", 0, 290, 11f));
        p.Controls.Add(H("Developer Portal > New Application > copy the Application ID > OAuth2: add redirect  http://localhost  and Reset Secret to copy the Client Secret.", 0, 318, 9.5f, Muted));
        var auth = DiscordAuth.Load();
        var id = new TextBox { Location = new Point(0, 346), Width = 330, Text = auth.ClientId, BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        var sec = new TextBox { Location = new Point(344, 346), Width = 330, UseSystemPasswordChar = true, Text = auth.Secret.Length > 0 ? "********" : "", BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(id); p.Controls.Add(sec);
        p.Controls.Add(Btn("Save and connect", 0, 384, 170, Panel2, (_, _) =>
        {
            var a = DiscordAuth.Load();
            a.Enabled = true; a.ClientId = id.Text.Trim();
            if (sec.Text != "********") a.Secret = sec.Text.Trim();
            a.AccessToken = ""; a.RefreshToken = ""; a.Expires = default; a.Save();
            server.Discord.Reconfigure();
        }));
        p.Controls.Add(Btn("Open developer portal", 186, 384, 190, Panel2, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true })));
        p.Controls.Add(H("Your login is stored encrypted for your Windows account. The phone page is in the default layout (+ Widget page > Discord call).", 0, 440, 9.5f, Muted));
        return p;
    }
    Control BuildObsPage()
    {
        var p = new Panel();
        p.Controls.Add(H("OBS control center", 0, 0));
        p.Controls.Add(H("Control OBS Studio from the phone: switch scenes, go live, record, virtual camera and the audio mixer.\nOBS has this built in. Turn it on once:", 0, 36, 10f, Muted));
        p.Controls.Add(H("1.  In OBS:  Tools  >  WebSocket Server Settings  >  tick  Enable WebSocket server  (port 4455 is the default).\n2.  Tick  Enable Authentication  and press  Show Connect Info  to see the password  (or leave authentication off).\n3.  Enter the password here and press Connect.   OBS must be running (version 28 or newer).", 0, 92, 10f));
        var auth = ObsAuth.Load();
        p.Controls.Add(H("Host", 0, 176, 10f, Muted));
        var host = new TextBox { Location = new Point(0, 198), Width = 200, Text = auth.Host, BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(H("Port", 216, 176, 10f, Muted));
        var port = new TextBox { Location = new Point(216, 198), Width = 90, Text = auth.Port.ToString(), BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(H("Password", 322, 176, 10f, Muted));
        var pw = new TextBox { Location = new Point(322, 198), Width = 280, UseSystemPasswordChar = true, Text = auth.Password.Length > 0 ? "********" : "", BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(host); p.Controls.Add(port); p.Controls.Add(pw);
        p.Controls.Add(Btn("Connect", 0, 240, 150, Purple, (_, _) =>
        {
            var a = ObsAuth.Load();
            a.Enabled = true; a.Host = host.Text.Trim().Length > 0 ? host.Text.Trim() : "127.0.0.1";
            a.Port = int.TryParse(port.Text, out var pn) ? pn : 4455;
            if (pw.Text != "********") a.Password = pw.Text;
            a.Save();
            server.Obs.Reconfigure();
        }));
        p.Controls.Add(Btn("Disconnect", 166, 240, 150, Panel2, (_, _) =>
        {
            var a = ObsAuth.Load(); a.Enabled = false; a.Save();
            server.Obs.Reconfigure();
        }));
        obsLabel.Location = new Point(0, 296); obsLabel.AutoSize = true; obsLabel.ForeColor = Fg;
        p.Controls.Add(obsLabel);
        p.Controls.Add(H("The password is stored encrypted for your Windows account. The phone page is in the default layout (+ Widget page > OBS control center).", 0, 360, 9.5f, Muted));
        return p;
    }

    Control BuildInternetPage()
    {
        var p = new Panel();
        p.Controls.Add(H("Use your deck from any network", 0, 0));
        p.Controls.Add(H("By default the phone only works on the same Wi-Fi. Pick one option below, then scan the QR code on the Pairing tab again\n(or just reconnect: already-paired phones receive the new address automatically the next time they connect at home).", 0, 36, 10f, Muted));

        p.Controls.Add(H("Option 1 - Free internet tunnel (easiest, no router setup)", 0, 100, 11.5f));
        var chk = new CheckBox { Text = "Turn on internet access (Cloudflare tunnel, needs cloudflared)", Location = new Point(0, 134), AutoSize = true, Checked = store.Data.InternetTunnel, ForeColor = Fg };
        chk.CheckedChanged += (_, _) => { store.Data.InternetTunnel = chk.Checked; store.Save(); server.ApplyRemoteSettings(); };
        p.Controls.Add(chk);
        tunnelLabel.Location = new Point(0, 164); tunnelLabel.AutoSize = true; tunnelLabel.ForeColor = Muted;
        p.Controls.Add(tunnelLabel);
        p.Controls.Add(H("The tunnel address changes each time the host restarts; the phone picks up the new one when it can reach the PC,\nso for a never-changing address use Option 2 or 3. Traffic is encrypted (HTTPS) and still needs the pairing token.", 0, 196, 9.5f, Muted));

        p.Controls.Add(H("Option 2 - Tailscale (private, free, works from anywhere)", 0, 256, 11.5f));
        p.Controls.Add(H("Install Tailscale on this PC and on the phone and sign in to the same account. The Tailscale address (100.x.x.x) is added\nto the QR code automatically. Nothing else to configure.", 0, 290, 9.5f, Muted));

        p.Controls.Add(H("Option 3 - Your own address (port forwarding / DDNS / your own tunnel)", 0, 350, 11.5f));
        p.Controls.Add(H("Address phones should also try. Examples:  myhome.duckdns.org   |   203.0.113.5:47800   |   https://deck.example.com", 0, 384, 9.5f, Muted));
        var tb = new TextBox { Location = new Point(0, 412), Width = 480, Text = store.Data.RemoteAddress, BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(tb);
        p.Controls.Add(Btn("Save address", 496, 408, 150, Purple, (_, _) =>
        {
            store.Data.RemoteAddress = tb.Text.Trim();
            store.Save();
            server.ApplyRemoteSettings();
        }));
        p.Controls.Add(H("Security: phones still need the single-use pairing code and a secret token. The button editor stays PC-only.\nFor port forwarding, forward TCP port " + store.Data.Port + " to this PC and keep the host updated.", 0, 460, 9.5f, Muted));
        return p;
    }

    Control BuildDevicesPage()
    {
        var p = new Panel();
        p.Controls.Add(H("Paired devices", 0, 0));
        devices.Location = new Point(0, 44); devices.Size = new Size(700, 340);
        devices.View = View.Details; devices.FullRowSelect = true; devices.BackColor = Panel; devices.ForeColor = Fg; devices.BorderStyle = BorderStyle.None;
        devices.Columns.Add("Device", 240); devices.Columns.Add("Paired", 150); devices.Columns.Add("Last seen", 150); devices.Columns.Add("Status", 100);
        p.Controls.Add(devices);
        p.Controls.Add(Btn("Revoke selected", 0, 396, 160, Color.FromArgb(200, 50, 70), (_, _) =>
        {
            if (devices.SelectedItems.Count == 0) return;
            var id = (string)devices.SelectedItems[0].Tag!;
            server.Disconnect(id);
            store.RemoveDevice(id);
            RefreshAll();
        }));
        return p;
    }

    Control BuildActivityPage()
    {
        var p = new Panel();
        p.Controls.Add(H("Activity", 0, 0));
        logBox.Location = new Point(0, 44); logBox.Size = new Size(700, 380); logBox.Multiline = true; logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical; logBox.BackColor = Panel; logBox.ForeColor = Fg; logBox.BorderStyle = BorderStyle.None;
        logBox.Font = new Font("Consolas", 10f);
        p.Controls.Add(logBox);
        return p;
    }

    Control BuildSettingsPage()
    {
        var p = new Panel();
        p.Controls.Add(H("Settings", 0, 0));

        CheckBox Chk(string text, int y, bool v, Action<bool> set)
        {
            var c = new CheckBox { Text = text, Location = new Point(0, y), AutoSize = true, Checked = v, ForeColor = Fg };
            c.CheckedChanged += (_, _) => { set(c.Checked); store.Save(); };
            return c;
        }

        p.Controls.Add(Chk("Start with Windows (runs in the tray)", 50, Autostart.IsEnabled(), v => Autostart.Set(v)));
        p.Controls.Add(Chk("Automatically enable Android USB connection (needs adb + USB debugging)", 84, store.Data.AutoUsb, v =>
        {
            store.Data.AutoUsb = v;
            if (v) StartUsbLoop();
        }));
        p.Controls.Add(Chk("Allow phones to run raw shell commands (advanced - off is safer)", 118, store.Data.AllowShell, v => store.Data.AllowShell = v));

        p.Controls.Add(H($"Network port: {store.Data.Port}   (restart after changing settings.json)", 0, 164, 10f, Muted));
        var usbOut = H("", 0, 258, 9.5f, Muted);
        p.Controls.Add(Btn("Enable USB now", 0, 206, 170, Blue, (_, _) =>
        {
            var (ok, o) = UsbBridge.Reverse(store.Data.Port);
            usbOut.ForeColor = ok ? Green : Color.Salmon;
            usbOut.Text = o;
        }));
        p.Controls.Add(usbOut);
        p.Controls.Add(Chk("Install updates from GitHub automatically", 270, store.Data.AutoUpdate, v => store.Data.AutoUpdate = v));
        updLabel.Location = new Point(0, 340); updLabel.AutoSize = true; updLabel.ForeColor = Muted; updLabel.Text = "Version " + Updater.Current.ToString(3);
        p.Controls.Add(updLabel);
        p.Controls.Add(Btn("Check for updates now", 0, 300, 190, Blue, async (_, _) => await CheckUpdatesAsync(true)));
        p.Controls.Add(Btn("Open data folder", 190, 206, 170, Panel2, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", AppPaths.Root)));
        return p;
    }

    // ------------------------------------------------------------ tray
    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowWindow("Devices"));
        menu.Items.Add("Pair new device", null, (_, _) => { server.RotateSecret(); ShowWindow("Pairing"); });
        menu.Items.Add("Open button editor", null, (_, _) => OpenEditor());
        updateMenuItem = new ToolStripMenuItem("Install update and restart", null, (_, _) => ApplyUpdateNow()) { Visible = false };
        menu.Items.Add(updateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit StreamDeck Host", null, (_, _) => { reallyExit = true; tray.Visible = false; Close(); });
        tray.ContextMenuStrip = menu;
        tray.Icon = Icon;
        tray.Text = "StreamDeck Host";
        tray.Visible = true;
        tray.DoubleClick += (_, _) => ShowWindow("Pairing");
    }

    void ShowWindow(string page)
    {
        ShowPage(page);
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            tray.ShowBalloonTip(2000, "StreamDeck Host", "Still running in the background.", ToolTipIcon.Info);
            return;
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------ refresh
    void RefreshAll()
    {
        int n = server.ConnectedCount;
        statusLabel.Text = "● Running on port " + server.Port;
        statusLabel.ForeColor = Green;
        obsLabel.Text = "Status: " + server.Obs.StatusText + (server.Obs.Phase == "ok" ? $"\nScene: {server.Obs.Scene}   Streaming: {server.Obs.Streaming}   Recording: {server.Obs.Recording}" : "");
        discordLabel.Text = "Status: " + server.Discord.StatusText + (server.Discord.InCall ? $"\nIn call: {server.Discord.Channel}  (mic muted: {server.Discord.SelfMute}, deafened: {server.Discord.SelfDeaf})" : "");
        tunnelLabel.Text = "Status: " + server.Tunnel.Status + (server.Tunnel.Url != null ? "\nAddress: " + server.Tunnel.Url : "");
        connectedLabel.Text = n == 0 ? "No phone connected" : $"{n} phone{(n == 1 ? "" : "s")} connected";

        var payload = server.PairingPayload();
        if (payload != shownPayload)
        {
            shownPayload = payload;
            try
            {
                using var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
                var png = new PngByteQRCode(data).GetGraphic(8, new byte[] { 0, 0, 0, 255 }, new byte[] { 255, 255, 255, 255 });
                qrBox.Image?.Dispose();
                qrBox.Image = Image.FromStream(new MemoryStream(png));
            }
            catch { }
            var addrs = server.AllAddresses();
            addrLabel.Text = $"PC name:  {server.PcName}\nPort:  {server.Port}\n\nAddresses:\n  " +
                (addrs.Count == 0 ? "(no network)" : string.Join("\n  ", addrs)) +
                "\n\nUSB: 127.0.0.1 (via adb reverse)" +
                "\n\nManual pairing code:\n  " + server.CurrentPairingSecret();
        }

        var connected = server.ConnectedDeviceIds;
        devices.BeginUpdate();
        devices.Items.Clear();
        foreach (var d in store.Data.Devices.ToList())
        {
            var it = new ListViewItem(d.Name) { Tag = d.Id };
            it.SubItems.Add(d.PairedAt.ToString("yyyy-MM-dd HH:mm"));
            it.SubItems.Add(d.LastSeen.ToString("yyyy-MM-dd HH:mm"));
            it.SubItems.Add(connected.Contains(d.Id) ? "Online" : "Offline");
            it.ForeColor = connected.Contains(d.Id) ? Green : Muted;
            devices.Items.Add(it);
        }
        devices.EndUpdate();
    }

    void AppendLog(string line) => BeginInvokeSafe(() =>
    {
        logBox.AppendText(line + Environment.NewLine);
        if (logBox.TextLength > 60000) logBox.Text = logBox.Text[^30000..];
    });

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    void StartUsbLoop()
    {
        if (usbLoopRunning) return;
        usbLoopRunning = true;
        _ = Task.Run(async () =>
        {
            while (!reallyExit)
            {
                if (store.Data.AutoUsb && UsbBridge.FindAdb() != null) UsbBridge.Reverse(store.Data.Port);
                await Task.Delay(10000);
            }
        });
    }
    bool usbLoopRunning;

    // ------------------------------------------------------------ editor / firewall / updates
    // The editor is a web page served by this app; show it inside the window (WebView2), or fall back to the browser.
    readonly WebView2 editorView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(7, 7, 15) };
    readonly Label editorMsg = new() { AutoSize = true, ForeColor = Muted, Location = new Point(20, 20) };
    bool editorStarted;

    Control BuildEditorPage()
    {
        var p = new Panel { Padding = new Padding(0) };
        p.Controls.Add(editorView);
        p.Controls.Add(editorMsg);
        editorMsg.BringToFront();
        return p;
    }

    async Task EnsureEditorAsync()
    {
        if (editorStarted) return;
        editorStarted = true;
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Root, "webview2"));
            await editorView.EnsureCoreWebView2Async(env);
            editorView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            editorView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            editorView.Source = new Uri($"http://localhost:{server.Port}/editor?embedded=1");
            editorMsg.Visible = false;
        }
        catch (Exception e)
        {
            editorMsg.Text = "The built-in editor needs the Microsoft Edge WebView2 runtime (" + e.Message + ").\nOpening the editor in your browser instead.";
            editorMsg.Visible = true;
            editorView.Visible = false;
            OpenEditorInBrowser();
        }
    }

    void OpenEditor() => ShowWindow("Editor");

    void OpenEditorInBrowser()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{server.Port}/editor") { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show("Could not open the browser: " + e.Message); }
    }

    void UpdateFirewallLabel()
    {
        bool ok = Firewall.RuleExists(server.Port);
        fwLabel.Text = ok ? "Firewall: phone connections allowed" : "Firewall: NOT allowed - the phone cannot connect until fixed";
        fwLabel.ForeColor = ok ? Green : Color.Salmon;
    }

    void FixFirewall(bool explicitClick)
    {
        bool ok = Firewall.AddRulesElevated(server.Port);
        UpdateFirewallLabel();
        if (explicitClick)
            MessageBox.Show(ok ? "Done. Your phone can now reach this PC." : "The firewall rule was not added (administrator permission is needed).",
                "StreamDeck Host");
    }

    async Task StartupTasksAsync()
    {
        BeginInvokeSafe(UpdateFirewallLabel);
        if (!Firewall.RuleExists(server.Port) && !store.Data.FirewallAsked)
        {
            store.Data.FirewallAsked = true;
            store.Save();
            BeginInvokeSafe(() =>
            {
                var r = MessageBox.Show(
                    "Windows Firewall will block your phone from reaching this PC unless you allow it.\n\nAllow phone connections now? (Windows will ask for administrator permission.)",
                    "StreamDeck Host", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) FixFirewall(false);
            });
        }
        if (noUpdate) return;
        await Task.Delay(4000);
        while (!reallyExit)
        {
            if (store.Data.AutoUpdate) await CheckUpdatesAsync(false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    async Task CheckUpdatesAsync(bool explicitClick)
    {
        try
        {
            var u = await Updater.CheckAsync();
            if (u == null)
            {
                if (explicitClick) BeginInvokeSafe(() => MessageBox.Show("You are on the latest version (" + Updater.Current.ToString(3) + ").", "StreamDeck Host"));
                return;
            }
            AppendLog($"Update {u.Version.ToString(3)} found, downloading...");
            if (!Updater.CanWriteHere())
            {
                BeginInvokeSafe(() =>
                {
                    tray.ShowBalloonTip(5000, "StreamDeck Host", $"Version {u.Version.ToString(3)} is available. Download it from GitHub.", ToolTipIcon.Info);
                    if (explicitClick) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.PageUrl) { UseShellExecute = true });
                });
                return;
            }
            await Updater.DownloadAsync(u);
            pendingUpdate = u;
            BeginInvokeSafe(() =>
            {
                if (updateMenuItem != null) updateMenuItem.Visible = true;
                updLabel.Text = $"Version {Updater.Current.ToString(3)}  -  update {u.Version.ToString(3)} ready";
                // Silent on start-up: install right away. Otherwise wait for the user.
                tray.ShowBalloonTip(4000, "StreamDeck Host", $"Updating to {u.Version.ToString(3)}...", ToolTipIcon.Info);
            });
            await Task.Delay(1500);
            if (!explicitClick && DateTime.Now - Process.GetCurrentProcess().StartTime < TimeSpan.FromMinutes(5))
                BeginInvokeSafe(ApplyUpdateNow);
        }
        catch (Exception e) { AppendLog("Update check failed: " + e.Message); }
    }

    void ApplyUpdateNow()
    {
        if (!Updater.HasStaged()) return;
        reallyExit = true;
        tray.Visible = false;
        server.Dispose();
        if (Updater.ApplyStagedAndRestart()) { Application.Exit(); Environment.Exit(0); }
        reallyExit = false;
    }

    // ------------------------------------------------------------ icon
    static Icon MakeIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            using var s = asm.GetManifestResourceStream(name)!;
            return new Icon(s);
        }
        catch { return MakeFallbackIcon(); }
    }

    static Icon MakeFallbackIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var br = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(124, 77, 255), Color.FromArgb(30, 144, 255), 45f);
            using var path = new GraphicsPath();
            path.AddArc(2, 2, 20, 20, 180, 90); path.AddArc(42, 2, 20, 20, 270, 90);
            path.AddArc(42, 42, 20, 20, 0, 90); path.AddArc(2, 42, 20, 20, 90, 90); path.CloseFigure();
            g.FillPath(br, path);
            g.FillPolygon(Brushes.White, new[] { new Point(24, 16), new Point(24, 48), new Point(50, 32) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

