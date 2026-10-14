using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// AirPods battery info from Bluetooth LE advertisements (the "proximity pairing" message).
// Apple manufacturer data (company id 0x004C), when the first byte is 0x07:
//   [3..4] model id (little-endian)
//   [5]    status (bit 5: which bud is "primary" -> left/right are swapped)
//   [6]    upper/lower nibble = battery of the two buds (0..10 => 0..100 %, 15 = unknown)
//   [7]    upper nibble = charging flags (bit0/bit1 buds, bit2 case), lower nibble = case battery
// Values come in 10 % steps. Advertised while the buds are worn or the case is open.
// Only strong signals (RSSI) count as "my AirPods", to filter out other Apple devices nearby.
// The "My devices" row is only shown when AirPods are paired with this PC; values come from
// the advertisements even while the AirPods are connected to a phone.
// ---------------------------------------------------------------------------
internal static class AirPods
{
    private const short MinRssi = -65;   // ~a few meters, so the neighbour's AirPods don't mix in

    private static BluetoothLEAdvertisementWatcher? _watcher;
    private static readonly object _lock = new();
    private static (int? left, int? right, int? box, bool chL, bool chR, bool chBox, DateTime at)? _last;

    // So the AirPods row doesn't disappear after an app restart, the paired name and the last
    // battery values are stored next to the exe. (AirPods only advertise battery with the case open
    // or while worn, and the paired-device query takes ~30 s on Windows.)
    private sealed record Saved(string? Name, int? L, int? R, int? Box, DateTime? At);
    private static readonly string StateFile = System.IO.Path.Combine(AppContext.BaseDirectory, "airpods_last.json");

    private static Saved? LoadSaved()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(StateFile)); }
        catch { return null; }
    }

    private static void Save()
    {
        try
        {
            (int? left, int? right, int? box, bool chL, bool chR, bool chBox, DateTime at)? v;
            lock (_lock) v = _last;
            var s = new Saved(_paired?.name, v?.left, v?.right, v?.box, v?.at);
            File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(s));
        }
        catch { }
    }

    static AirPods()
    {
        var s = LoadSaved();
        if (s is null) return;
        if (s.Name != null) _paired = (s.Name, false);   // the real state is updated by the background query
        if (s.At is DateTime at && (s.L ?? s.R ?? s.Box) != null)
            _last = (s.L, s.R, s.Box, false, false, false, at);
    }
    private static readonly HashSet<string> _loggedRaw = new();
    private static string? _startError;

    // ALL nearby AirPods/Beats advertisements (unpaired included), keyed by address
    private sealed record Ad(string Model, short Rssi, int? L, int? R, int? Box, bool ChL, bool ChR, bool ChBox, DateTime At);
    private static readonly Dictionary<ulong, Ad> _nearby = new();

    // Every other Bluetooth LE device: name, manufacturer, signal and (if present) Battery Service percentage
    private sealed record Ble(string? Name, string? Maker, short Rssi, int? Battery, DateTime At);
    private static readonly Dictionary<ulong, Ble> _ble = new();

    private static string? MakerName(ushort id) => id switch
    {
        0x004C => "Apple", 0x0006 => "Microsoft", 0x0075 => "Samsung", 0x00E0 => "Google",
        0x0087 => "Garmin", 0x038F => "Xiaomi", 0x027D => "Huawei", 0x0157 => "Huami / Mi Band",
        0x0171 => "Amazon", 0x00D2 => "Bose", 0x012D => "Sony", 0x0310 => "JBL / Harman",
        0x02E5 => "Espressif", 0x0059 => "Nordic", 0x0499 => "Ruuvi", 0x0822 => "Anker / Soundcore",
        _ => null
    };

    private static void RecordBle(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var adv = args.Advertisement;
        string? name = string.IsNullOrWhiteSpace(adv.LocalName) ? null : adv.LocalName.Trim();
        string? maker = adv.ManufacturerData.Select(m => MakerName(m.CompanyId)).FirstOrDefault(n => n != null);

        // Standard Battery Service (0x180F) service data: [0F 18 percent]
        int? battery = null;
        foreach (var sec in adv.DataSections.Where(x => x.DataType == 0x16))
        {
            var b = new byte[sec.Data.Length];
            DataReader.FromBuffer(sec.Data).ReadBytes(b);
            if (b.Length >= 3 && b[0] == 0x0F && b[1] == 0x18 && b[2] <= 100) battery = b[2];
        }

        lock (_ble)
        {
            _ble.TryGetValue(args.BluetoothAddress, out var old);
            // Name and battery sometimes come only in some packets: keep the previous ones
            _ble[args.BluetoothAddress] = new Ble(name ?? old?.Name, maker ?? old?.Maker,
                args.RawSignalStrengthInDBm, battery ?? old?.Battery, DateTime.Now);
        }
    }

    private static string Distance(short rssi) =>
        rssi >= -55 ? L.T("çok yakın", "very close") : rssi >= -70 ? L.T("yakın", "near")
        : rssi >= -85 ? L.T("orta", "medium") : L.T("uzak", "far");

    // Known model ids (proximity pairing [3..4]). Unknown ones are shown with their code.
    private static string ModelName(string code) => code switch
    {
        "2002" => "AirPods (1st gen)",
        "200F" => "AirPods (2nd gen)",
        "2013" => "AirPods (3rd gen)",
        "2019" or "201B" => "AirPods 4",
        "200E" => "AirPods Pro",
        "2014" or "2024" => "AirPods Pro 2",
        "2027" => "AirPods Pro 3",
        "200A" or "201F" => "AirPods Max",
        "2003" or "200B" or "2011" or "2010" or "2016" or "2012" or "2017" or "2006" or "2009" or "2005" => "Beats",
        _ => L.T("Apple kulaklık", "Apple earbuds") + $" ({code})"
    };

    public static void Start()
    {
        if (_watcher != null) return;
        try
        {
            // Active scanning: devices usually send their name only in the "scan response"
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += OnReceived;
            _watcher.Stopped += (_, e) => Log.W($"[AirPods] scan stopped: {e.Error}");
            _watcher.Start();
            Log.W("[AirPods] Bluetooth LE scan started");
        }
        catch (Exception ex)
        {
            _startError = ex.Message;
            Log.W("[AirPods] could not start scan: " + ex.Message);
        }
    }

    private static void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            RecordBle(args);
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != 0x004C) continue;
                var d = new byte[md.Data.Length];
                DataReader.FromBuffer(md.Data).ReadBytes(d);
                // Only the long "proximity pairing" message carries battery: 07 19 01 ...
                // (short messages like 07 11 06 ... have no battery and would give wrong values)
                if (d.Length < 9 || d[0] != 0x07 || d[1] != 0x19 || d[2] != 0x01) continue;

                // Log the raw data once per model/format (helps identify new models)
                string key = $"{d[4]:X2}{d[3]:X2}";   // model id is little-endian (e.g. 0E 20 -> 200E = AirPods Pro)
                lock (_loggedRaw)
                    if (_loggedRaw.Add(key))
                        Log.Debug($"[AirPods] advertisement: model=0x{key} rssi={args.RawSignalStrengthInDBm} raw={BitConverter.ToString(d)}");

                bool flip = (d[5] & 0x20) == 0;
                int a = d[6] >> 4, b = d[6] & 0x0F;
                int leftRaw = flip ? a : b, rightRaw = flip ? b : a;
                int caseRaw = d[7] & 0x0F;
                int chg = d[7] >> 4;

                static int? Pct(int v) => v <= 10 ? v * 10 : null;   // 15 = unknown / not in place
                var left = Pct(leftRaw);
                var right = Pct(rightRaw);
                var box = Pct(caseRaw);
                bool chL = (chg & (flip ? 0b010 : 0b001)) != 0;
                bool chR = (chg & (flip ? 0b001 : 0b010)) != 0;
                bool chBox = (chg & 0b100) != 0;

                if (left is null && right is null && box is null) continue;

                lock (_nearby)
                    _nearby[args.BluetoothAddress] = new Ad(ModelName(key), args.RawSignalStrengthInDBm,
                        left, right, box, chL, chR, chBox, DateTime.Now);

                // Only strong (close) signals count as "my AirPods"
                if (args.RawSignalStrengthInDBm < MinRssi) continue;
                bool changed;
                lock (_lock)
                {
                    changed = _last is null || _last.Value.left != left || _last.Value.right != right || _last.Value.box != box;
                    _last = (left, right, box, chL, chR, chBox, DateTime.Now);
                }
                if (changed) Save();
            }
        }
        catch (Exception ex) { Log.W("[AirPods] could not read advertisement: " + ex.Message); }
    }

    // Paired AirPods: name and whether connected (refreshed every 60 s)
    private static (string name, bool connected)? _paired;
    private static DateTime _pairedCheckedAt = DateTime.MinValue;
    private static bool _pairedLogged;

    private static Task? _pairedTask;

    private static async Task RefreshPairedAsync()
    {
        if ((DateTime.Now - _pairedCheckedAt).TotalSeconds < 60) return;
        _pairedCheckedAt = DateTime.Now;
        try
        {
            // Paired Bluetooth devices (AssociationEndpoint) + the "is connected" property
            const string IsConnected = "System.Devices.Aep.IsConnected";
            var list = await DeviceInformation.FindAllAsync(
                "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
                new[] { IsConnected }, DeviceInformationKind.AssociationEndpoint);
            (string, bool)? found = null;
            foreach (var di in list.Where(d => d.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)))
            {
                bool connected = di.Properties.TryGetValue(IsConnected, out var v) && v is bool b && b;
                found = (di.Name, connected);
                if (connected) break;   // with more than one paired AirPods, prefer the connected one
            }
            bool nameChanged = _paired?.name != found?.Item1;
            _paired = found;
            if (nameChanged) Save();
            // Log only the first result and changes, not every minute
            if (!_pairedLogged || nameChanged)
            {
                _pairedLogged = true;
                Log.Debug(found is null ? "[AirPods] no paired AirPods"
                                    : $"[AirPods] paired: {found.Value.Item1}, connected={found.Value.Item2}");
            }
        }
        catch (Exception ex) { Log.W("[AirPods] could not read paired devices: " + ex.Message); }
    }

    /// Panel row. Null when there are no paired AirPods (row not shown).
    public static async Task<DeviceStatus?> ReadAsync()
    {
        // The paired-device query can take up to 30 s on Windows: run it in the background,
        // the panel/icon shouldn't wait for it (the row may not appear on the very first round)
        if (_pairedTask is null || _pairedTask.IsCompleted) _pairedTask = Task.Run(RefreshPairedAsync);
        await Task.CompletedTask;
        return Current();
    }

    /// Panel row from what the Bluetooth scan has already seen. Cheap: no device queries,
    /// so the open panel can call it every second (e.g. right after the case lid is opened).
    public static DeviceStatus? Current()
    {
        if (_paired is null) return null;
        var (pairedName, connected) = _paired.Value;

        Start();
        (int? left, int? right, int? box, bool chL, bool chR, bool chBox, DateTime at)? v;
        lock (_lock) v = _last;

        if (_startError is not null)
            return new(DeviceKind.Earbuds, pairedName, null, false, L.T("Bluetooth taranamıyor", "Bluetooth scan unavailable"));
        if (v is null)
            return new(DeviceKind.Earbuds, pairedName, null, false,
                L.T("Pil için kutunun kapağını aç", "Open the case lid to read battery"));

        var x = v.Value;
        // Main percentage: the lower of the two buds (left/right can differ)
        int? main = (x.left, x.right) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => x.box
        };

        // AirPods advertise battery while connected to a phone too; show values even if not connected to this PC.
        // The connection state is only mentioned in the second line.
        string conn = connected ? Notes.Wireless : L.T("Bu bilgisayara bağlı değil", "Not connected to this PC");
        bool stale = (DateTime.Now - x.at).TotalSeconds > 90;
        var parts = Parts(x.left, x.right, x.box, !stale && x.chL, !stale && x.chR, !stale && x.chBox);
        if (stale)
            return new(DeviceKind.Earbuds, pairedName, main, false,
                L.T("Son görülme ", "Last seen ") + $"{x.at:HH:mm}" + (connected ? "" : " · " + conn),
                Parts: parts);
        if (!connected)
            return new(DeviceKind.Earbuds, pairedName, main, x.chL || x.chR, conn, Parts: parts);

        bool charging = x.chL || x.chR;
        return new(DeviceKind.Earbuds, pairedName, main, charging, Notes.Make(Notes.Wireless, charging), Parts: parts);
    }

    /// "Nearby" tab: devices seen in the last 60 s, closest first.
    /// AirPods/Beats with battery info; others with their name (or manufacturer) and distance.
    public static IReadOnlyList<DeviceStatus> Nearby()
    {
        const int MaxRows = 8;
        List<(ulong addr, Ad ad)> ads;
        lock (_nearby)
        {
            foreach (var k in _nearby.Where(kv => (DateTime.Now - kv.Value.At).TotalSeconds > 60).Select(kv => kv.Key).ToList())
                _nearby.Remove(k);
            ads = _nearby.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // The same AirPods can show up under several addresses: each bud advertises separately and
        // Apple rotates the address every few minutes. Same model + same battery values = one device;
        // keep the strongest signal.
        var earbuds = ads
            .GroupBy(x => (x.ad.Model, x.ad.L, x.ad.R, x.ad.Box))
            .Select(g => g.OrderByDescending(x => x.ad.Rssi).First())
            // The paired AirPods already appear in "My devices": don't repeat them here
            .Where(x => !IsMine(x.ad))
            .Select(x => (rssi: x.ad.Rssi, status: EarbudStatus(x.ad)))
            .ToList();
        var skip = ads.Select(x => x.addr).ToHashSet();

        List<(short rssi, DeviceStatus status)> others;
        lock (_ble)
        {
            foreach (var k in _ble.Where(kv => (DateTime.Now - kv.Value.At).TotalSeconds > 60).Select(kv => kv.Key).ToList())
                _ble.Remove(k);
            others = _ble.Where(kv => !skip.Contains(kv.Key) && (kv.Value.Name != null || kv.Value.Battery != null))
                .Select(kv =>
                {
                    var b = kv.Value;
                    string title = b.Name ?? (b.Maker != null ? L.T($"{b.Maker} cihazı", $"{b.Maker} device") : L.T("İsimsiz cihaz", "Unnamed device"));
                    string note = Distance(b.Rssi) + (b.Name != null && b.Maker != null ? $" · {b.Maker}" : "");
                    return (b.Rssi, new DeviceStatus(DeviceKind.Other, title, b.Battery, false, note));
                })
                // Devices with rotating (random) addresses shouldn't appear several times under the same name
                .GroupBy(x => x.Item2.Name)
                .Select(g => g.OrderByDescending(x => x.Item1).First())
                .ToList();
        }

        return earbuds.Concat(others)
            .OrderByDescending(x => x.Item1)
            .Take(MaxRows)
            .Select(x => x.Item2)
            .ToList();
    }

    private static IReadOnlyList<BatteryPart> Parts(int? l, int? r, int? box, bool chL, bool chR, bool chBox) => new[]
    {
        new BatteryPart(L.T("Sol", "Left"), l, chL, Approx: true),
        new BatteryPart(L.T("Sağ", "Right"), r, chR, Approx: true),
        new BatteryPart(L.T("Kutu", "Case"), box, chBox, Approx: true),
    };

    /// Does this advertisement belong to the paired AirPods shown in "My devices"?
    /// (Advertisements carry no stable id; if the values match the last 90 s, we assume it does.)
    private static bool IsMine(Ad a)
    {
        if (_paired is null) return false;
        (int? left, int? right, int? box, bool chL, bool chR, bool chBox, DateTime at)? m;
        lock (_lock) m = _last;
        return m is { } v && (DateTime.Now - v.at).TotalSeconds <= 90
               && v.left == a.L && v.right == a.R && v.box == a.Box;
    }

    private static DeviceStatus EarbudStatus(Ad a)
    {
        int? main = (a.L, a.R) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => a.Box
        };
        // Second line shows only the rough distance; batteries are in separate columns
        return new DeviceStatus(DeviceKind.Earbuds, a.Model, main, a.ChL || a.ChR, Distance(a.Rssi),
            Parts: Parts(a.L, a.R, a.Box, a.ChL, a.ChR, a.ChBox));
    }

    public static void Stop()
    {
        try { _watcher?.Stop(); } catch { }
    }
}
