using System.Diagnostics;
using System.Drawing.Drawing2D;
using QRCoder;
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
        Size = new Size(980, 640);
        MinimumSize = new Size(900, 580);
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
        ShowPage("Pairing");

        if (store.Data.AutoUsb) StartUsbLoop();
        HandleCreated += (_, _) => _ = Task.Run(StartupTasksAsync);
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
        var sub = new Label { Text = "Host Â· Windows companion", Font = new Font("Segoe UI", 10.5f), ForeColor = Purple, AutoSize = true, Location = new Point(23, 46) };
        statusLabel.AutoSize = true; statusLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold); statusLabel.Location = new Point(360, 16);
        connectedLabel.AutoSize = true; connectedLabel.ForeColor = Muted; connectedLabel.Location = new Point(360, 44);
        header.Controls.AddRange(new Control[] { title, sub, statusLabel, connectedLabel });
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Purple, 2);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var nav = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Panel, Padding = new Padding(12, 16, 12, 12) };
        string[] names = { "Pairing", "Devices", "Activity", "Settings" };
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

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(24) };
        pages["Pairing"] = BuildPairingPage();
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
        p.Controls.Add(Btn("Open button editor", 490, 300, 190, Blue, (_, _) => OpenEditor()));
        fwLabel.Location = new Point(310, 348); fwLabel.AutoSize = true; p.Controls.Add(fwLabel);
        p.Controls.Add(Btn("Fix firewall", 310, 374, 170, Panel2, (_, _) => FixFirewall(true)));
        p.Controls.Add(H("The code is single-use and expires after 10 minutes. Phones and PC must be on the same Wi-Fi,\nor connected with a USB cable (enable USB debugging; see Settings).", 0, 420, 9.5f, Muted));
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
        statusLabel.Text = "â— Running on port " + server.Port;
        statusLabel.ForeColor = Green;
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
            var addrs = HostServer.LocalAddresses();
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
    void OpenEditor()
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

