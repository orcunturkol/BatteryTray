using System.Diagnostics;
using HidSharp;
using Microsoft.Win32;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// ROG Azoth keyboard lighting (VID 0x0B05), no Armoury Crate needed.
// The protocol was worked out by the G-Helper project (MIT); this file only sends what is listed here:
//   12 00        probe (read-only): a keyboard answers [1]=0x12 [2]=0x00 [5]!=0xFA
//   51 2C ...    set the lighting (applies immediately)
//   50 55        save the current lighting on the keyboard, so it stays after BatteryTray is closed
// Lighting packet (19 bytes, zero-padded to the channel's report length):
//   [0]=report ID [1]=0x51 [2]=0x2C [3]=effect [5]=speed [6]=brightness 0..100 [7]=1 when the colour is black
//   (= random colours) [9]=0x02 [10..12]=colour 1 (R,G,B); colour 2 and 3 stay black
//   speed: slow 0xFF, normal 0x1E, fast 0x0A (a smaller number is faster)
// Channels, as in G-Helper:
//   cable:        PID of the keyboard, interface "mi_01", vendor page 0xFF00, report ID from the descriptor (0x00)
//   Omni receiver: PID 0x1ACE,         interface "mi_02&col02", vendor page 0xFF00, report ID 0x02
// Bluetooth has no vendor channel on Windows, so it can't be controlled.
// Not offered: Wave / Ripple / Quicksand. The Azoth sends those in several chunks and the chunk contents aren't known.
// ---------------------------------------------------------------------------

/// Effect codes of the 0x51 0x2C packet. Only these are ever sent.
internal enum AuraEffect { Static = 0, Breathing = 1, ColorCycle = 2, Reactive = 3, StarryNight = 6, RainDrop = 9 }

internal enum AuraSpeed { Slow, Normal, Fast }

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public bool IsBlack => R == 0 && G == 0 && B == 0;
    public int ToInt() => (R << 16) | (G << 8) | B;
    public static Rgb FromInt(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}

internal sealed record LightingSettings(AuraEffect Effect, Rgb Color, int Brightness, AuraSpeed Speed, bool SaveToKeyboard)
{
    public static LightingSettings Default => new(AuraEffect.Static, new Rgb(255, 255, 255), 100, AuraSpeed.Normal, true);

    /// Colour is ignored by the colour cycle; speed has no meaning for a static colour.
    public static bool UsesColor(AuraEffect e) => e != AuraEffect.ColorCycle;
    public static bool UsesSpeed(AuraEffect e) => e != AuraEffect.Static;
}

internal sealed record LightingResult(bool Ok, string Message);

internal static class AsusLighting
{
    private const int AsusVid = 0x0B05;
    private const int OmniPid = 0x1ACE;
    // ROG Azoth, Azoth Extreme, Azoth Extreme SE, Azoth X and their wireless identities
    private static readonly int[] AzothPids = { 0x1A83, 0x1B3F, 0x1CEF, 0x1C24, 0x1A85, 0x1C25 };

    // ---- packets (pure) ---------------------------------------------------------------------------------------

    internal static byte SpeedByte(AuraSpeed s) => s switch { AuraSpeed.Slow => 0xFF, AuraSpeed.Fast => 0x0A, _ => 0x1E };

    internal static byte[] BuildLightingPacket(byte reportId, LightingSettings s)
    {
        if (!Enum.IsDefined(s.Effect)) throw new ArgumentException($"Unknown effect {s.Effect}");
        var p = new byte[19];
        p[0] = reportId; p[1] = 0x51; p[2] = 0x2C;
        p[3] = (byte)s.Effect;
        p[5] = SpeedByte(s.Speed);
        p[6] = (byte)Math.Clamp(s.Brightness, 0, 100);
        p[7] = s.Color.IsBlack ? (byte)1 : (byte)0;   // black means "random colours"
        p[9] = 0x02;
        p[10] = s.Color.R; p[11] = s.Color.G; p[12] = s.Color.B;
        return p;
    }

    internal static byte[] BuildSavePacket(byte reportId) => new byte[] { reportId, 0x50, 0x55 };
    internal static byte[] BuildProbePacket(byte reportId) => new byte[] { reportId, 0x12, 0x00 };

    internal static bool IsProbeReply(byte[] r) => r.Length > 5 && r[1] == 0x12 && r[2] == 0x00 && r[5] != 0xFA;

    /// The keyboard's "no valid response" marker.
    internal static bool IsRejected(byte[]? r) => r is { Length: > 2 } && r[1] == 0xFF && r[2] == 0xAA;

    // ---- settings (remembered between runs) -----------------------------------------------------------------------

    internal static LightingSettings FromValues(string? effect, int? color, int? brightness, string? speed, int? save)
    {
        var d = LightingSettings.Default;
        return new LightingSettings(
            Enum.TryParse<AuraEffect>(effect, out var e) && Enum.IsDefined(e) ? e : d.Effect,
            color is int c ? Rgb.FromInt(c) : d.Color,
            brightness is int b ? Math.Clamp(b, 0, 100) : d.Brightness,
            Enum.TryParse<AuraSpeed>(speed, out var sp) && Enum.IsDefined(sp) ? sp : d.Speed,
            save is int sv ? sv != 0 : d.SaveToKeyboard);
    }

    private const string Key = @"Software\BatteryTray";

    public static LightingSettings Load()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k is null) return LightingSettings.Default;
            return FromValues(k.GetValue("KbdEffect") as string, k.GetValue("KbdColor") as int?,
                              k.GetValue("KbdBrightness") as int?, k.GetValue("KbdSpeed") as string, k.GetValue("KbdSave") as int?);
        }
        catch { return LightingSettings.Default; }
    }

    public static void Save(LightingSettings s)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("KbdEffect", s.Effect.ToString());
            k.SetValue("KbdColor", s.Color.ToInt(), RegistryValueKind.DWord);
            k.SetValue("KbdBrightness", s.Brightness, RegistryValueKind.DWord);
            k.SetValue("KbdSpeed", s.Speed.ToString());
            k.SetValue("KbdSave", s.SaveToKeyboard ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { /* if it can't be saved, it only applies to this session */ }
    }

    // ---- channels ------------------------------------------------------------------------------------------------

    private sealed record Channel(HidDevice Dev, byte ReportId, string Label);

    private static bool HasVendorPage(HidDevice d)
    {
        try { return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Any(u => (u >> 16) == 0xFF00); }
        catch { return false; }
    }

    private static int OutLen(HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); }
        catch { return 0; }
    }

    private static byte DeclaredReportId(HidDevice d)
    {
        try { return d.GetReportDescriptor().OutputReports.Select(r => r.ReportID).FirstOrDefault(); }
        catch { return 0; }
    }

    /// Only the Azoth's own vendor channel (cable) and the Omni receiver's keyboard channel are ever written to.
    private static List<Channel> Channels()
    {
        var list = new List<Channel>();
        foreach (var d in DeviceList.Local.GetHidDevices(AsusVid))
        {
            string path = d.DevicePath;
            if (d.ProductID == OmniPid)
            {
                if (path.Contains("mi_02&col02", StringComparison.OrdinalIgnoreCase) && OutLen(d) >= 64 && HasVendorPage(d))
                    list.Add(new Channel(d, 0x02, "ROG Omni Receiver"));
            }
            else if (AzothPids.Contains(d.ProductID) && path.Contains("mi_01", StringComparison.OrdinalIgnoreCase)
                     && OutLen(d) >= 64 && HasVendorPage(d) && list.All(c => c.Dev.ProductID != d.ProductID))
                list.Add(new Channel(d, DeclaredReportId(d), HidNames.Of(d, "ROG Azoth")));
        }
        return list;
    }

    private static HidStream Open(HidDevice dev)
    {
        var cfg = new OpenConfiguration();
        cfg.SetOption(OpenOption.Exclusive, false);
        var s = dev.Open(cfg);
        s.ReadTimeout = 300;
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

    private static byte[] Pad(Channel ch, byte[] payload)
    {
        var buf = new byte[Math.Max(OutLen(ch.Dev), payload.Length)];
        Array.Copy(payload, buf, payload.Length);
        return buf;
    }

    /// Sends the keyboard's probe and returns its reply, or null if it doesn't answer (asleep, not on this receiver ...).
    private static byte[]? Probe(HidStream s, Channel ch)
    {
        Drain(s);
        s.Write(Pad(ch, BuildProbePacket(ch.ReportId)));
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 700)
        {
            byte[] r;
            try { r = s.Read(); }
            catch (TimeoutException) { continue; }
            catch (IOException ex) when (ex.Message.Contains("after some time")) { continue; }
            if (IsProbeReply(r)) return r;
        }
        return null;
    }

    /// Writes one packet and returns the first reply (null when there is none).
    private static byte[]? Send(HidStream s, Channel ch, byte[] payload)
    {
        Drain(s);
        s.Write(Pad(ch, payload));
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            try { return s.Read(); }
            catch (TimeoutException) { }
            catch (IOException ex) when (ex.Message.Contains("after some time")) { }
        }
        return null;
    }

    // ---- public ---------------------------------------------------------------------------------------------------

    /// Applies the lighting to every Azoth channel that answers (cable and/or Omni receiver).
    public static LightingResult Apply(LightingSettings s)
    {
        lock (AsusKeyboard.Gate)
        {
            var channels = Channels();
            if (channels.Count == 0)
                return new(false, L.T("ROG Azoth bulunamadı. Kabloyu ya da Omni alıcıyı tak; yine de olmazsa Armoury Crate'i kapat.",
                                      "No ROG Azoth found. Plug in its cable or the Omni receiver; if it still fails, close Armoury Crate."));

            var done = new List<string>();
            string? problem = null;
            foreach (var ch in channels)
            {
                try
                {
                    using var stream = Open(ch.Dev);
                    if (Probe(stream, ch) is null)
                    {
                        problem ??= ch.Label + ": " + L.T("cevap vermedi (klavye uykuda olabilir, bir tuşa bas)", "no answer (the keyboard may be asleep; press a key)");
                        Log.W($"[Azoth RGB] {ch.Label} PID=0x{ch.Dev.ProductID:X4}: probe got no answer");
                        continue;
                    }

                    var packet = BuildLightingPacket(ch.ReportId, s);
                    var reply = Send(stream, ch, packet);
                    Log.W($"[Azoth RGB] {ch.Label} PID=0x{ch.Dev.ProductID:X4} sent {Log.Hex(packet, 19)} -> {(reply is null ? "(no reply)" : Log.Hex(reply, 8))}");
                    if (IsRejected(reply))
                    {
                        problem ??= ch.Label + ": " + L.T("klavye komutu reddetti", "the keyboard rejected the command");
                        continue;
                    }

                    if (s.SaveToKeyboard)
                    {
                        var saved = Send(stream, ch, BuildSavePacket(ch.ReportId));
                        Log.W($"[Azoth RGB] {ch.Label} save -> {(saved is null ? "(no reply)" : Log.Hex(saved, 8))}");
                        if (IsRejected(saved)) problem ??= ch.Label + ": " + L.T("kaydedilemedi", "could not be saved on the keyboard");
                    }
                    done.Add(ch.Label);
                }
                catch (Exception ex)
                {
                    Log.W($"[Azoth RGB] {ch.Label} error: {ex.Message}");
                    problem ??= ch.Label + ": " + ex.Message;
                }
            }

            if (done.Count == 0) return new(false, problem ?? L.T("Uygulanamadı", "Could not apply"));
            string ok = L.T("Uygulandı: ", "Applied to ") + string.Join(", ", done) +
                        (s.SaveToKeyboard ? L.T(" · klavyeye kaydedildi", " · saved on the keyboard") : "");
            return new(true, problem is null ? ok : ok + " · " + problem);
        }
    }

    /// Diagnostics: what was found and how each channel answers the read-only probe.
    public static void Diagnose()
    {
        lock (AsusKeyboard.Gate)
        {
            Log.W("========== Azoth RGB diagnostics started ==========");
            var channels = Channels();
            if (channels.Count == 0)
                Log.W("[Azoth RGB diag] no Azoth channel: needs a keyboard on the cable (VID 0x0B05, interface mi_01) or the Omni receiver (PID 0x1ACE, mi_02&col02)");
            foreach (var ch in channels)
            {
                string tag = $"[Azoth RGB diag] {ch.Label} PID=0x{ch.Dev.ProductID:X4} reportId=0x{ch.ReportId:X2} out={OutLen(ch.Dev)}";
                try
                {
                    using var stream = Open(ch.Dev);
                    var r = Probe(stream, ch);
                    Log.W($"{tag} {ch.Dev.DevicePath} probe -> {(r is null ? "(no answer)" : Log.Hex(r, 16))}");
                }
                catch (Exception ex) { Log.W($"{tag} error: {ex.Message}"); }
            }
            Log.W("========== Azoth RGB diagnostics finished ==========");
        }
    }
}
