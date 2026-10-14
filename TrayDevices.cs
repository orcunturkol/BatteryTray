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
internal sealed record PinnedDevice(string Id, string Name);

/// Devices pinned to the tray ("Show in tray"): each one gets its own icon with its own percentage.
/// Stored as one "id<TAB>name" line per device under HKCU\Software\BatteryTray\TrayDevices.
internal static class TrayPins
{
    private const string Key = @"Software\BatteryTray";
    private const string ValueName = "TrayDevices";

    internal static string Format(PinnedDevice p) => $"{Clean(p.Id)}\t{Clean(p.Name)}";

    internal static PinnedDevice? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        int tab = line.IndexOf('\t');
        string id = (tab < 0 ? line : line[..tab]).Trim();
        string name = tab < 0 ? "" : line[(tab + 1)..].Trim();
        return id.Length == 0 ? null : new PinnedDevice(id, name.Length == 0 ? id : name);
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// Pins the device (added at the end), or unpins it when it is already pinned.
    internal static List<PinnedDevice> Toggle(IEnumerable<PinnedDevice> pins, PinnedDevice device)
    {
        var list = pins.ToList();
        int i = list.FindIndex(p => p.Id == device.Id);
        if (i >= 0) list.RemoveAt(i); else list.Add(device);
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
