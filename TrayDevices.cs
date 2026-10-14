using System.Drawing;
using Microsoft.Win32;

namespace BatteryTray;

/// Which devices are listed in the panel and can be pinned to the tray: only those that report something.
/// A dongle / receiver that is plugged in while the device itself is off or out of range never had a reading,
/// so there is nothing to show. A device that fell asleep keeps its last reading and stays listed.
/// One exception: Standby, a receiver that answered while its device didn't (e.g. HS80 receiver plugged in,
/// headset off). That is a confirmed status the user asked to see, not an unknown.
internal static class DeviceFilter
{
    public static bool IsOnline(DeviceStatus d) =>
        d.Present && (d.Percent is not null || d.Wired || d.Standby || d.Parts?.Any(p => p.Percent is not null) == true);
}

/// Id: stable per device slot ("headset", "atk-mouse", "logitech:G305" ...). Name: what the device last called itself,
/// kept so the tray tooltip still has a name while the device is offline.
/// Color: ARGB of the tray text (0 = not chosen yet; one is picked from TrayPins.Palette when the device is pinned).
internal sealed record PinnedDevice(string Id, string Name, int Color = 0);

/// Devices pinned to the tray ("Show in tray"): each one gets its own icon with its own percentage.
/// Stored as one "id<TAB>name" line per device under HKCU\Software\BatteryTray\TrayDevices.
internal static class TrayPins
{
    private const string Key = @"Software\BatteryTray";
    private const string ValueName = "TrayDevices";

    /// Text colors for the tray icons, so devices can be told apart. No red: that is reserved for a low battery.
    internal static readonly (string En, string Tr, Color Value)[] Palette =
    {
        ("White", "Beyaz", Color.White),
        ("Cyan", "Camgöbeği", Color.FromArgb(80, 220, 255)),
        ("Yellow", "Sarı", Color.FromArgb(255, 225, 70)),
        ("Green", "Yeşil", Color.FromArgb(110, 235, 110)),
        ("Orange", "Turuncu", Color.FromArgb(255, 160, 50)),
        ("Magenta", "Eflatun", Color.FromArgb(255, 120, 230)),
        ("Blue", "Mavi", Color.FromArgb(130, 160, 255)),
    };

    internal static string Format(PinnedDevice p) => $"{Clean(p.Id)}\t{Clean(p.Name)}\t{p.Color}";

    internal static PinnedDevice? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split('\t');
        string id = parts[0].Trim();
        string name = parts.Length > 1 ? parts[1].Trim() : "";
        int color = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;
        return id.Length == 0 ? null : new PinnedDevice(id, name.Length == 0 ? id : name, color);
    }

    /// First palette color no pinned device uses yet (wraps around when there are more devices than colors).
    internal static int NextColor(IReadOnlyCollection<PinnedDevice> pins)
    {
        foreach (var (_, _, c) in Palette)
            if (pins.All(p => p.Color != c.ToArgb())) return c.ToArgb();
        return Palette[pins.Count % Palette.Length].Value.ToArgb();
    }

    /// Color of a pin's text; pins saved before colors existed (0) get one by their position.
    internal static Color ColorOf(PinnedDevice p, int index) =>
        p.Color != 0 ? Color.FromArgb(p.Color) : Palette[index % Palette.Length].Value;

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// Pins the device (added at the end), or unpins it when it is already pinned.
    internal static List<PinnedDevice> Toggle(IEnumerable<PinnedDevice> pins, PinnedDevice device)
    {
        var list = pins.ToList();
        int i = list.FindIndex(p => p.Id == device.Id);
        if (i >= 0) list.RemoveAt(i);
        else list.Add(device.Color != 0 ? device : device with { Color = NextColor(list) });
        return list;
    }

    public static List<PinnedDevice> Load()
    {
        var result = new List<PinnedDevice>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue(ValueName) is string[] lines)
                foreach (var p in lines.Select(Parse))
                    if (p is not null && result.All(r => r.Id != p.Id)) result.Add(p);
        }
        catch { }
        return result;
    }

    public static void Save(IEnumerable<PinnedDevice> pins)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue(ValueName, pins.Select(Format).ToArray(), RegistryValueKind.MultiString);
        }
        catch { /* if it can't be saved, it only applies to this session */ }
    }
}

internal static class TrayText
{
    /// Hover text of a pinned icon (the tray tooltip holds at most 127 characters). d = null: the device is offline.
    internal static string Tooltip(string name, DeviceStatus? d)
    {
        string t;
        if (d is null) t = $"{name}: " + L.T("çevrimdışı", "offline");
        else if (d.Percent is int p)
            t = $"{name}: {(d.Approx || d.Kind == DeviceKind.Earbuds ? "~" : "")}{L.Pct(p)}" +
                (d.Charging ? " · " + L.T("şarj oluyor", "charging") : "");
        else t = $"{name}: {d.Note}";
        return t.Length > 120 ? t[..120] : t;
    }
}
