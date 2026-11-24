using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>
/// Reads an Xbox-compatible (XInput) controller on the PC, only while a phone is showing the Controller page.
/// Sends buttons / triggers / sticks so the 3D controller on the phone lights up as you press things.
/// </summary>
public sealed class GamepadMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct XSTATE { public uint packet; public ushort buttons; public byte lt, rt; public short lx, ly, rx, ry; }
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint GetStateEx(uint index, out XSTATE s);   // like XInputGetState, but also reports the Guide button
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern uint GetState(uint index, out XSTATE s);

    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    public Func<int>? SubCount;
    public event Action? Changed;
    JsonObject last = Make(false, 0, new XSTATE());
    int active = -1;
    bool useEx = true;
    readonly bool fake = Environment.GetEnvironmentVariable("SDH_FAKE_PAD") != null;

    static JsonObject Make(bool connected, int slot, XSTATE s)
    {
        static int Ax(short v) => (int)Math.Round(v / 327.67);
        return new JsonObject
        {
            ["t"] = "pad", ["connected"] = connected, ["slot"] = slot, ["b"] = (int)s.buttons, ["lt"] = s.lt, ["rt"] = s.rt,
            ["lx"] = Ax(s.lx), ["ly"] = Ax(s.ly), ["rx"] = Ax(s.rx), ["ry"] = Ax(s.ry),
        };
    }

    public JsonObject Snapshot() { lock (gate) return (JsonObject)last.DeepClone(); }
    public void Start() => _ = Task.Run(Loop);
    public void Dispose() => cts.Cancel();

    uint Poll(uint i, out XSTATE s)
    {
        if (useEx) { try { return GetStateEx(i, out s); } catch (EntryPointNotFoundException) { useEx = false; } }
        return GetState(i, out s);
    }

    async Task Loop()
    {
        DateTime probeAt = DateTime.MinValue, beat = DateTime.MinValue;
        var t0 = DateTime.UtcNow;
        while (!cts.IsCancellationRequested)
        {
            if ((SubCount?.Invoke() ?? 0) == 0) { try { await Task.Delay(400, cts.Token); } catch { break; } continue; }
            try
            {
                XSTATE s = default; bool ok = false;
                if (fake)
                {
                    // test mode: pretend a controller is pressing one button every half second and moving the sticks
                    double t = (DateTime.UtcNow - t0).TotalSeconds;
                    ushort[] bits = { 0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0x20, 0x10, 0x400, 0x800, 0x40, 0x80, 1, 2, 4, 8 };
                    s.buttons = bits[(int)(t * 2) % bits.Length];
                    s.lt = (byte)(((int)(t * 2) % 8) == 3 ? 200 : 0);
                    s.lx = (short)(Math.Cos(t) * 20000); s.ly = (short)(Math.Sin(t) * 20000);
                    ok = true; active = 0;
                }
                else if (active >= 0) { ok = Poll((uint)active, out s) == 0; if (!ok) active = -1; }
                else if ((DateTime.UtcNow - probeAt).TotalSeconds >= 1.5)
                {
                    probeAt = DateTime.UtcNow;
                    for (uint i = 0; i < 4 && !ok; i++) if (Poll(i, out s) == 0) { ok = true; active = (int)i; }
                }
                var j = Make(ok, Math.Max(active, 0), s);
                bool changed;
                lock (gate)
                {
                    changed = last.ToJsonString() != j.ToJsonString() || (DateTime.UtcNow - beat).TotalSeconds > 2;
                    if (changed) { last = j; beat = DateTime.UtcNow; }
                }
                if (changed) Changed?.Invoke();
            }
            catch { }
            try { await Task.Delay(active >= 0 ? 33 : 300, cts.Token); } catch { break; }
        }
    }
}
