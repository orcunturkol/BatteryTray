using System.Runtime.InteropServices;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// Xbox controllers (Xbox 360 / One / Series; wired, Xbox wireless adapter or Bluetooth) through XInput:
//  - Windows hands out up to four XInput slots; XInputGetState tells which are connected
//  - XInputGetBatteryInformation returns the battery type (wired / alkaline / NiMH) and a coarse level
//    (empty / low / medium / full). There is no percentage and no charging flag, so the level is shown
//    as an approximate value ("~") and the "empty" level counts as critical.
// ---------------------------------------------------------------------------
internal static class XboxController
{
    private const uint ErrorSuccess = 0;
    private const byte DevTypeGamepad = 0;

    private const byte TypeWired = 1, TypeAlkaline = 2, TypeNimh = 3;
    private const byte LevelEmpty = 0, LevelLow = 1, LevelMedium = 2, LevelFull = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public ushort Buttons; public byte Lt, Rt; public short Lx, Ly, Rx, Ry; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBattery { public byte Type; public byte Level; }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XInputState state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte devType, out XInputBattery battery);

    private static bool _unavailable;
    private static readonly string[] Connected = new string[4];   // for logging connect / disconnect only

    /// Level -> approximate percentage (the middle of each band, "empty" kept low on purpose).
    internal static int? Approximate(byte level) => level switch
    {
        LevelEmpty => 5,
        LevelLow => 25,
        LevelMedium => 60,
        LevelFull => 100,
        _ => null,
    };

    /// One entry per connected controller; empty when none (or when XInput isn't available).
    public static IReadOnlyList<DeviceStatus> Read()
    {
        var result = new List<DeviceStatus>();
        if (_unavailable) return result;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                bool here = XInputGetState(i, out _) == ErrorSuccess;
                string name = i == 0 ? L.T("Xbox kolu", "Xbox controller") : L.T($"Xbox kolu {i + 1}", $"Xbox controller {i + 1}");
                if (!here)
                {
                    if (Connected[i] is not null) { Log.W($"[Xbox] {Connected[i]} disconnected"); Connected[i] = null!; }
                    continue;
                }
                if (XInputGetBatteryInformation(i, DevTypeGamepad, out var b) != ErrorSuccess)
                {
                    result.Add(new(DeviceKind.Gamepad, name, null, false, L.T("Okunamadı", "Read failed")));
                    continue;
                }
                var wgi = b.Type is TypeWired or TypeAlkaline or TypeNimh ? null : ReadWinRt((int)i);
                string summary = $"type={b.Type} level={b.Level}" + (wgi is { } w ? $" winrt={w.Remaining}/{w.Full} status={w.Status}" : "");
                result.Add(Describe(name, b.Type, b.Level, wgi));
                if (Connected[i] != summary)
                {
                    Connected[i] = summary;
                    Log.W($"[Xbox] {name} connected: {summary}");
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            Log.W("[Xbox] XInput not available: " + ex.Message);
        }
        catch (Exception ex) { Log.W("[Xbox] error: " + ex.Message); }
        return result;
    }

    /// Bluetooth controllers often answer XInput with "battery type disconnected" although they are connected; Windows
    /// still knows their charge through Windows.Gaming.Input (exact capacities), so ask that instead.
    private static (int Remaining, int Full, string Status)? ReadWinRt(int index)
    {
        try
        {
            var pads = Windows.Gaming.Input.Gamepad.Gamepads;
            if (index >= pads.Count) return null;
            var r = pads[index].TryGetBatteryReport();
            if (r?.RemainingCapacityInMilliwattHours is not int rem || r.FullChargeCapacityInMilliwattHours is not int full || full <= 0) return null;
            return (rem, full, r.Status.ToString());
        }
        catch (Exception ex) { Log.Debug("[Xbox] WinRT battery error: " + ex.Message); return null; }
    }

    internal static DeviceStatus Describe(string name, byte type, byte level, (int Remaining, int Full, string Status)? wgi = null)
    {
        if (type is not (TypeWired or TypeAlkaline or TypeNimh))
        {
            // XInput has nothing usable for this controller
            if (wgi is not { } w) return new(DeviceKind.Gamepad, name, null, false, L.T("Pil düzeyi alınamadı", "Battery level unavailable"));
            int wp = Math.Clamp((int)Math.Round(100.0 * w.Remaining / w.Full), 0, 100);
            bool charging = w.Status == "Charging";
            return new(DeviceKind.Gamepad, name, wp, charging, Notes.Make(L.T("Kablosuz", "Wireless"), charging));
        }
        if (type == TypeWired)
            return new(DeviceKind.Gamepad, name, null, false, Notes.Cable, Wired: true);

        string power = type switch
        {
            TypeAlkaline => L.T("Piller", "Batteries"),
            TypeNimh => L.T("Şarj edilebilir pil", "Rechargeable pack"),
            _ => L.T("Kablosuz", "Wireless"),
        };
        int? pct = Approximate(level);
        if (pct is null) return new(DeviceKind.Gamepad, name, null, false, L.T("Pil düzeyi bilinmiyor", "Battery level unknown"));
        string band = level switch
        {
            LevelEmpty => L.T("Boş", "Empty"),
            LevelLow => L.T("Düşük", "Low"),
            LevelMedium => L.T("Orta", "Medium"),
            _ => L.T("Dolu", "Full"),
        };
        return new(DeviceKind.Gamepad, name, pct, false, $"{band} · {power}", Approx: true);
    }
}
