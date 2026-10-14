using System.Diagnostics;
using HidSharp;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// ATK / VXE "Dragonfly R1" mice (R1, R1 SE+, R1 Pro, R1 Pro Max ...) - Compx-based chipset.
// The protocol was worked out by others; this file only reads (it never writes to the mouse):
//  - HaloBattery (VXE R1 Pro Max, verified on hardware), python-pulsar-mouse-tool (Wireshark capture of a sibling
//    Compx mouse, which documents the memory map) and OpenMouse's protocol notes (cross-check).
//
//  - Channel: vendor collection usage page 0xFF02 / usage 0x0002 with 17-byte output reports, report ID 0x08.
//    VID 0x3554 (Compx) or 0x373B (ATK); the PID differs per model and per connection (receiver / cable).
//  - Frame (17 bytes, same in both directions): [0]=0x08 [1]=command [4]=address [5]=length [6..15]=data [16]=checksum
//    checksum = 0x55 - sum(bytes 0..15). Every value stored in the mouse's memory is followed by its own complement
//    byte (value + checksum byte = 0x55), so a read that doesn't match the layout is detected and simply not shown.
//  - Command 0x04 (battery): reply [6]=percent, [7]=non-zero while USB power is attached, [8..9]=millivolts (big-endian)
//  - Command 0x08 (read memory): request [4]=address [5]=count (max 10); reply echoes both, data from [6]
//      0x00  polling-rate code (1 = 1000 Hz, 2 = 500, 4 = 250, 8 = 125)
//      0x02  number of DPI stages        0x04  active DPI stage (0-based)
//      0x0C + 4*stage  [x, y, mode, checksum]: DPI = ((value | (mode_nibble >> 2) << 8) + 1) * 50,
//                      x uses the low nibble of "mode", y the high nibble
// Battery is the verified part. The memory map above is documented for sibling firmware: if it doesn't match a
// given mouse, DPI / polling rate are left out instead of guessed. "Diagnostics" dumps the raw memory to the log.
// ---------------------------------------------------------------------------
internal static class AtkMouse
{
    private static readonly int[] Vids = { 0x3554, 0x373B };
    private const uint VendorUsage = 0xFF020002;   // usage page 0xFF02, usage 0x0002
    private const byte ReportId = 0x08;
    private const int FrameLen = 17;
    private const byte CmdPower = 0x04, CmdMemGet = 0x08;
    private const int ConfigBytes = 50;            // memory 0x00..0x31, read in blocks of 10

    /// Models seen in the wild. Anything else with the same vendor channel is still tried (new PIDs, other R1 variants).
    private sealed record Known(int Vid, int Pid, string Name, bool Wired);

    private static readonly Known[] Models =
    {
        new(0x3554, 0xF58A, "VXE R1 Pro Max", false), new(0x3554, 0xF58C, "VXE R1 Pro Max", true),
        new(0x373B, 0x1085, "VXE R1 SE+", false),
        new(0x3554, 0xF58E, "VXE R1 SE+", false),     // 1K receiver per OpenMouse; no battery report seen yet
        new(0x3554, 0xF58F, "VXE R1 SE+", true),
    };

    private static readonly object Gate = new();       // refresh and Diagnostics must not read the same channel at once
    private static readonly HashSet<int> _logged = new();
    private static readonly Dictionary<int, (DeviceStatus Status, DateTime At)> _lastGood = new();

    // ---- pure parsing (no I/O) --------------------------------------------------------------------------------

    internal static byte Checksum(byte[] f)
    {
        int sum = 0;
        for (int i = 0; i < FrameLen - 1; i++) sum += f[i];
        return (byte)(0x55 - sum);
    }

    internal static byte[] Frame(byte cmd, byte address = 0, byte count = 0)
    {
        var f = new byte[FrameLen];
        f[0] = ReportId; f[1] = cmd; f[4] = address; f[5] = count;
        f[FrameLen - 1] = Checksum(f);
        return f;
    }

    internal static bool IsReply(byte[] r, byte cmd) =>
        r.Length >= FrameLen && r[0] == ReportId && r[1] == cmd && r[FrameLen - 1] == Checksum(r);

    internal sealed record Power(int Percent, bool UsbPower, int Millivolts);

    internal static Power? ParsePower(byte[] r)
    {
        if (!IsReply(r, CmdPower) || r[6] > 100) return null;
        return new Power(r[6], r[7] != 0, (r[8] << 8) | r[9]);
    }

    /// A stored value is followed by its complement: the pair must add up to 0x55.
    private static int? Pair(byte[] m, int i) =>
        i >= 0 && i + 1 < m.Length && (byte)(m[i] + m[i + 1]) == 0x55 ? m[i] : null;

    internal sealed record Config(int? PollingHz, int? Dpi, int? DpiY, int? Stage, int? StageCount);

    internal static Config ParseConfig(byte[] m)
    {
        int? hz = Pair(m, 0x00) switch { 1 => 1000, 2 => 500, 4 => 250, 8 => 125, _ => null };
        int? count = Pair(m, 0x02) is > 0 and <= 8 and var c ? c : null;
        int? stage = Pair(m, 0x04) is int s && (count is null || s < count) ? s : null;

        (int x, int y)? dpi = stage is int idx ? DpiRow(m, 0x0C + 4 * idx) : null;
        return new Config(hz, dpi?.x, dpi?.y, stage, count);
    }

    /// One DPI stage: [x, y, mode, checksum]; the mode nibbles carry bits 8-9 of the value (x: low nibble, y: high nibble).
    internal static (int x, int y)? DpiRow(byte[] m, int i)
    {
        if (i < 0 || i + 3 >= m.Length || (byte)(m[i] + m[i + 1] + m[i + 2] + m[i + 3]) != 0x55) return null;
        int x = ((m[i] | (((m[i + 2] & 0x0F) >> 2) << 8)) + 1) * 50;
        int y = ((m[i + 1] | (((m[i + 2] >> 4) >> 2) << 8)) + 1) * 50;
        return x is >= 50 and <= 30000 && y is >= 50 and <= 30000 ? (x, y) : null;
    }

    /// Third line of the panel row, e.g. "1600 DPI (2/4) · 1000 Hz". null when nothing could be read.
    internal static string? Describe(Config c)
    {
        var parts = new List<string>();
        if (c.Dpi is int x)
        {
            string dpi = c.DpiY is int y && y != x ? $"{x}/{y} DPI" : $"{x} DPI";
            if (c.Stage is int s && c.StageCount is int n) dpi += $" ({s + 1}/{n})";
            parts.Add(dpi);
        }
        if (c.PollingHz is int hz) parts.Add($"{hz} Hz");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // ---- device discovery -------------------------------------------------------------------------------------

    private static bool HasVendorChannel(HidDevice d)
    {
        try { return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Contains(VendorUsage); }
        catch { return false; }
    }

    private static int OutLen(HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); }
        catch { return 0; }
    }

    private static Known? KnownOf(HidDevice d) => Models.FirstOrDefault(m => m.Vid == d.VendorID && m.Pid == d.ProductID);

    /// One channel per USB product (the receiver and the cable are different products; only one answers at a time).
    /// The channel is picked by its signature, so only devices that speak this protocol are ever written to.
    private static List<(HidDevice Dev, Known? Known)> Channels()
    {
        var all = Vids.SelectMany(v => DeviceList.Local.GetHidDevices(v)).ToList();
        var result = new List<(HidDevice, Known?)>();
        foreach (var g in all.GroupBy(d => (d.VendorID, d.ProductID)))
        {
            var dev = g.Where(d => OutLen(d) == FrameLen && HasVendorChannel(d)).FirstOrDefault();
            if (dev is not null) result.Add((dev, KnownOf(dev)));
        }
        // Cable first: while it is plugged in the receiver stays silent
        return result.OrderBy(r => r.Item2?.Wired == true ? 0 : 1).ToList();
    }

    /// The receiver calls itself "... dongle"; that isn't a name for the panel, so fall back to the model name.
    private static string NameOf(HidDevice d, Known? k)
    {
        string n = HidNames.Of(d, "");
        bool generic = n.Length == 0 || n.Contains("dongle", StringComparison.OrdinalIgnoreCase)
                                     || n.Contains("receiver", StringComparison.OrdinalIgnoreCase);
        return generic ? k?.Name ?? "ATK mouse" : n;
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    public static DeviceStatus Read()
    {
        lock (Gate)
        {
            var channels = Channels();
            DeviceStatus? last = null;
            foreach (var (dev, known) in channels)
            {
                var r = Query(dev, known);
                if (r.Percent is not null) return r;
                last = r;
            }
            return last ?? new(DeviceKind.Mouse, L.T("Fare", "Mouse"), null, false, L.T("Bulunamadı", "Not found"), Present: false);
        }
    }

    private static DeviceStatus Query(HidDevice dev, Known? known)
    {
        string name = NameOf(dev, known);
        try
        {
            using var s = Open(dev);
            Drain(s);

            var power = Exchange(s, Frame(CmdPower), CmdPower, attempts: 2) is { } pr ? ParsePower(pr) : null;
            if (power is null)
            {
                // Mice sleep after a while and stop answering: keep showing the last reading, with its time
                if (_lastGood.TryGetValue(dev.ProductID, out var g))
                    return g.Status with { Charging = false, Note = L.T("Uykuda · son ", "Asleep · last ") + $"{g.At:HH:mm}" };
                return new(DeviceKind.Mouse, name, null, false, L.T("Cevap yok", "No response"));
            }

            var cfg = ReadConfig(s);
            // A known PID tells receiver from cable; for new ones, USB power attached means the cable
            bool cable = known?.Wired ?? power.UsbPower;
            bool full = power.UsbPower && power.Percent >= 100;
            bool charging = power.UsbPower && !full;
            var st = new DeviceStatus(DeviceKind.Mouse, name, power.Percent, charging,
                Notes.Make(cable ? Notes.Cable : Notes.Wireless, charging, full), Details: Describe(cfg));

            if (_logged.Add(dev.ProductID))
                Log.Debug($"[ATK] {name} PID=0x{dev.ProductID:X4}: {power.Percent}%, {power.Millivolts} mV, usbPower={power.UsbPower}, " +
                      $"config: dpi={cfg.Dpi} stage={cfg.Stage}/{cfg.StageCount} polling={cfg.PollingHz} {dev.DevicePath}");
            _lastGood[dev.ProductID] = (st, DateTime.Now);
            return st;
        }
        catch (Exception ex)
        {
            Log.W($"[ATK] {name} error: {ex.Message}");
            return new(DeviceKind.Mouse, name, null, false, L.T("Okunamadı", "Read failed"));
        }
    }

    /// Memory 0x00..0x31 in blocks of 10; blocks that don't answer stay zero, which fails the checksum pairs.
    private static Config ReadConfig(HidStream s)
    {
        var mem = ReadMemory(s, ConfigBytes);
        return ParseConfig(mem);
    }

    private static byte[] ReadMemory(HidStream s, int length)
    {
        var mem = new byte[length];
        for (int addr = 0; addr < length; addr += 10)
        {
            var r = Exchange(s, Frame(CmdMemGet, (byte)addr, 10), CmdMemGet, attempts: 1, address: (byte)addr);
            if (r is null) break;
            Array.Copy(r, 6, mem, addr, Math.Min(10, length - addr));
        }
        return mem;
    }

    private static HidStream Open(HidDevice dev)
    {
        var cfg = new OpenConfiguration();
        cfg.SetOption(OpenOption.Exclusive, false);
        var s = dev.Open(cfg);
        s.ReadTimeout = 150;
        s.WriteTimeout = 500;
        return s;
    }

    private static void Drain(HidStream s)
    {
        int old = s.ReadTimeout;
        s.ReadTimeout = 20;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 16 && sw.ElapsedMilliseconds < 100; i++)
        {
            try { s.Read(); } catch { break; }
        }
        s.ReadTimeout = old;
    }

    /// One request / reply. The mouse also pushes unsolicited events (command 0x0A), so keep reading until our reply shows up.
    private static byte[]? Exchange(HidStream s, byte[] request, byte cmd, int attempts, byte? address = null)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            s.Write(request);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 450)
            {
                byte[] r;
                try { r = s.Read(); }
                catch (TimeoutException) { continue; }
                catch (IOException ex) when (ex.Message.Contains("after some time")) { continue; }
                if (!IsReply(r, cmd)) continue;
                if (address is byte a && r[4] != a) continue;
                return r;
            }
        }
        return null;
    }

    // ---- diagnostics ------------------------------------------------------------------------------------------

    /// Logs what was found and a raw dump of the mouse's memory (read-only). If DPI / polling rate don't show up
    /// for a model, this log is what's needed to add its memory layout.
    public static void Diagnose()
    {
        lock (Gate)
        {
            Log.W("========== ATK/VXE diagnostics started ==========");
            var channels = Channels();
            if (channels.Count == 0)
                Log.W("[ATK diag] no HID device with the 0xFF02 vendor channel (17-byte output) on VID 0x3554 / 0x373B");

            foreach (var (dev, known) in channels)
            {
                string tag = $"[ATK diag] VID=0x{dev.VendorID:X4} PID=0x{dev.ProductID:X4} \"{HidNames.Of(dev, "?")}\"" +
                             $"{(known is null ? " (unlisted model)" : " (" + known.Name + (known.Wired ? ", cable)" : ")"))}";
                Log.W($"{tag} {dev.DevicePath}");
                try
                {
                    using var s = Open(dev);
                    Drain(s);

                    var pr = Exchange(s, Frame(CmdPower), CmdPower, attempts: 2);
                    Log.W(pr is null ? $"{tag} battery -> (no response; asleep or different protocol)"
                                     : $"{tag} battery -> {Log.Hex(pr, FrameLen)}");
                    if (pr is null) continue;

                    var mem = ReadMemory(s, 0xCA);   // 0x00..0xC9: settings, DPI stages, buttons ...
                    for (int a = 0; a < mem.Length; a += 10)
                        Log.W($"{tag} mem 0x{a:X2}: {Log.Hex(mem[a..Math.Min(a + 10, mem.Length)], 10)}");
                    var cfg = ParseConfig(mem);
                    Log.W($"{tag} parsed: dpi={cfg.Dpi}/{cfg.DpiY} stage={cfg.Stage}/{cfg.StageCount} polling={cfg.PollingHz} Hz");
                }
                catch (Exception ex) { Log.W($"{tag} error: {ex.Message}"); }
            }
            Log.W("========== ATK/VXE diagnostics finished ==========");
        }
    }
}
