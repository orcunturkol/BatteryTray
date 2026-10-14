using System.Diagnostics;
using HidSharp;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// Logitech wireless devices (VID 0x046D) over HID++ 2.0 - the protocol documented by Solaar / libratbag:
//  - Channel: the "long" HID++ collection (vendor usage page 0xFF00, usage 0x0002), 20-byte reports, report ID 0x11
//  - Request:  [0x11, deviceIndex, featureIndex, (function << 4) | swId, params...]
//    deviceIndex = 1..6 behind a receiver (Lightspeed / Unifying / Bolt), 0xFF for a directly connected device
//  - Feature indices differ per device; ask the root feature (index 0, function 0 = getFeature) for them
//  - Battery features, in order of preference:
//      0x1004 UNIFIED_BATTERY  getStatus:      [0]=state of charge %, [1]=level bits (1 critical, 2 low, 4 good, 8 full), [2]=charging status
//      0x1000 BATTERY_STATUS   getLevel:       [0]=level %, [2]=status (1..3 = charging / full)
//      0x1001 BATTERY_VOLTAGE  getBatteryInfo: [0..1]=millivolts (big-endian), [2]=flags (bit 7 = charging)
//  - 0x0005 DEVICE_NAME gives the device's own name and type, so nothing is hard-coded
// Devices that report only a voltage or a level (e.g. G305 on an AA battery) are shown as approximate (~).
// ---------------------------------------------------------------------------
internal static class Logitech
{
    private const int Vid = 0x046D;
    private const byte SwId = 0x0A;
    private const uint HidppLongUsage = 0xFF000002;

    private const ushort FeatName = 0x0005, FeatBattStatus = 0x1000, FeatBattVoltage = 0x1001, FeatUnified = 0x1004;

    private sealed record Found(string Path, byte Index, string Name, DeviceKind Kind, bool ViaReceiver,
        ushort BattFeature, byte BattIndex);

    private static List<Found> _found = new();
    private static string _pathsKey = "";
    private static int _sinceScan;
    private static readonly Dictionary<string, DeviceStatus> _lastGood = new();
    private static readonly Dictionary<string, string> _lastRaw = new();

    /// All Logitech HID++ devices with a battery feature. Rescans when the USB device set changes
    /// and every ~5 minutes (to pick up devices that were asleep during the previous scan).
    public static IReadOnlyList<DeviceStatus> Read()
    {
        var channels = DeviceList.Local.GetHidDevices(Vid).Where(IsHidppLong).ToList();
        if (channels.Count == 0) { _found.Clear(); _pathsKey = ""; return Array.Empty<DeviceStatus>(); }

        string key = string.Join("|", channels.Select(d => d.DevicePath).OrderBy(p => p));
        if (key != _pathsKey || ++_sinceScan >= (_found.Count == 0 ? 3 : 10))
        {
            _pathsKey = key;
            _sinceScan = 0;
            _found = Scan(channels);
        }

        var result = new List<DeviceStatus>();
        foreach (var f in _found)
        {
            var dev = channels.FirstOrDefault(d => d.DevicePath == f.Path);
            if (dev is null) continue;
            result.Add(ReadBattery(dev, f));
        }
        return result;
    }

    private static bool IsHidppLong(HidDevice d)
    {
        try
        {
            if (d.GetMaxOutputReportLength() != 20) return false;
            return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Contains(HidppLongUsage);
        }
        catch { return false; }
    }

    private static bool IsReceiver(HidDevice d)
    {
        string n = HidNames.Of(d, "");
        return n.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("Lightspeed", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("Unifying", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("Bolt", StringComparison.OrdinalIgnoreCase) ||
               d.ProductID is 0xC52B or 0xC532 or 0xC534 or 0xC539 or 0xC53A or 0xC53D or 0xC53F
                           or 0xC541 or 0xC545 or 0xC547 or 0xC548 or 0xC54D;
    }

    private static List<Found> Scan(List<HidDevice> channels)
    {
        var list = new List<Found>();
        foreach (var dev in channels)
        {
            bool receiver = IsReceiver(dev);
            Log.Debug($"[Logitech] HID++ channel PID=0x{dev.ProductID:X4} \"{HidNames.Of(dev, "?")}\" receiver={receiver}");
            try
            {
                using var s = Open(dev);
                var indices = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
                foreach (byte idx in indices)
                {
                    // Root getFeature doubles as a ping: devices that are off/asleep don't answer
                    byte batt = 0; ushort battFeat = 0;
                    foreach (var fid in new[] { FeatUnified, FeatBattStatus, FeatBattVoltage })
                    {
                        var r = GetFeatureIndex(s, idx, fid);
                        if (r is null) break;          // no answer: nothing at this index
                        if (r.Value != 0) { batt = r.Value; battFeat = fid; break; }
                    }
                    if (batt == 0) continue;

                    var (name, kind) = GetName(s, idx);
                    name ??= HidNames.Of(dev, "Logitech");
                    Log.Debug($"[Logitech] index {idx}: \"{name}\" ({kind}), battery feature 0x{battFeat:X4} at index {batt}");
                    list.Add(new Found(dev.DevicePath, idx, name, kind, receiver, battFeat, batt));
                }
            }
            catch (Exception ex) { Log.W($"[Logitech] scan error PID=0x{dev.ProductID:X4}: {ex.Message}"); }
        }
        return list;
    }

    private static DeviceStatus ReadBattery(HidDevice dev, Found f)
    {
        string conn = f.ViaReceiver ? Notes.Wireless : Notes.Cable;
        string key = f.Path + "#" + f.Index;
        try
        {
            using var s = Open(dev);
            byte func = f.BattFeature == FeatUnified ? (byte)1 : (byte)0;
            var r = Call(s, f.Index, f.BattIndex, func);
            if (r is null)
            {
                // The device is asleep (mice sleep after a few seconds of inactivity): show the last reading
                return _lastGood.TryGetValue(key, out var last)
                    ? last with { Charging = false, Note = L.T("Uykuda · son ", "Asleep · last ") + LastTime(key) }
                    : new(f.Kind, f.Name, null, false, L.T("Uykuda / kapalı", "Asleep / off"));
            }

            string raw = Log.Hex(r, 10);
            if (!_lastRaw.TryGetValue(key, out var prevRaw) || prevRaw != raw)
            {
                _lastRaw[key] = raw;
                Log.Debug($"[Logitech] {f.Name} feature 0x{f.BattFeature:X4} -> {raw}");
            }

            DeviceStatus st = f.BattFeature switch
            {
                FeatUnified => FromUnified(f, r, conn),
                FeatBattStatus => FromStatus(f, r, conn),
                _ => FromVoltage(f, r, conn),
            };
            if (st.Percent is not null) { _lastGood[key] = st; _lastAt[key] = DateTime.Now; }
            return st;
        }
        catch (Exception ex)
        {
            Log.W($"[Logitech] {f.Name} error: {ex.Message}");
            return new(f.Kind, f.Name, null, false, L.T("Okunamadı", "Read failed"));
        }
    }

    private static readonly Dictionary<string, DateTime> _lastAt = new();
    private static string LastTime(string key) => _lastAt.TryGetValue(key, out var t) ? $"{t:HH:mm}" : "?";

    // 0x1004: exact percentage if the device has one, otherwise just a level
    private static DeviceStatus FromUnified(Found f, byte[] r, string conn)
    {
        int soc = r[4], levels = r[5], chg = r[6];
        bool charging = chg is 1 or 2;
        bool full = chg == 3;
        if (soc is > 0 and <= 100)
            return new(f.Kind, f.Name, soc, charging, Notes.Make(conn, charging, full));

        (int pct, string tr, string en) = (levels & 8) != 0 ? (100, "Dolu", "Full")
                         : (levels & 4) != 0 ? (50, "İyi", "Good")
                         : (levels & 2) != 0 ? (20, "Düşük", "Low")
                         : (levels & 1) != 0 ? (5, "Kritik", "Critical")
                         : (0, "", "");
        if (pct == 0) return new(f.Kind, f.Name, null, charging, Notes.Make(conn, charging, full));
        return new(f.Kind, f.Name, pct, charging, L.T(tr, en) + " · " + Notes.Make(conn, charging, full), Approx: true);
    }

    // 0x1000: percentage (often in coarse steps) + status
    private static DeviceStatus FromStatus(Found f, byte[] r, string conn)
    {
        int lvl = r[4], status = r[6];
        bool charging = status is 1 or 2;
        bool full = status == 3;
        if (lvl is <= 0 or > 100) return new(f.Kind, f.Name, null, charging, Notes.Make(conn, charging, full));
        return new(f.Kind, f.Name, lvl, charging, Notes.Make(conn, charging, full), Approx: true);
    }

    // 0x1001: millivolts only. One AA cell (~0.9-1.6 V) or a Li-ion cell (~3.5-4.2 V); the % is an estimate.
    private static DeviceStatus FromVoltage(Found f, byte[] r, string conn)
    {
        int mv = (r[4] << 8) | r[5];
        bool charging = (r[6] & 0x80) != 0;
        int? pct = mv switch
        {
            <= 0 => null,
            < 2500 => Interp(mv, AaCurve),
            _ => Interp(mv, LiIonCurve),
        };
        return new(f.Kind, f.Name, pct, charging, Notes.Make(conn, charging), Approx: true);
    }

    // (millivolts, percent), highest first
    private static readonly (int mv, int pct)[] AaCurve =
        { (1500, 100), (1450, 90), (1400, 75), (1350, 60), (1300, 45), (1250, 30), (1200, 20), (1150, 10), (1100, 5), (1000, 0) };
    private static readonly (int mv, int pct)[] LiIonCurve =
        { (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3500, 0) };

    private static int Interp(int mv, (int mv, int pct)[] t)
    {
        if (mv >= t[0].mv) return t[0].pct;
        for (int i = 1; i < t.Length; i++)
            if (mv >= t[i].mv)
            {
                var (hi, lo) = (t[i - 1], t[i]);
                return lo.pct + (mv - lo.mv) * (hi.pct - lo.pct) / (hi.mv - lo.mv);
            }
        return 0;
    }

    private static (string? name, DeviceKind kind) GetName(HidStream s, byte idx)
    {
        try
        {
            var fi = GetFeatureIndex(s, idx, FeatName);
            if (fi is null or 0) return (null, DeviceKind.Mouse);
            var cnt = Call(s, idx, fi.Value, 0);
            if (cnt is null) return (null, DeviceKind.Mouse);
            int len = cnt[4];
            var chars = new List<byte>();
            while (chars.Count < len)
            {
                var part = Call(s, idx, fi.Value, 1, (byte)chars.Count);
                if (part is null) break;
                for (int i = 4; i < 20 && chars.Count < len; i++) chars.Add(part[i]);
            }
            string? name = chars.Count > 0 ? System.Text.Encoding.UTF8.GetString(chars.ToArray()).TrimEnd('\0', ' ') : null;

            var type = Call(s, idx, fi.Value, 2);
            DeviceKind kind = type?[4] switch
            {
                0 or 2 => DeviceKind.Keyboard,
                3 or 4 or 5 => DeviceKind.Mouse,
                8 => DeviceKind.Headset,
                _ => DeviceKind.Other,
            };
            return (string.IsNullOrWhiteSpace(name) ? null : name, kind);
        }
        catch { return (null, DeviceKind.Mouse); }
    }

    /// Root feature, getFeature(featureId): feature index, 0 if unsupported, null if the device didn't answer.
    private static byte? GetFeatureIndex(HidStream s, byte idx, ushort featureId)
    {
        var r = Call(s, idx, 0, 0, (byte)(featureId >> 8), (byte)featureId);
        return r?[4];
    }

    /// One HID++ 2.0 request/response. null = no answer (timeout) or an error reply.
    private static byte[]? Call(HidStream s, byte idx, byte feature, byte function, params byte[] args)
    {
        var q = new byte[20];
        q[0] = 0x11; q[1] = idx; q[2] = feature; q[3] = (byte)((function << 4) | SwId);
        Array.Copy(args, 0, q, 4, Math.Min(args.Length, 16));
        s.Write(q);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            byte[] r;
            try { r = s.Read(); }
            catch (TimeoutException) { continue; }
            catch (IOException ex) when (ex.Message.Contains("after some time")) { continue; }
            if (r.Length < 7 || r[0] != 0x11 || r[1] != idx) continue;
            if (r[2] == 0xFF && r[3] == feature && r[4] == q[3]) return null;   // HID++ 2.0 error reply
            if (r[2] == feature && r[3] == q[3]) return r;
        }
        return null;
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
}
