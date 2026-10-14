using System.Diagnostics;
using HidSharp;
using HidSharp.Reports;

namespace BatteryTray;

internal enum DeviceKind { Headset, Mouse, Keyboard, Earbuds, Other }

/// One row in the popup: device type, name, battery percentage (null if unknown), charge state and a short note.
/// The second line in the panel has the same shape for every device: "[Charging · | Full · ]Connection"
/// Connection is always "Wireless" or "USB cable"; "Full" only makes sense while on the cable.
internal static class Notes
{
    public static string Wireless => L.T("Kablosuz", "Wireless");
    public static string Cable => L.T("USB kablo", "USB cable");

    public static string Make(string conn, bool charging, bool full = false) =>
        charging ? L.T("Şarj oluyor", "Charging") + $" · {conn}"
        : full && conn == Cable ? L.T("Dolu", "Full") + $" · {conn}"
        : conn;
}

/// Present = false: the device isn't on this PC and isn't shown in the panel.
/// Parts: for devices with more than one battery (AirPods: left, right, case). Drawn as separate columns.
/// Details: optional extra line under the note, e.g. a mouse's "1600 DPI (2/4) · 1000 Hz".
internal sealed record DeviceStatus(DeviceKind Kind, string Name, int? Percent, bool Charging, string Note,
    bool Wired = false, bool Present = true, IReadOnlyList<BatteryPart>? Parts = null, bool Approx = false,
    string? Details = null);

/// Approx: the value is approximate (AirPods advertise in 10 % steps), shown with "~" in the panel.
internal sealed record BatteryPart(string Label, int? Percent, bool Charging, bool Approx = false);

/// Device names are never hard-coded: the name the device reports over USB (product string) is used.
internal static class HidNames
{
    public static string Of(HidDevice d, string fallback)
    {
        try
        {
            var n = d.GetProductName()?.Trim();
            return string.IsNullOrEmpty(n) ? fallback : n;
        }
        catch { return fallback; }
    }
}

// ---------------------------------------------------------------------------
// ASUS ROG mice (VID 0x0B05) - protocol figured out by G-Helper / BatteryTool:
//  - Interface: usually "mi_00"
//  - Request (65 bytes): [0]=report id (0) [1]=0x12 [2]=0x07
//  - Response: [1]=0x12 [2]=0x07 [5]=battery % (x25 on some older models) [10]=charging (non-zero = charging)
//  - Response "FF AA" means the dongle answers but the mouse is asleep/off
// ---------------------------------------------------------------------------
internal static class RogMouse
{
    private sealed record Profile(int Pid, string Name, string Iface = "mi_00",
        byte ReportId = 0, int Len = 65, int Offset = 5, int Scale = 1);

    private static readonly Profile[] Mice =
    {
        new(0x197F, "ROG Gladius III Wireless"), new(0x197D, "ROG Gladius III (cable)"),
        new(0x1A72, "ROG Gladius III AimPoint"), new(0x1A70, "ROG Gladius III AimPoint (cable)"),
        new(0x1B0C, "ROG Gladius III EVA-02"), new(0x1B0A, "ROG Gladius III EVA-02 (cable)"),
        new(0x1ACE, "ROG Mouse (OMNI Receiver)", "mi_02&col03", 3, 64),
        new(0x1AD0, "ROG Harpe II", "mi_02&col03", 3, 64),
        new(0x1A1A, "ROG Chakram X"), new(0x1A18, "ROG Chakram X (cable)"),
        new(0x1A94, "ROG Harpe Ace Aim Lab"), new(0x1A92, "ROG Harpe Ace Aim Lab (cable)"),
        new(0x1A68, "ROG Keris AimPoint"), new(0x1A66, "ROG Keris AimPoint (cable)"),
        new(0x1979, "ROG Spatha X"), new(0x1977, "ROG Spatha X (cable)"),
        new(0x18A0, "ROG Gladius II Wireless", "mi_02", Scale: 25),
        new(0x1960, "ROG Keris Wireless", Scale: 25), new(0x195E, "ROG Keris (cable)", Scale: 25),
    };

    private static bool _loggedOnce;

    /// ROG mice also expose a keyboard interface (for macro keys); the keyboard code uses this to skip them
    /// (the Omni Receiver 0x1ACE is shared by mice and keyboards, so it doesn't count).
    public static bool IsKnownMouse(int pid) => pid != 0x1ACE && Mice.Any(m => m.Pid == pid);

    public static DeviceStatus Read()
    {
        var all = DeviceList.Local.GetHidDevices(0x0B05).ToList();
        if (!_loggedOnce)
        {
            _loggedOnce = true;
            foreach (var d in all) Log.W($"[ROG] PID=0x{d.ProductID:X4} {d.DevicePath}");
        }

        DeviceStatus? last = null;
        foreach (var dev in all)
        {
            var p = Mice.FirstOrDefault(m => m.Pid == dev.ProductID &&
                dev.DevicePath.Contains(m.Iface, StringComparison.OrdinalIgnoreCase));
            if (p is null) continue;

            var r = Query(dev, p);
            if (r.Percent is not null) return r;
            last = r;
        }

        if (last is not null) return last;

        // Only report "Unsupported model" for an ASUS device that really is a mouse:
        // it has a mouse interface and no keyboard interface (keyboards often expose a mouse collection too).
        var unknownMouse = all
            .GroupBy(d => d.ProductID)
            .FirstOrDefault(g => g.Any(d => HasUsage(d, MouseUsage)) && !g.Any(d => HasUsage(d, KeyboardUsage)));
        return unknownMouse is null
            ? new(DeviceKind.Mouse, L.T("Fare", "Mouse"), null, false, L.T("Bulunamadı", "Not found"), Present: false)
            : new(DeviceKind.Mouse, HidNames.Of(unknownMouse.First(), "ASUS mouse"), null, false, L.T("Desteklenmeyen model", "Unsupported model"));
    }

    private const uint MouseUsage = 0x00010002;      // Generic Desktop / Mouse
    private const uint KeyboardUsage = 0x00010006;   // Generic Desktop / Keyboard

    private static bool HasUsage(HidDevice d, uint usage)
    {
        try { return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Contains(usage); }
        catch { return false; }
    }

    private static DeviceStatus Query(HidDevice dev, Profile p)
    {
        string name = HidNames.Of(dev, p.Name);
        bool cable = p.Name.Contains("(cable)");
        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = dev.Open(cfg);

            // First drain any stale packets from the queue
            s.ReadTimeout = 20;
            var buf = new byte[Math.Max(256, dev.GetMaxInputReportLength())];
            var drain = Stopwatch.StartNew();
            for (int i = 0; i < 16 && drain.ElapsedMilliseconds < 100; i++)
            {
                try { s.Read(buf); } catch (TimeoutException) { break; }
            }

            var q = new byte[p.Len];
            q[0] = p.ReportId; q[1] = 0x12; q[2] = 0x07;
            s.ReadTimeout = 300;
            s.WriteTimeout = 500;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                s.Write(q);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 900)
                {
                    int n;
                    try { n = s.Read(buf); } catch (TimeoutException) { continue; }
                    if (n < 11 || buf[0] != p.ReportId) continue;

                    if (buf[1] == 0xFF && buf[2] == 0xAA)
                        return new(DeviceKind.Mouse, name, null, false, L.T("Uykuda / kapalı", "Asleep / off"));

                    if (buf[1] == 0x12 && buf[2] == 0x07)
                    {
                        int pct = buf[p.Offset] * p.Scale;
                        if (pct is > 0 and <= 100)
                        {
                            bool charging = buf[10] != 0;
                            string note = Notes.Make(cable ? Notes.Cable : Notes.Wireless, charging);
                            return new(DeviceKind.Mouse, name, pct, charging, note);
                        }
                    }
                }
            }
            return new(DeviceKind.Mouse, name, null, false, L.T("Cevap yok", "No response"));
        }
        catch (Exception ex)
        {
            Log.W($"[ROG] {name} error: {ex.Message}");
            return new(DeviceKind.Mouse, name, null, false, L.T("Okunamadı", "Read failed"));
        }
    }
}

// ---------------------------------------------------------------------------
// ASUS ROG wireless keyboards (VID 0x0B05) - protocol figured out by mikgangal/battery-tray
// (verified with ROG Strix Scope II 96 Wireless + Omni Receiver):
//  - Interface: "mi_02&col02", vendor usage page 0xFF00, report ID 0x02
//  - Request (64 bytes): [0x02, 0x12, 0x01, 0...]
//  - Response: [1]=0x12 [2]=0x01 (echo), [6]=battery %, [9]=charging (1 = charging)
// ROG Falchion answers the same command on "mi_01" (usage page 0xFF00, report ID 0x00, 65-byte reports)
// with a different layout: [9]=charging, [11]=battery %, [12..13]=battery voltage in mV (little-endian).
//   cable    (PID 0x193C): 00-12-01-00-00-00-0A-02-01-01-02-64-D0-10 -> charging, 100 %, 4304 mV
//   wireless (PID 0x193E): 00-12-01-00-00-00-08-02-01-00-02-50-85-0F -> on battery, 80 %, 3973 mV
// The layout is told apart by the voltage field: a plausible Li-ion voltage at [12..13] means the Falchion layout.
// To avoid accidentally sending commands to other ASUS devices (e.g. a motherboard RGB controller),
// only devices that have both this interface and a keyboard interface are queried.
// ---------------------------------------------------------------------------
internal static class AsusKeyboard
{
    private const uint KeyboardUsage = 0x00010006;   // Generic Desktop / Keyboard
    private static bool _loggedOnce;

    private static bool HasUsage(HidDevice d, Func<uint, bool> match)
    {
        try { return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Any(match); }
        catch { return false; }
    }

    private static bool _diagnosed;

    /// Diagnostics: logs ALL connected ASUS HID interfaces in detail and sends read-only battery
    /// queries to the vendor channels of devices that have a keyboard interface, logging the responses.
    /// Runs once automatically at startup, then via "Diagnostics" in the menu.
    public static void Diagnose()
    {
        Log.W("========== ASUS diagnostics started ==========");
        var all = DeviceList.Local.GetHidDevices(0x0B05).ToList();
        if (all.Count == 0) { Log.W("[ASUS diag] no ASUS HID devices"); return; }

        foreach (var d in all)
        {
            string usages = "?";
            string reports = "";
            try
            {
                var desc = d.GetReportDescriptor();
                usages = string.Join(",", desc.DeviceItems.SelectMany(i => i.Usages.GetAllValues())
                    .Distinct().Select(u => "0x" + u.ToString("X8")));
                reports = $" outIds=[{string.Join(",", desc.OutputReports.Select(r => r.ReportID))}]" +
                          $" inIds=[{string.Join(",", desc.InputReports.Select(r => r.ReportID))}]" +
                          $" featIds=[{string.Join(",", desc.FeatureReports.Select(r => r.ReportID))}]";
            }
            catch (Exception ex) { usages = "unreadable: " + ex.Message; }

            string mfr = "?";
            try { mfr = d.GetManufacturer(); } catch { }
            Log.W($"[ASUS diag] PID=0x{d.ProductID:X4} name=\"{HidNames.Of(d, "?")}\" manufacturer=\"{mfr}\" " +
                  $"in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} feat={d.GetMaxFeatureReportLength()}");
            Log.W($"            usages={usages}{reports}");
            Log.W($"            {d.DevicePath}");
        }

        // Only query the vendor (0xFFxx) interfaces of devices that also have a keyboard interface
        var keyboardPids = all.Where(d => HasUsage(d, u => u == KeyboardUsage)).Select(d => d.ProductID).ToHashSet();
        Log.W($"[ASUS diag] PIDs with a keyboard interface: {string.Join(",", keyboardPids.Select(p => "0x" + p.ToString("X4")))}");

        foreach (var d in all.Where(d => keyboardPids.Contains(d.ProductID) && d.GetMaxOutputReportLength() > 0
                                        && HasUsage(d, u => (u >> 16) >= 0xFF00)))
        {
            byte[] ids;
            try { ids = d.GetReportDescriptor().OutputReports.Select(r => r.ReportID).Distinct().ToArray(); }
            catch { ids = new byte[] { 0 }; }
            if (ids.Length == 0) ids = new byte[] { 0 };

            foreach (byte id in ids)
                foreach (byte sub in new byte[] { 0x01, 0x07 })   // 0x01: keyboard battery, 0x07: mouse battery
                    Probe(d, id, sub);
        }
        Log.W("========== ASUS diagnostics finished ==========");
    }

    private static void Probe(HidDevice d, byte reportId, byte sub)
    {
        string tag = $"[ASUS diag] PID=0x{d.ProductID:X4} {ShortPath(d)} id=0x{reportId:X2} cmd=12-{sub:X2}";
        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = d.Open(cfg);
            s.ReadTimeout = 20;
            for (int i = 0; i < 16; i++) { try { s.Read(); } catch { break; } }

            var q = new byte[d.GetMaxOutputReportLength()];
            q[0] = reportId; q[1] = 0x12; q[2] = sub;
            s.WriteTimeout = 500;
            s.Write(q);

            s.ReadTimeout = 300;
            var sw = Stopwatch.StartNew();
            int got = 0;
            while (sw.ElapsedMilliseconds < 1000 && got < 4)
            {
                try { Log.W($"{tag} -> {Log.Hex(s.Read(), 16)}"); got++; }
                catch (TimeoutException) { }
            }
            if (got == 0) Log.W($"{tag} -> (no response)");
        }
        catch (Exception ex) { Log.W($"{tag} -> error: {ex.Message}"); }
    }

    private static string ShortPath(HidDevice d)
    {
        // Extract the "...&mi_02&col02#..." part
        var p = d.DevicePath.ToLowerInvariant();
        int i = p.IndexOf("&mi_");
        if (i < 0) return "";
        int j = p.IndexOf('#', i);
        return j > i ? p[(i + 1)..j] : p[(i + 1)..];
    }

    public static DeviceStatus Read()
    {
        if (!_diagnosed) { _diagnosed = true; try { Diagnose(); } catch (Exception ex) { Log.W("[ASUS diag] error: " + ex); } }
        var all = DeviceList.Local.GetHidDevices(0x0B05).ToList();

        // ASUS devices that have a keyboard interface (by PID)
        var keyboardPids = all.Where(d => HasUsage(d, u => u == KeyboardUsage)).Select(d => d.ProductID).ToHashSet();

        // Vendor (0xFF00) channel with an output report on a device that is also a keyboard
        // ROG mice have a keyboard interface too (macro keys), so known mouse PIDs are skipped
        var candidates = all.Where(d =>
                keyboardPids.Contains(d.ProductID) &&
                !RogMouse.IsKnownMouse(d.ProductID) &&
                d.GetMaxOutputReportLength() > 0 &&
                HasUsage(d, u => (u >> 16) == 0xFF00))
            .ToList();

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            foreach (var d in candidates) Log.W($"[ASUS keyboard] candidate PID=0x{d.ProductID:X4} {d.DevicePath}");
        }

        DeviceStatus? last = null;
        foreach (var dev in candidates)
        {
            var r = Query(dev);
            if (r.Percent is not null) return r;
            last = r;
        }
        return last ?? new(DeviceKind.Keyboard, L.T("ASUS klavye", "ASUS keyboard"), null, false, L.T("Bulunamadı", "Not found"), Present: false);
    }

    private static DeviceStatus Query(HidDevice dev)
    {
        // Prefer the keyboard's own name over the receiver's ("ROG Omni Receiver") when available
        string name = HidNames.Of(dev, "ASUS keyboard");
        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = dev.Open(cfg);

            // Drain stale packets from the queue
            s.ReadTimeout = 20;
            var drain = Stopwatch.StartNew();
            for (int i = 0; i < 16 && drain.ElapsedMilliseconds < 100; i++)
            {
                try { s.Read(); } catch { break; }
            }

            // Report ID from the descriptor: 0x02 on the Omni Receiver, 0x00 on the Falchion
            byte id = 0x02;
            try
            {
                var ids = dev.GetReportDescriptor().OutputReports.Select(r => r.ReportID).ToList();
                if (ids.Count > 0 && !ids.Contains(0x02)) id = ids[0];
            }
            catch { }

            var q = new byte[Math.Max(dev.GetMaxOutputReportLength(), 64)];
            q[0] = id; q[1] = 0x12; q[2] = 0x01;
            s.ReadTimeout = 300;
            s.WriteTimeout = 500;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                s.Write(q);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 900)
                {
                    byte[] buf;
                    try { buf = s.Read(); } catch (TimeoutException) { continue; }
                    if (buf.Length < 10 || buf[0] != id) continue;
                    if (buf[1] == 0xFF && buf[2] == 0xAA)
                        return new(DeviceKind.Keyboard, name, null, false, L.T("Uykuda / kapalı", "Asleep / off"));
                    if (buf[1] != 0x12 || buf[2] != 0x01) continue;

                    int mv = buf.Length > 13 ? buf[12] | (buf[13] << 8) : 0;
                    bool falchionLayout = mv is >= 3000 and <= 4500 && buf[11] is > 0 and <= 100;
                    int pct = falchionLayout ? buf[11] : buf[6];
                    if (pct is <= 0 or > 100) continue;
                    bool charging = buf[9] == 1;
                    // Via a receiver (Omni Receiver / dongle) it's wireless; with the keyboard's own USB ID it's the cable
                    bool viaReceiver = dev.ProductID is 0x1ACE or 0x193E ||
                        name.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Dongle", StringComparison.OrdinalIgnoreCase);
                    return new(DeviceKind.Keyboard, name, pct, charging,
                        Notes.Make(viaReceiver ? Notes.Wireless : Notes.Cable, charging));
                }
            }
            return new(DeviceKind.Keyboard, name, null, false, L.T("Cevap yok", "No response"));
        }
        catch (Exception ex)
        {
            Log.W($"[ASUS keyboard] {name} error: {ex.Message}");
            return new(DeviceKind.Keyboard, name, null, false, L.T("Okunamadı", "Read failed"));
        }
    }
}

// ---------------------------------------------------------------------------
// Keychron K2 Max (VID 0x3434, 2.4 GHz receiver PID 0xD030)
// Stock firmware doesn't share the battery level over 2.4 GHz and the receiver doesn't forward
// requests from the PC to the keyboard. So the keyboard gets a patched QMK firmware
// that pushes this packet on the raw HID channel (0xFF60) every few seconds:
//
//   [0xA4, percent, voltage_lo, voltage_hi, charge (0 battery / 1 charging / 2 full), transport]
// The app listens on that channel in the background.
// ---------------------------------------------------------------------------
internal static class Keychron
{
    // The name comes from the device: the keyboard's own name on cable, the receiver's name on 2.4 GHz
    private static volatile string Name = "Keyboard";
    private static Thread? _listener;
    private static volatile bool _dongleFound;
    private static (int pct, int mv, int charge, DateTime at)? _last;
    private static readonly object _lock = new();

    public static DeviceStatus Read()
    {
        if (_listener is null)
        {
            _listener = new Thread(Listen) { IsBackground = true, Name = "KeychronListener" };
            _listener.Start();
        }

        // Cable mode: the keyboard shows up as a USB device of its own; ask it for the battery
        var wired = ReadWired();
        if (wired is not null) return wired;

        (int pct, int mv, int charge, DateTime at)? last;
        lock (_lock) last = _last;

        if (last is null)
            return _dongleFound
                ? new(DeviceKind.Keyboard, Name, null, false, L.T("Pil paketi bekleniyor", "Waiting for battery packet"))
                : new(DeviceKind.Keyboard, Name, null, false, L.T("Bulunamadı", "Not found"), Present: false);

        var v = last.Value;
        var age = DateTime.Now - v.at;
        bool charging = v.charge == 1;
        if (age.TotalSeconds > 60)
            // No packets arrive while the keyboard sleeps; show the last value with its time
            return new(DeviceKind.Keyboard, Name, v.pct, false, L.T("Uykuda · son ", "Asleep · last ") + $"{v.at:HH:mm}");

        return Build(v.pct, v.charge, Notes.Wireless);
    }

    private static bool _wiredLogged;

    // While charging, the battery voltage reads artificially high (~4.15 V) and shows as 100 %.
    // So while charging we show the last reliable reading taken before charging,
    // not the percentage derived from the voltage.
    // The last reliable (on-battery) reading is kept in a file so it survives app restarts
    private static readonly string StateFile = System.IO.Path.Combine(AppContext.BaseDirectory, "keychron_last.txt");
    private static (int pct, DateTime at)? _lastOnBattery = LoadLast();

    private static (int pct, DateTime at)? LoadLast()
    {
        try
        {
            var parts = File.ReadAllText(StateFile).Split('|');
            return (int.Parse(parts[0]), DateTime.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }

    private static void SaveLast(int pct, DateTime at)
    {
        try { File.WriteAllText(StateFile, $"{pct}|{at.ToString("o", System.Globalization.CultureInfo.InvariantCulture)}"); }
        catch { }
    }

    private static DeviceStatus Build(int pct, int charge, string conn)
    {
        if (charge == 0)
        {
            var now = DateTime.Now;
            lock (_lock) _lastOnBattery = (pct, now);
            SaveLast(pct, now);
        }

        if (charge == 2) return new(DeviceKind.Keyboard, Name, 100, false, Notes.Make(conn, false, full: true));
        if (charge == 1)
        {
            (int pct, DateTime at)? b;
            lock (_lock) b = _lastOnBattery;
            return b is null
                ? new(DeviceKind.Keyboard, Name, null, true, Notes.Make(conn, true))
                : new(DeviceKind.Keyboard, Name, b.Value.pct, true, Notes.Make(conn, true));
        }
        return new(DeviceKind.Keyboard, Name, pct, false, conn);
    }

    /// In cable mode, sends a 0xA4 (battery) request to the keyboard's own raw HID interface.
    private static DeviceStatus? ReadWired()
    {
        var dev = DeviceList.Local.GetHidDevices(0x3434)
            .Where(d => d.ProductID != 0xD030 && d.GetMaxOutputReportLength() > 0)
            .FirstOrDefault(IsRawHid);
        if (dev is null) return null;
        Name = HidNames.Of(dev, Name);

        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = dev.Open(cfg);
            s.ReadTimeout = 300;
            var q = new byte[Math.Max(dev.GetMaxOutputReportLength(), 33)];
            q[0] = 0x00; q[1] = 0xA4;
            s.Write(q);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                byte[] buf;
                try { buf = s.Read(); } catch (TimeoutException) { continue; }
                int i = buf.Length > 0 && buf[0] == 0xA4 ? 0 : buf.Length > 1 && buf[1] == 0xA4 ? 1 : -1;
                if (i < 0 || buf.Length < i + 6) continue;

                int pct = buf[i + 1], charge = buf[i + 4];
                if (!_wiredLogged)
                {
                    _wiredLogged = true;
                    Log.W($"[Keychron] cable mode PID=0x{dev.ProductID:X4}: {pct}%, charge={charge}, raw={Log.Hex(buf, 10)}");
                }
                if (pct is <= 0 or > 100) return new(DeviceKind.Keyboard, Name, null, false, Notes.Cable + L.T(" · pil okunamadı", " · battery unreadable"));
                // On the cable the percentage isn't reliable (charging voltage): show a plug icon instead
                return new(DeviceKind.Keyboard, Name, null, charge == 1, Notes.Cable, Wired: true);
            }
            return new(DeviceKind.Keyboard, Name, null, false, Notes.Cable + L.T(" · cevap yok (firmware?)", " · no response (firmware?)"));
        }
        catch (Exception ex)
        {
            Log.W("[Keychron] cable mode error: " + ex.Message);
            return null;
        }
    }

    private static bool IsRawHid(HidDevice d)
    {
        try
        {
            return d.GetReportDescriptor().DeviceItems
                .SelectMany(i => i.Usages.GetAllValues())
                .Any(u => (u >> 16) == 0xFF60);
        }
        catch { return false; }
    }

    private static void Listen()
    {
        while (true)
        {
            try
            {
                var dev = DeviceList.Local.GetHidDevices(0x3434)
                    .FirstOrDefault(d => d.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase));
                _dongleFound = dev is not null;
                if (dev is null) { Thread.Sleep(5000); continue; }
                Name = HidNames.Of(dev, Name);

                var cfg = new OpenConfiguration();
                cfg.SetOption(OpenOption.Exclusive, false);
                using var s = dev.Open(cfg);
                s.ReadTimeout = 15000;
                Log.W("[Keychron] listening on raw HID");
                bool firstLogged = false;

                while (true)
                {
                    byte[] buf;
                    try { buf = s.Read(); }   // IOException if the receiver is unplugged; the outer loop reconnects
                    catch (TimeoutException) { continue; }
                    catch (IOException ex) when (ex.Message.Contains("after some time")) { continue; }
                    // Report ID (0) may come first: look for 0xA4 in the first two bytes
                    int i = buf.Length > 0 && buf[0] == 0xA4 ? 0 : buf.Length > 1 && buf[1] == 0xA4 ? 1 : -1;
                    if (i < 0 || buf.Length < i + 6) continue;

                    int pct = buf[i + 1];
                    int mv = buf[i + 2] | (buf[i + 3] << 8);
                    int charge = buf[i + 4];
                    if (pct is < 0 or > 100) continue;

                    lock (_lock) _last = (pct, mv, charge, DateTime.Now);
                    if (!firstLogged)
                    {
                        firstLogged = true;
                        Log.W($"[Keychron] first battery packet: {pct}%, {mv} mV, charge={charge}, raw={Log.Hex(buf, 10)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.W("[Keychron] listener error: " + ex.Message);
                Thread.Sleep(5000);
            }
        }
    }
}
