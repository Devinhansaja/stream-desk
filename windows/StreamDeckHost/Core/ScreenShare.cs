using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StreamDeckHost.Core;

/// <summary>Shows the PC screen on the phone (a mirrored "external screen") and lets taps on it click on the PC.</summary>
public static class ScreenShare
{
    static readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    static Rectangle Bounds => System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

    /// <summary>One JPEG frame of the primary monitor, scaled to at most [maxW] wide.</summary>
    public static byte[]? Capture(int maxW = 1280, long quality = 55)
    {
        try
        {
            var b = Bounds;
            using var full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            var w = Math.Min(maxW, b.Width);
            var h = (int)Math.Round(b.Height * (w / (double)b.Width));
            using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.Bilinear; g.DrawImage(full, 0, 0, w, h); }
            using var ms = new MemoryStream();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            small.Save(ms, jpeg, ep);
            return ms.ToArray();
        }
        catch { return null; } // e.g. secure desktop / lock screen
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    const uint LDOWN = 2, LUP = 4, RDOWN = 8, RUP = 16, WHEEL = 0x800;

    /// <summary>x,y are 0..1 positions on the shared screen.</summary>
    public static void Pointer(string kind, double x, double y, int delta = 0)
    {
        var b = Bounds;
        x = Math.Clamp(x, 0, 1); y = Math.Clamp(y, 0, 1);
        SetCursorPos(b.Left + (int)(x * (b.Width - 1)), b.Top + (int)(y * (b.Height - 1)));
        switch (kind)
        {
            case "click": mouse_event(LDOWN, 0, 0, 0, UIntPtr.Zero); mouse_event(LUP, 0, 0, 0, UIntPtr.Zero); break;
            case "dbl": for (int i = 0; i < 2; i++) { mouse_event(LDOWN, 0, 0, 0, UIntPtr.Zero); mouse_event(LUP, 0, 0, 0, UIntPtr.Zero); } break;
            case "rclick": mouse_event(RDOWN, 0, 0, 0, UIntPtr.Zero); mouse_event(RUP, 0, 0, 0, UIntPtr.Zero); break;
            case "down": mouse_event(LDOWN, 0, 0, 0, UIntPtr.Zero); break;
            case "up": mouse_event(LUP, 0, 0, 0, UIntPtr.Zero); break;
            case "wheel": mouse_event(WHEEL, 0, 0, (uint)delta, UIntPtr.Zero); break;
        }
    }
}
