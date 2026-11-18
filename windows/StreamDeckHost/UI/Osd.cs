using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace StreamDeckHost.UI;

/// <summary>
/// Small on-screen flyout like Windows' own volume indicator. Windows only shows its own flyout for real key presses,
/// so volume/mute use media keys (see AudioControl) and this is used for things Windows shows nothing for
/// (microphone mute, brightness set from the phone).
/// </summary>
public static class Osd
{
    static Control? owner;
    static OsdForm? form;

    public static void Init(Control uiOwner) => owner = uiOwner;

    public static void ShowBar(string glyph, string label, int percent) => Post(f => f.Present(glyph, label, percent, false));
    public static void ShowMic(bool muted) => Post(f => f.Present("", muted ? "Microphone muted" : "Microphone on", null, muted));

    static void Post(Action<OsdForm> act)
    {
        var o = owner;
        if (o == null || o.IsDisposed || !o.IsHandleCreated) return;
        try
        {
            o.BeginInvoke(() =>
            {
                form ??= new OsdForm();
                act(form);
            });
        }
        catch { }
    }
}

sealed class OsdForm : Form
{
    string glyph = "", text = "";
    int? percent;
    bool strike;
    readonly System.Windows.Forms.Timer hide = new() { Interval = 1600 };
    static readonly string IconFont = Fonts.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    static System.Collections.Generic.HashSet<string> Fonts => new(new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name));

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, 58);
        BackColor = Color.FromArgb(32, 32, 32);
        DoubleBuffered = true;
        hide.Tick += (_, _) => { hide.Stop(); Hide(); };
        Shown += (_, _) => RoundCorners();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000020 /*TRANSPARENT: click-through*/;
            return cp;
        }
    }

    void RoundCorners()
    {
        using var p = new GraphicsPath();
        int r = 16;
        p.AddArc(0, 0, r, r, 180, 90); p.AddArc(Width - r, 0, r, r, 270, 90);
        p.AddArc(Width - r, Height - r, r, r, 0, 90); p.AddArc(0, Height - r, r, r, 90, 90);
        p.CloseFigure();
        Region = new Region(p);
    }

    public void Present(string g, string label, int? pct, bool strikeIcon)
    {
        glyph = g; text = label; percent = pct; strike = strikeIcon;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + 16, wa.Top + 16);   // top-left, like Windows 11's flyout
        Invalidate();
        if (!Visible) Show();
        RoundCorners();
        hide.Stop(); hide.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var pen = new Pen(Color.FromArgb(70, 70, 70))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        using var iconFont = new Font(IconFont, 15f);
        using var white = new SolidBrush(Color.White);
        g.DrawString(glyph, iconFont, white, 16, 17);
        if (strike) using (var pen = new Pen(Color.White, 2f)) g.DrawLine(pen, 14, 38, 38, 16);

        if (percent is int p)
        {
            int x0 = 60, x1 = Width - 58, cy = Height / 2;
            using var track = new SolidBrush(Color.FromArgb(160, 160, 160));
            using var fill = new SolidBrush(Color.FromArgb(76, 194, 255));
            g.FillRectangle(track, x0, cy - 2, x1 - x0, 4);
            int fx = x0 + (x1 - x0) * Math.Clamp(p, 0, 100) / 100;
            g.FillRectangle(fill, x0, cy - 2, fx - x0, 4);
            g.FillEllipse(white, fx - 6, cy - 6, 12, 12);
            using var f = new Font("Segoe UI", 12f);
            g.DrawString(p.ToString(), f, white, x1 + 14, cy - 11);
        }
        else
        {
            using var f = new Font("Segoe UI", 12f);
            g.DrawString(text, f, white, 60, 17);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) hide.Dispose(); base.Dispose(disposing); }
}
