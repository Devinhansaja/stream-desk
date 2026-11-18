using System.Runtime.InteropServices;

namespace StreamDeckHost.Core;

/// <summary>Win32 interop: keyboard injection, lock, sleep, wallpaper.</summary>
public static class Native
{
    // ---------- SendInput ----------
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;

    static readonly HashSet<ushort> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, // PgUp..Delete
        0x5B, 0x5C, 0x6F, 0x90,                                   // Win, Divide, NumLock
    };

    static INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
    }

    static readonly Dictionary<string, ushort> VkMap = BuildMap();

    static Dictionary<string, ushort> BuildMap()
    {
        var m = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
            ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["pause"] = 0x13,
            ["menu"] = 0x5D,
            ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD, ["mute"] = 0xAD,
            ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["next"] = 0xB0, ["prevtrack"] = 0xB1, ["prev"] = 0xB1, ["stop"] = 0xB2,
            ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE, ["slash"] = 0xBF, ["backtick"] = 0xC0,
        };
        for (int i = 1; i <= 24; i++) m["f" + i] = (ushort)(0x6F + i);
        for (char c = 'a'; c <= 'z'; c++) m[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (char c = '0'; c <= '9'; c++) m[c.ToString()] = (ushort)c;
        return m;
    }

    public static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B;

    /// <summary>Press a combination such as "ctrl+shift+esc" or "win+d".</summary>
    public static void Hotkey(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty hotkey");
        var vks = new List<ushort>();
        foreach (var p in parts)
        {
            if (!VkMap.TryGetValue(p, out var vk)) throw new ArgumentException($"Unknown key '{p}'");
            vks.Add(vk);
        }
        var inputs = new List<INPUT>();
        foreach (var vk in vks) inputs.Add(Key(vk, false));
        for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(Key(vks[i], true));
        var arr = inputs.ToArray();
        if (SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) != arr.Length)
            throw new InvalidOperationException("SendInput blocked (is a elevated window focused?)");
    }

    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            if (c == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        var arr = inputs.ToArray();
        if (arr.Length > 0) SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    // ---------- Power / session ----------
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // ---------- Wallpaper ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, string vparam, uint winIni);

    public static void SetWallpaper(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Wallpaper not found", path);
        const uint SPI_SETDESKWALLPAPER = 0x14, UPDATE_AND_BROADCAST = 0x1 | 0x2;
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, UPDATE_AND_BROADCAST))
            throw new InvalidOperationException("Windows rejected the wallpaper change");
    }
}
