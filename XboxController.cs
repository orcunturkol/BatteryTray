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

    internal static string NameOf(uint i) =>
        i == 0 ? L.T("Xbox kolu", "Xbox controller") : L.T($"Xbox kolu {i + 1}", $"Xbox controller {i + 1}");

    // Undocumented export (ordinal 103) that the Xbox accessories software uses to switch a controller off
    [DllImport("xinput1_4.dll", EntryPoint = "#103")] private static extern uint XInputPowerOffController(uint index);

    /// Switches the controller in the given XInput slot off, like holding the Xbox button for six seconds.
    public static bool PowerOff(uint index)
    {
        try
        {
            uint r = XInputPowerOffController(index);
            Log.W($"[Xbox] power off slot {index} -> {r}");
            return r == ErrorSuccess;
        }
        catch (Exception ex) { Log.W("[Xbox] power off failed: " + ex.Message); return false; }
    }

    /// One entry per connected controller; empty when none (or when XInput isn't available).
    public static IReadOnlyList<DeviceStatus> Read()
    {
        var result = new List<DeviceStatus>();
        if (_unavailable) return result;
        int btIndex = 0;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                bool here = XInputGetState(i, out _) == ErrorSuccess;
                string name = NameOf(i);
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
                // XInput can't see a Bluetooth controller's battery: the n-th such controller gets the n-th Bluetooth level
                int? bt = null;
                if (b.Type is not (TypeWired or TypeAlkaline or TypeNimh))
                {
                    var levels = BluetoothLevels();
                    if (btIndex < levels.Count) bt = levels[btIndex];
                    btIndex++;
                }
                string summary = $"type={b.Type} level={b.Level}" + (bt is int v ? $" bluetooth={v}%" : "");
                result.Add(Describe(name, b.Type, b.Level, bt));
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

    // Bluetooth levels are looked up in the background (a few ms of device-manager calls) and cached for 20 s
    private static List<int> _btLevels = new();
    private static DateTime _btAt = DateTime.MinValue;
    private static bool _btBusy;

    private static List<int> BluetoothLevels()
    {
        if (!_btBusy && DateTime.Now - _btAt > TimeSpan.FromSeconds(20))
        {
            _btBusy = true;
            _btAt = DateTime.Now;
            Task.Run(() =>
            {
                try { _btLevels = BtBattery.Levels("Xbox"); }
                finally { _btBusy = false; }
            });
        }
        return _btLevels;
    }

    internal static DeviceStatus Describe(string name, byte type, byte level, int? bluetoothPercent = null)
    {
        if (type is not (TypeWired or TypeAlkaline or TypeNimh))
        {
            // XInput has nothing usable for this controller (typical for Bluetooth)
            if (bluetoothPercent is not int bp) return new(DeviceKind.Gamepad, name, null, false, L.T("Pil düzeyi alınamadı", "Battery level unavailable"));
            return new(DeviceKind.Gamepad, name, bp, false, L.T("Bluetooth", "Bluetooth"));
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
