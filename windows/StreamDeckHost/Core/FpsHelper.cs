using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace StreamDeckHost.Core;

/// <summary>
/// Runs when this exe is started with "--fps-helper" (as administrator, because Windows only allows ETW tracing to admins).
/// Counts DXGI Present() calls per process, which is what a game does once per frame, and streams FPS / frame time / 1% low
/// to the normal host over a named pipe. It does nothing else.
/// </summary>
public static class FpsHelper
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    public static int Run(string pipeName, int parentPid)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(8000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            var frames = new Dictionary<int, List<double>>();
            var gate = new object();
            double latest = 0;

            using var session = new TraceEventSession("StreamDeckHostFps-" + Environment.ProcessId) { StopOnDispose = true };
            session.EnableProvider("Microsoft-Windows-DXGI", TraceEventLevel.Informational, ulong.MaxValue);
            session.Source.AllEvents += d =>
            {
                if (d.ProviderGuid != Dxgi || (int)d.ID != 42) return;   // 42 = Present::Start
                lock (gate)
                {
                    if (!frames.TryGetValue(d.ProcessID, out var l)) frames[d.ProcessID] = l = new List<double>();
                    l.Add(d.TimeStampRelativeMSec); latest = Math.Max(latest, d.TimeStampRelativeMSec);
                }
            };

            var reporter = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        Thread.Sleep(500);
                        try { using var parent = Process.GetProcessById(parentPid); if (parent.HasExited) break; } catch { break; }
                        var sb = new StringBuilder("{\"p\":{"); bool first = true;
                        lock (gate)
                        {
                            foreach (var (pid, list) in frames.ToList())
                            {
                                list.RemoveAll(t => t < latest - 10000);
                                if (list.Count == 0) { frames.Remove(pid); continue; }
                                var recent = list.Where(t => t > latest - 1000).ToList();
                                if (recent.Count < 3 || latest - list[^1] > 1500) continue;      // not presenting right now
                                double fps = (recent.Count - 1) * 1000.0 / Math.Max(1, recent[^1] - recent[0]);
                                var diffs = new List<double>(); for (int i = 1; i < list.Count; i++) diffs.Add(list[i] - list[i - 1]);
                                diffs.Sort(); double p99 = diffs.Count > 20 ? diffs[(int)(diffs.Count * 0.99) - 1] : diffs[^1];
                                double ms = 1000.0 / fps, low1 = p99 > 0 ? 1000.0 / p99 : 0;
                                if (!first) sb.Append(','); first = false;
                                sb.Append('"').Append(pid).Append("\":[").Append(fps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                                  .Append(ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(low1.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                            }
                        }
                        sb.Append("}}");
                        writer.WriteLine(sb.ToString());       // throws when the host closed the pipe
                    }
                }
                catch { }
                try { session.Dispose(); } catch { }
            }) { IsBackground = true };
            reporter.Start();

            session.Source.Process();    // blocks until the session is disposed
            return 0;
        }
        catch (Exception) { return 1; }
    }
}
