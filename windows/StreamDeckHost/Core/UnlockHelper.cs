using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamDeckHost.Core;

/// <summary>
/// "Unlock PC" from the deck. Windows only lets a system-level program type on the lock screen, so a small helper runs as a
/// scheduled task under SYSTEM (installed once with an admin prompt). It keeps the PIN encrypted, and when asked it types the
/// PIN on the lock screen. It refuses to type unless the PC really is locked.
///   --unlock-helper            the long-running SYSTEM helper (pipe server)
///   --unlock-type              short-lived child started by the helper inside the locked session
///   --install-unlock-helper    elevated installer;   --uninstall-unlock-helper   elevated remover
/// </summary>
public static class UnlockPaths
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamDeckHost", "unlock");
    public static string SecretDir => Path.Combine(Dir, "secret");          // only SYSTEM + Administrators can open this folder
    public static string Exe => Path.Combine(Dir, "StreamDeckHost.exe");
    public static string Config => Path.Combine(Dir, "config.json");
    public static string PinFile => Path.Combine(SecretDir, "pin.dat");
    public const string Pipe = "StreamDeckHostUnlock";
    public const string Task = "StreamDeckHost Unlock Helper";
}

public static class UnlockHelper
{
    // ---------------------------------------------------------------- Win32
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQuerySessionInformation(IntPtr h, uint session, int cls, out IntPtr buf, out uint bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr attrs, int imp, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr tok, int cls, ref uint val, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr tok, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desk);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int idx, StringBuilder buf, int len, out int needed);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desk);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; }

    const uint KEYUP = 2, UNICODE = 4, SCANCODE = 8;

    // ---------------------------------------------------------------- PIN storage (machine-scope DPAPI, in a folder only SYSTEM/Admins can open)
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StreamDeckHost.unlock.v1");

    static void SavePin(string pin, bool enter)
    {
        Directory.CreateDirectory(UnlockPaths.SecretDir);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(new JsonObject { ["pin"] = pin, ["enter"] = enter }.ToJsonString()), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(UnlockPaths.PinFile, blob);
    }

    static (string pin, bool enter)? LoadPin()
    {
        try
        {
            var j = JsonNode.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(UnlockPaths.PinFile), Entropy, DataProtectionScope.LocalMachine)))!;
            return (j["pin"]!.GetValue<string>(), j["enter"]?.GetValue<bool>() ?? true);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- the helper (SYSTEM)
    public static int RunHelper()
    {
        SecurityIdentifier? user = null;
        try { user = new SecurityIdentifier(JsonNode.Parse(File.ReadAllText(UnlockPaths.Config))!["sid"]!.GetValue<string>()); } catch { }
        if (user == null) return 2;
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));    // only the Windows user who installed it
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        while (true)
        {
            try
            {
                var server = NamedPipeServerStreamAcl.Create(UnlockPaths.Pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, ps);
                server.WaitForConnection();
                _ = Task.Run(() => Serve(server));
            }
            catch { Thread.Sleep(500); }
        }
    }

    static void Serve(NamedPipeServerStream s)
    {
        using (s)
        {
            JsonObject res;
            try
            {
                var rd = new StreamReader(s, new UTF8Encoding(false), false, 1024, true); var wr = new StreamWriter(s, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                var line = rd.ReadLine();
                var req = line != null ? JsonNode.Parse(line)!.AsObject() : new JsonObject();
                res = Handle(req);
                wr.WriteLine(res.ToJsonString());
            }
            catch { }
        }
    }

    static JsonObject Handle(JsonObject req)
    {
        var cmd = req["cmd"]?.GetValue<string>() ?? "";
        switch (cmd)
        {
            case "status":
            {
                var p = LoadPin();
                return new JsonObject { ["ok"] = true, ["hasPin"] = p != null, ["enter"] = p?.enter ?? true, ["locked"] = IsConsoleLocked() };
            }
            case "setpin":
            {
                var pin = req["pin"]?.GetValue<string>() ?? "";
                if (pin.Length < 1 || pin.Length > 64) return new JsonObject { ["ok"] = false, ["error"] = "PIN must be 1-64 characters" };
                SavePin(pin, req["enter"]?.GetValue<bool>() ?? true);
                return new JsonObject { ["ok"] = true };
            }
            case "clearpin": try { File.Delete(UnlockPaths.PinFile); } catch { } return new JsonObject { ["ok"] = true };
            case "unlock":
            {
                if (LoadPin() == null) return new JsonObject { ["ok"] = false, ["error"] = "No PIN saved yet (PC app > Unlock)." };
                if (!IsConsoleLocked()) return new JsonObject { ["ok"] = false, ["error"] = "The PC is not locked." };
                var code = RunTyperInConsoleSession(out var err);
                return code switch
                {
                    0 => new JsonObject { ["ok"] = true },
                    3 => new JsonObject { ["ok"] = false, ["error"] = "The PC is not on the lock screen." },
                    _ => new JsonObject { ["ok"] = false, ["error"] = err ?? ("Could not type the PIN (code " + code + ")") },
                };
            }
            default: return new JsonObject { ["ok"] = false, ["error"] = "unknown command" };
        }
    }

    /// <summary>True when the active console session shows the lock screen (WTS session flags: 0 = locked).</summary>
    static bool IsConsoleLocked()
    {
        try
        {
            uint sid = WTSGetActiveConsoleSessionId(); if (sid == 0xFFFFFFFF) return false;
            if (!WTSQuerySessionInformation(IntPtr.Zero, sid, 25 /* WTSSessionInfoEx */, out var buf, out _)) return false;
            try
            {
                // WTSINFOEX: DWORD Level (+4 padding, the level struct is 8-byte aligned), then SessionId, SessionState, SessionFlags
                int flags = Marshal.ReadInt32(buf, 8 + 4 + 4);
                return flags == 0;
            }
            finally { WTSFreeMemory(buf); }
        }
        catch { return false; }
    }

    /// <summary>Starts "--unlock-type" with this process's SYSTEM token inside the logged-in session, on the Winlogon desktop.</summary>
    static int RunTyperInConsoleSession(out string? err)
    {
        err = null;
        IntPtr tok = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            uint sid = WTSGetActiveConsoleSessionId(); if (sid == 0xFFFFFFFF) { err = "No user session."; return 1; }
            if (!OpenProcessToken(GetCurrentProcess(), 0xF01FF, out tok)) { err = "token: " + Marshal.GetLastWin32Error(); return 1; }
            if (!DuplicateTokenEx(tok, 0x2000000, IntPtr.Zero, 2, 1, out dup)) { err = "dup: " + Marshal.GetLastWin32Error(); return 1; }
            if (!SetTokenInformation(dup, 12 /* TokenSessionId */, ref sid, 4)) { err = "session: " + Marshal.GetLastWin32Error(); return 1; }
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\winlogon" };
            var cmd = $"\"{Environment.ProcessPath}\" --unlock-type";
            if (!CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x08000400 /* NO_WINDOW | UNICODE_ENV */, IntPtr.Zero, Path.GetDirectoryName(Environment.ProcessPath), ref si, out var pi))
            { err = "start: " + Marshal.GetLastWin32Error(); return 1; }
            try
            {
                if (WaitForSingleObject(pi.hProcess, 25000) != 0) { err = "Timed out typing the PIN."; return 1; }
                GetExitCodeProcess(pi.hProcess, out var code); return (int)code;
            }
            finally { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
        }
        finally { if (dup != IntPtr.Zero) CloseHandle(dup); if (tok != IntPtr.Zero) CloseHandle(tok); }
    }

    // ---------------------------------------------------------------- the typer (SYSTEM, on the lock-screen desktop)
    static void Tap(ushort vk)
    {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        var a = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = SCANCODE } } };
        var b = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = SCANCODE | KEYUP } } };
        SendInput(2, new[] { a, b }, Marshal.SizeOf<INPUT>());
    }

    static void TapChar(char c)
    {
        if (c is >= '0' and <= '9') { Tap((ushort)c); return; }
        var a = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = UNICODE } } };
        var b = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = UNICODE | KEYUP } } };
        SendInput(2, new[] { a, b }, Marshal.SizeOf<INPUT>());
    }

    public static int RunTyper()
    {
        try
        {
            var desk = OpenInputDesktop(0, false, 0x10000000 /* GENERIC_ALL */);
            if (desk == IntPtr.Zero) return 4;
            var sb = new StringBuilder(256);
            if (!GetUserObjectInformation(desk, 2 /* UOI_NAME */, sb, sb.Capacity * 2, out _) || !sb.ToString().Equals("Winlogon", StringComparison.OrdinalIgnoreCase)) { CloseDesktop(desk); return 3; }
            SetThreadDesktop(desk);
            var p = LoadPin(); if (p == null) return 5;
            Tap(0x10); Thread.Sleep(350); Tap(0x10);        // wake the lock screen (Shift types nothing)
            Thread.Sleep(1700);                              // sign-in screen slides in
            foreach (var c in p.Value.pin) { TapChar(c); Thread.Sleep(70); }
            if (p.Value.enter) { Thread.Sleep(250); Tap(0x0D); }
            return 0;
        }
        catch { return 1; }
    }

    // ---------------------------------------------------------------- install / remove (run elevated)
    static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(30000);
    }

    public static int RunInstall(string userSid)
    {
        try
        {
            try { new SecurityIdentifier(userSid); } catch { return 2; }
            Run("schtasks.exe", $"/End /TN \"{UnlockPaths.Task}\"");
            foreach (var p in Process.GetProcessesByName("StreamDeckHost")) { try { if (string.Equals(p.MainModule?.FileName, UnlockPaths.Exe, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { } }
            Thread.Sleep(800);
            Directory.CreateDirectory(UnlockPaths.SecretDir);
            File.Copy(Environment.ProcessPath!, UnlockPaths.Exe, true);
            File.WriteAllText(UnlockPaths.Config, new JsonObject { ["sid"] = userSid }.ToJsonString());
            // folder rights: SYSTEM + Administrators full; Users may only read and run the program. The PIN folder is closed to Users.
            Run("icacls.exe", $"\"{UnlockPaths.Dir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX");
            Run("icacls.exe", $"\"{UnlockPaths.SecretDir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F");
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Lets the StreamDeck phone deck unlock this PC. Types the saved PIN on the lock screen.</Description></RegistrationInfo>
  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>99</Count></RestartOnFailure>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{UnlockPaths.Exe}</Command><Arguments>--unlock-helper</Arguments></Exec></Actions>
</Task>";
            var tmp = Path.Combine(Path.GetTempPath(), "sdh-unlock-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            Run("schtasks.exe", $"/Create /TN \"{UnlockPaths.Task}\" /XML \"{tmp}\" /F");
            File.Delete(tmp);
            Run("schtasks.exe", $"/Run /TN \"{UnlockPaths.Task}\"");
            return 0;
        }
        catch { return 1; }
    }

    public static int RunUninstall()
    {
        try
        {
            Run("schtasks.exe", $"/End /TN \"{UnlockPaths.Task}\"");
            Run("schtasks.exe", $"/Delete /TN \"{UnlockPaths.Task}\" /F");
            foreach (var p in Process.GetProcessesByName("StreamDeckHost")) { try { if (string.Equals(p.MainModule?.FileName, UnlockPaths.Exe, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { } }
            Thread.Sleep(800);
            try { Directory.Delete(UnlockPaths.Dir, true); } catch { }
            return 0;
        }
        catch { return 1; }
    }
}

/// <summary>Used by the normal host to talk to the SYSTEM helper.</summary>
public static class UnlockClient
{
    static JsonObject Call(JsonObject req, int timeoutMs = 40000)
    {
        try
        {
            using var p = new NamedPipeClientStream(".", UnlockPaths.Pipe, PipeDirection.InOut);
            p.Connect(1500);
            var wr = new StreamWriter(p, new UTF8Encoding(false)) { AutoFlush = true }; var rd = new StreamReader(p, new UTF8Encoding(false));
            wr.WriteLine(req.ToJsonString());
            var t = rd.ReadLineAsync(); if (!t.Wait(timeoutMs)) return new JsonObject { ["ok"] = false, ["error"] = "The unlock helper did not answer." };
            return t.Result != null ? JsonNode.Parse(t.Result)!.AsObject() : new JsonObject { ["ok"] = false, ["error"] = "empty answer" };
        }
        catch { return new JsonObject { ["ok"] = false, ["installed"] = false, ["error"] = "The unlock helper is not installed or not running (PC app > Unlock)." }; }
    }

    public static JsonObject Status() => Call(new JsonObject { ["cmd"] = "status" }, 4000);
    public static string? SetPin(string pin, bool enter) { var r = Call(new JsonObject { ["cmd"] = "setpin", ["pin"] = pin, ["enter"] = enter }, 5000); return r["ok"]?.GetValue<bool>() == true ? null : r["error"]?.GetValue<string>() ?? "failed"; }
    public static void ClearPin() => Call(new JsonObject { ["cmd"] = "clearpin" }, 5000);

    public static void Unlock()
    {
        var r = Call(new JsonObject { ["cmd"] = "unlock" });
        if (r["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(r["error"]?.GetValue<string>() ?? "Unlock failed");
    }

    /// <summary>Starts the installer with the Windows admin prompt. Returns null on success, else a message.</summary>
    public static string? InstallElevated()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--install-unlock-helper {sid}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(60000);
            Thread.Sleep(1500);
            return p.ExitCode == 0 ? null : "Install failed (code " + p.ExitCode + ")";
        }
        catch (Exception) { return "Cancelled (the admin prompt was declined)."; }
    }

    public static string? UninstallElevated()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uninstall-unlock-helper") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(60000); return p.ExitCode == 0 ? null : "Remove failed (code " + p.ExitCode + ")";
        }
        catch (Exception) { return "Cancelled (the admin prompt was declined)."; }
    }
}
