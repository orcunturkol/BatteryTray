using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using HidSharp;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// HS80 MAX protocol (reverse-engineered by the community):
//  - VID 0x1B1C (Corsair), wireless dongle PID 0x0A97
//  - Control interface: interface 4 ("mi_04" in the path), vendor usage page 0xFF42
//  - Request (64 bytes): [0]=0x02 (report id) [1]=target [2]=0x02 (GET) [3]=property
//      target 0x08 = dongle/root device, 0x09 = headset (child)
//      property 0x0F = battery (LE16, 0..1000 -> /10 = %)
//      property 0x10 = charge state (0 discharging, 1 charging, 2 full)
//  - Response: [1]=target [2]=command [3]=error code (0 = OK) [4..]=value
// ---------------------------------------------------------------------------
internal static class Hs80Protocol
{
    public const int CorsairVid = 0x1B1C;
    public const uint VendorUsagePage = 0xFF42;
    public const byte ReportId = 0x02;
    public const byte RouteRoot = 0x08;
    public const byte RouteHeadset = 0x09;
    public const byte CmdGet = 0x02;
    public const byte PropFirmware = 0x13;   // receiver firmware query (also part of the wake-up handshake)
    public const byte PropBattery = 0x0F;
    public const byte PropChargeState = 0x10;
}

internal sealed record BatteryInfo(double Percent, int ChargeState);

internal static class Log
{
    // The log is kept next to the exe (batterytray_log.txt)
    public static readonly string File_ = System.IO.Path.Combine(AppContext.BaseDirectory, "batterytray_log.txt");
    public static void W(string msg)
    {
        try
        {
            // Reset the log once it exceeds 2 MB
            var fi = new FileInfo(File_);
            if (fi.Exists && fi.Length > 2_000_000) File.WriteAllText(File_, "");
            File.AppendAllText(File_, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
    // Routine device traces (every scan, every read) are written only for a while after "Diagnostics" is used,
    // so the log stays small. W is for events worth keeping: errors, connect/disconnect, alerts.
    private static DateTime _verboseUntil;
    public static void StartVerbose(TimeSpan span) => _verboseUntil = DateTime.Now + span;
    public static void Debug(string msg) { if (DateTime.Now < _verboseUntil) W(msg); }
    public static string Hex(byte[] b, int n = 16) => BitConverter.ToString(b, 0, Math.Min(n, b.Length));
}

internal sealed class Hs80Device : IDisposable
{
    public string? LastError { get; private set; }
    private HidStream? _stream;
    private int _outLen = 64;
    private byte _route = Hs80Protocol.RouteHeadset;
    private byte _reportId = Hs80Protocol.ReportId;
    private bool _discovering;   // packets are only logged while searching for the device

    public string? Path { get; private set; }
    public string? ProductName { get; private set; }   // name the device reports over USB
    public bool Found { get; private set; }            // is a supported headset present on this PC
    /// The receiver answers but the headset doesn't: the receiver is plugged in and the headset is off / out of range.
    public bool ReceiverOnly { get; private set; }

    /// Finds Corsair's vendor (0xFF42) control interface. The PID is not checked,
    /// so both the dongle (0x0A97) and a headset connected by cable can be tried.
    public static IEnumerable<HidDevice> FindCandidates()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(Hs80Protocol.CorsairVid))
        {
            if (HasVendorUsagePage(d)) yield return d;
        }
    }

    public static bool HasVendorUsagePage(HidDevice d)
    {
        try
        {
            var desc = d.GetReportDescriptor();
            foreach (var item in desc.DeviceItems)
                foreach (var usage in item.Usages.GetAllValues())
                    if ((usage >> 16) == Hs80Protocol.VendorUsagePage) return true;
        }
        catch { /* some interfaces don't expose a descriptor */ }
        return false;
    }

    public bool EnsureOpen()
    {
        if (_stream != null) return true;
        _discovering = true;
        try { return EnsureOpenCore(); }
        finally { _discovering = false; }
    }

    private int _lastCandidateCount = -1;

    private bool EnsureOpenCore()
    {
        // Take writable interfaces (out > 0); try the known-good dongle (0x0A97)
        // first and the headset's own USB interface (0x0A96) second.
        var candidates = FindCandidates()
            .Where(d => d.GetMaxOutputReportLength() > 0)
            .OrderBy(d => d.ProductID == 0x0A97 ? 0 : 1)
            .ToList();
        // Logged only when the count changes; while the headset is off this runs every 5 s
        if (candidates.Count != _lastCandidateCount)
        {
            _lastCandidateCount = candidates.Count;
            Log.W($"candidate interfaces: {candidates.Count}");
        }
        Found = candidates.Count > 0;
        if (!Found) { LastError = L.T("Bulunamadı", "Not found"); ReceiverOnly = false; }

        bool receiverAnswered = false;   // a receiver replied on its own route although the headset did not

        foreach (var dev in candidates)
        {
          try
          {
            Log.Debug($"trying PID=0x{dev.ProductID:X4} out={dev.GetMaxOutputReportLength()} in={dev.GetMaxInputReportLength()} {dev.DevicePath}");
            if (!dev.TryOpen(out var s)) { LastError = L.T("arayüz açılamadı", "could not open interface"); Log.W("  could not open"); continue; }
            s.ReadTimeout = 400;
            s.WriteTimeout = 400;
            _stream = s;
            _outLen = Math.Max(dev.GetMaxOutputReportLength(), 64);
            Path = dev.DevicePath;
            // The receiver calls itself "... Receiver"; it stands for the headset in the panel, so say "Headset"
            ProductName = HidNames.Of(dev, L.T("Kulaklık", "Headset")).Replace("Receiver", "Headset");

            // Read the output report IDs the device declares; use the first one if 0x02 is missing.
            try
            {
                var ids = dev.GetReportDescriptor().OutputReports.Select(r => r.ReportID).ToList();
                Log.Debug("  output report IDs: " + string.Join(",", ids.Select(i => "0x" + i.ToString("X2"))));
                _reportId = ids.Count == 0 || ids.Contains(Hs80Protocol.ReportId) ? Hs80Protocol.ReportId : ids[0];
            }
            catch (Exception ex) { Log.Debug("  could not read descriptor: " + ex.Message); }

            // Try the headset target (0x09) first; when connected directly by cable
            // the headset itself may answer as the root (0x08).
            foreach (var route in new[] { Hs80Protocol.RouteHeadset, Hs80Protocol.RouteRoot })
            {
                _route = route;
                Log.Debug($"  trying route 0x{route:X2}");
                if (route == Hs80Protocol.RouteHeadset) Wake();
                if (ReadBatteryRaw() is not null) { Log.Debug("  OK"); LastError = null; ReceiverOnly = false; return true; }
            }
            LastError = L.T("kulaklıktan geçerli cevap gelmedi", "no valid response from headset");
            // The receiver (not the headset's own USB interface) still answers on its own route when the headset is off
            if (dev.ProductID == 0x0A97 && ReceiverResponds()) receiverAnswered = true;
          }
          catch (Exception ex)
          {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Log.W($"  error (PID=0x{dev.ProductID:X4}): " + ex);
          }
          Close();
        }

        // Receiver answered but the headset didn't: the receiver is plugged in and the headset is off or out of range
        if (receiverAnswered)
        {
            LastError = L.T("Alıcı takılı · kulaklık bağlı değil", "Receiver found · headset not connected");
            if (!ReceiverOnly) Log.W("receiver answers, headset doesn't: headset not connected");
        }
        else if (ReceiverOnly) Log.W("receiver no longer answers on its own route");
        ReceiverOnly = receiverAnswered;
        return false;
    }

    /// Asks the receiver itself (root route) for its firmware version. It answers even while the headset is off,
    /// which tells "headset not connected" apart from "receiver not responding".
    private bool ReceiverResponds()
    {
        var saved = _route;
        _route = Hs80Protocol.RouteRoot;
        try { return GetProperty(Hs80Protocol.PropFirmware) is not null; }
        finally { _route = saved; }
    }

    public BatteryInfo? Read()
    {
        try
        {
            if (!EnsureOpen()) return null;
            var raw = ReadBatteryRaw();
            if (raw is null && _route == Hs80Protocol.RouteHeadset)
            {
                // Over wireless the headset may need a wake-up after staying idle for a while
                Wake();
                raw = ReadBatteryRaw();
            }
            if (raw is null)
            {
                // If the cable was plugged/unplugged the right interface may have changed:
                // close the connection and search again on the next round.
                LastError = L.T("cevap alınamadı, yeniden aranacak", "no response, will retry");
                Close();
                return null;
            }
            var charge = GetProperty(Hs80Protocol.PropChargeState);
            int state = charge is { Length: > 0 } ? charge[0] : -1;
            return new BatteryInfo(raw.Value / 10.0, state);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Log.W("read error: " + ex);
            Close(); // if the device was removed, search again next round
            return null;
        }
    }

    /// Handshake from HeadsetControl: firmware query (0x13) and "heartbeat" (0x12) to the receiver,
    /// then a heartbeat to the headset. Opens communication without switching the headset to software mode.
    private void Wake()
    {
        if (_stream == null) return;
        void Send(byte route, byte prop)
        {
            var req = new byte[_outLen];
            req[0] = _reportId; req[1] = route; req[2] = Hs80Protocol.CmdGet; req[3] = prop;
            _stream!.Write(req);
        }
        void Drain()
        {
            int old = _stream!.ReadTimeout;
            _stream.ReadTimeout = 50;
            try { for (int i = 0; i < 10; i++) { var r = _stream.Read(); if (_discovering) Log.Debug($"    < (wake) {Log.Hex(r)}"); } }
            catch { }
            _stream.ReadTimeout = old;
        }
        try
        {
            if (_discovering) Log.Debug("    wake: receiver 0x13/0x12, headset 0x12");
            Send(Hs80Protocol.RouteRoot, 0x13);
            Send(Hs80Protocol.RouteRoot, 0x12);
            Drain();
            Send(Hs80Protocol.RouteHeadset, 0x12);
            Thread.Sleep(100);
            Drain();
        }
        catch (Exception ex) { Log.W("    wake error: " + ex.Message); }
    }

    private int? ReadBatteryRaw()
    {
        var v = GetProperty(Hs80Protocol.PropBattery);
        if (v is not { Length: >= 2 }) return null;
        int raw = v[0] | (v[1] << 8);
        return raw is > 0 and <= 1000 ? raw : null;
    }

    /// Sends a GET request and returns the value part of the matching response.
    private byte[]? GetProperty(byte prop)
    {
        if (_stream == null) return null;

        var req = new byte[_outLen];
        req[0] = _reportId;
        req[1] = _route;
        req[2] = Hs80Protocol.CmdGet;
        req[3] = prop;
        if (_discovering) Log.Debug($"    > {Log.Hex(req, 8)}");
        _stream.Write(req);

        // The headset may send unsolicited event packets in between,
        // so read a few reports and look for ours.
        for (int i = 0; i < 8; i++)
        {
            byte[] resp;
            try { resp = _stream.Read(); }
            catch (Exception ex) when (ex is TimeoutException || (ex is IOException && ex.Message.Contains("after some time")))
            { if (_discovering) Log.Debug("    < (timeout)"); return null; }
            if (_discovering) Log.Debug($"    < {Log.Hex(resp)}");

            // The report ID is sometimes at the start and sometimes not: try both offsets.
            foreach (int off in new[] { 0, -1 })
            {
                int r = 1 + off;
                if (r < 0 || resp.Length < r + 5) continue;
                // In wired mode the headset returns 0x00 as the target byte,
                // so only the command + error code are checked, not the target.
                if (resp[r + 1] == Hs80Protocol.CmdGet && resp[r + 2] == 0x00)
                    return resp[(r + 3)..];
            }
        }
        return null;
    }

    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Close();
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Hs80Device _device = new();
    private readonly PopupForm _popup = new();
    // Nothing is shown at start; each device is added to the panel with its name once found
    private DeviceStatus _headset = new(DeviceKind.Headset, L.T("Kulaklık", "Headset"), null, false, "...", Present: false);
    private DeviceStatus _mouse = new(DeviceKind.Mouse, L.T("Fare", "Mouse"), null, false, "...", Present: false);
    private DeviceStatus _atkMouse = new(DeviceKind.Mouse, L.T("ATK fare", "ATK mouse"), null, false, "...", Present: false);
    private DeviceStatus _keyboard = new(DeviceKind.Keyboard, L.T("Klavye", "Keyboard"), null, false, "...", Present: false);
    private DeviceStatus _asusKeyboard = new(DeviceKind.Keyboard, L.T("ASUS klavye", "ASUS keyboard"), null, false, "...", Present: false);
    private IReadOnlyList<DeviceStatus> _logitech = Array.Empty<DeviceStatus>();
    private IReadOnlyList<DeviceStatus> _xbox = Array.Empty<DeviceStatus>();
    private readonly System.Windows.Forms.Timer _timer;
    private Icon? _currentIcon;
    private bool _busy;

    // Devices pinned to the tray ("Show in tray"): one icon each. While none is pinned, the single headset icon above is used.
    private sealed class PinnedIcon
    {
        public PinnedIcon(NotifyIcon tray) => Tray = tray;
        public NotifyIcon Tray { get; }
        public Icon? Current { get; set; }
        public string LastKey { get; set; } = "";
    }
    private List<PinnedDevice> _pinned = TrayPins.Load();
    private readonly Dictionary<string, PinnedIcon> _pinIcons = new();
    private readonly System.Windows.Forms.Timer _pinBlink;
    private bool _pinBlinkOn;

    // Battery icon state
    private readonly System.Windows.Forms.Timer _blink;
    private int _pct;
    private int _state = -1;
    private bool _blinkOn;

    public TrayApp()
    {
        // The single headset icon is only shown while no device is pinned to the tray
        _tray = new NotifyIcon { ContextMenuStrip = BuildMenu(), Visible = _pinned.Count == 0, Text = L.T("Cihazlar aranıyor...", "Looking for devices...") };
        SetIcon("?", Color.Gray);
        _tray.MouseClick += OnTrayClick;
        _popup.NearbyProvider = AirPods.Nearby;
        // While the panel is open, AirPods values show up within a second of opening the case lid
        _popup.ItemsProvider = () => { _airpods = AirPods.Current() ?? _airpods; SyncPinned(); return Items; };
        AirPods.Start();   // scanning always runs for the Nearby tab

        // Timer that alternates between the battery icon and the charging device icon
        _blink = new System.Windows.Forms.Timer { Interval = 1500 };
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; RenderBattery(); };

        // Same for the pinned icons: a charging device alternates between its percentage and its glyph
        _pinBlink = new System.Windows.Forms.Timer { Interval = 1500 };
        _pinBlink.Tick += (_, _) => { _pinBlinkOn = !_pinBlinkOn; SyncPinned(); };
        SyncPinned();   // pinned icons appear right away (with "–") instead of after the first read

        _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // The full refresh (all devices, Bluetooth) runs every 30 s; the headset alone is cheap to query, so it is polled
        // more often to notice it being switched on/off or the receiver being unplugged within seconds.
        _headsetPoll = new System.Windows.Forms.Timer { Interval = 5_000 };
        _headsetPoll.Tick += async (_, _) => await PollHeadsetAsync();
        _headsetPoll.Start();
        _ = RefreshAsync();
    }

    private readonly System.Windows.Forms.Timer _headsetPoll;

    private async Task PollHeadsetAsync()
    {
        if (_busy) return;   // a full refresh is already reading the headset
        _busy = true;
        try
        {
            var info = await Task.Run(() => _device.Read());
            UpdateHeadsetStatus(info);
            UpdateTrayIcon(info);
            _xbox = XboxController.Read();   // cheap, so controllers appear/disappear within seconds too
            SyncPinned();
            if (_popup.Visible) _popup.SetItems(Items);
        }
        catch (Exception ex) { Log.W("headset poll error: " + ex.Message); }
        finally { _busy = false; }
    }

    /// Right-click menu. Rebuilt from scratch when the language changes.
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Pil durumu", "Battery status"), null, (_, _) => { _popup.SetItems(Items); _popup.ShowAt(Cursor.Position); });
        menu.Items.Add(L.T("Yenile", "Refresh"), null, async (_, _) => await RefreshAsync());

        // Pin devices to the tray: a checked device gets its own icon with its own percentage.
        // The list is filled when the submenu opens, from what is online at that moment.
        var pin = new ToolStripMenuItem(L.T("Tepside göster", "Show in tray"));
        pin.DropDownItems.Add(new ToolStripMenuItem("..."));
        pin.DropDownOpening += (_, _) => FillPinMenu(pin);
        menu.Items.Add(pin);

        // "Turn off <controller>" items, shown only while an Xbox controller is connected (filled when the menu opens)
        var xboxItems = new List<ToolStripItem>();
        menu.Opening += (_, _) =>
        {
            foreach (var old in xboxItems) menu.Items.Remove(old);
            xboxItems.Clear();
            int at = menu.Items.IndexOf(pin) + 1;
            for (uint i = 0; i < 4; i++)
            {
                string name = XboxController.NameOf(i);
                if (_xbox.All(d => d.Name != name)) continue;
                uint slot = i;
                var item = new ToolStripMenuItem(L.T($"{name} kapat", $"Turn off {name}"));
                item.Click += (_, _) =>
                {
                    if (!XboxController.PowerOff(slot))
                        BalloonHost.ShowBalloonTip(3000, name, L.T("Kapatılamadı", "Could not turn it off"), ToolTipIcon.Warning);
                };
                menu.Items.Insert(at++, item);
                xboxItems.Add(item);
            }
        };

        menu.Items.Add(L.T("Tanılama", "Diagnostics"), null, async (_, _) =>
        {
            // Writes every connected HID device and the responses of supported brands to the log, then opens it.
            // This log is all that's needed to add a new device or troubleshoot a problem.
            Log.StartVerbose(TimeSpan.FromMinutes(2));   // also capture the live device traces for a couple of minutes
            await Task.Run(RunDiagnostics);
            OpenLog();
        });
        menu.Items.Add(L.T("Logu aç", "Open log"), null, (_, _) => OpenLog());

        // Language selection
        // Always reads "Language" so someone stuck in the wrong language can still find it
        var lang = new ToolStripMenuItem("Language");
        foreach (var (value, text) in new[] { (L.Lang.Auto, L.T("Otomatik (Windows dili)", "Automatic (Windows language)")),
                                              (L.Lang.Tr, "Türkçe"), (L.Lang.En, "English") })
        {
            var item = new ToolStripMenuItem(text) { Checked = L.Setting == value };
            item.Click += (_, _) => ChangeLanguage(value);
            lang.DropDownItems.Add(item);
        }

        // Start with Windows (right above Exit)
        var autoStart = new ToolStripMenuItem(L.T("Windows ile başlat", "Start with Windows")) { Checked = AutoStart.IsEnabled };
        autoStart.Click += (_, _) =>
        {
            AutoStart.Set(!autoStart.Checked);
            autoStart.Checked = AutoStart.IsEnabled;
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(lang);
        menu.Items.Add(autoStart);

        // Exit is always last
        menu.Items.Add(L.T("Çıkış", "Exit"), null, (_, _) => ExitThread());
        return menu;
    }

    private static void OpenLog()
    {
        if (!File.Exists(Log.File_)) File.WriteAllText(Log.File_, "");
        Process.Start(new ProcessStartInfo(Log.File_) { UseShellExecute = true });
    }

    private void ChangeLanguage(L.Lang lang)
    {
        L.Set(lang);
        // The old menu is the one being clicked right now, so it isn't disposed here
        _tray.ContextMenuStrip = BuildMenu();
        foreach (var p in _pinIcons.Values) p.Tray.ContextMenuStrip = _tray.ContextMenuStrip;
        _popup.Invalidate();
        _ = RefreshAsync();   // regenerate device status texts in the new language
    }

    // Low battery alert: one Windows notification per device at 10 % and at 5 %.
    // Alerts reset once the device is put on charge or goes above 15 %.
    private readonly Dictionary<string, int> _warnedAt = new();   // keyed by device name

    private void CheckLowBattery()
    {
        foreach (var d in Items)
        {
            if (d.Charging || d.Percent is null or >= 15)
            {
                if (d.Charging || d.Percent >= 15) _warnedAt.Remove(d.Name);
                continue;
            }

            int p = d.Percent.Value;
            int level = p < 5 ? 5 : p < 10 ? 10 : 0;
            if (level == 0) continue;
            if (_warnedAt.TryGetValue(d.Name, out int prev) && prev <= level) continue;

            _warnedAt[d.Name] = level;
            Log.W($"low battery alert: {d.Name} {p}%");
            BalloonHost.ShowBalloonTip(10_000,
                level == 5 ? L.T("Pil kritik seviyede", "Battery critical") : L.T("Pil azaldı", "Battery low"),
                // Approximate readings (AirPods in 10 % steps, Logitech voltage/level) are marked with "~"
                L.T($"{d.Name}: {(d.Approx || d.Kind == DeviceKind.Earbuds ? "~" : "")}%{p} kaldı. Şarj etmeyi ya da pili değiştirmeyi unutma.",
                    $"{d.Name}: {(d.Approx || d.Kind == DeviceKind.Earbuds ? "~" : "")}{p}% left. Don't forget to charge it or replace the battery."),
                level == 5 ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }
    }

    private DeviceStatus? _airpods;   // only listed once its advertisement has been seen

    /// Every device slot with a stable id, which is what "Show in tray" remembers. Includes devices that are offline.
    private IEnumerable<(string Id, DeviceStatus Status)> Slots()
    {
        yield return ("headset", _headset);
        yield return ("mouse", _mouse);
        yield return ("atk-mouse", _atkMouse);
        yield return ("keyboard", _keyboard);
        yield return ("asus-keyboard", _asusKeyboard);
        if (_airpods is not null) yield return ("airpods", _airpods);
        foreach (var d in _logitech) yield return ("logitech:" + d.Name, d);
        foreach (var d in _xbox) yield return ("xbox:" + d.Name, d);
    }

    /// What the panel lists: only devices that report something (see DeviceFilter).
    private IReadOnlyList<DeviceStatus> Items => Slots().Select(s => s.Status).Where(DeviceFilter.IsOnline).ToList();

    // ---- Pinned tray icons ------------------------------------------------------------------------------

    /// Balloon notifications need a visible icon, and the headset icon is hidden while devices are pinned.
    private NotifyIcon BalloonHost => _tray.Visible ? _tray : _pinIcons.Values.FirstOrDefault()?.Tray ?? _tray;

    /// Makes the pinned icons match the pinned list and the latest readings: creates, updates and removes them.
    private void SyncPinned()
    {
        var online = new Dictionary<string, DeviceStatus>();
        foreach (var (id, st) in Slots())
            if (DeviceFilter.IsOnline(st)) online.TryAdd(id, st);

        bool namesChanged = false;
        for (int i = 0; i < _pinned.Count; i++)
        {
            var pin = _pinned[i];
            online.TryGetValue(pin.Id, out var st);
            bool standby = st is { Standby: true };   // receiver plugged in but its device is off: nothing worth a tray icon

            // Remember the name the device reports, for the tooltip while it is offline
            if (st is not null && st.Name != pin.Name)
            {
                _pinned[i] = pin = pin with { Name = st.Name };
                namesChanged = true;
            }

            if (_pinIcons.TryGetValue(pin.Id, out var icon))
            {
                RenderPinned(icon, pin.Name, TrayPins.ColorOf(pin, i), st);
                icon.Tray.Visible = !standby;
                continue;
            }
            icon = new PinnedIcon(new NotifyIcon { ContextMenuStrip = _tray.ContextMenuStrip });
            icon.Tray.MouseClick += OnTrayClick;
            _pinIcons[pin.Id] = icon;
            RenderPinned(icon, pin.Name, TrayPins.ColorOf(pin, i), st);
            icon.Tray.Visible = !standby;   // only once it has an image
        }
        if (namesChanged) TrayPins.Save(_pinned);

        // Icons of devices that were unpinned
        foreach (var id in _pinIcons.Keys.Where(k => _pinned.All(p => p.Id != k)).ToList())
        {
            var icon = _pinIcons[id];
            _pinIcons.Remove(id);
            icon.Tray.Visible = false;
            icon.Tray.Dispose();
            icon.Current?.Dispose();
        }

        // The headset icon stands in whenever no pinned icon is visible, so the app always has a tray presence
        _tray.Visible = _pinned.All(p => _pinIcons.TryGetValue(p.Id, out var pi) is false || !pi.Tray.Visible);
        // A charging pinned device alternates between its percentage and its glyph; a critically low one (<= 5 %)
        // flashes between its own color and red. Low-battery flashing is faster, to catch the eye.
        bool anyCharging = _pinned.Any(p => online.TryGetValue(p.Id, out var s) && s.Charging);
        bool anyCritical = _pinned.Any(p => online.TryGetValue(p.Id, out var s) && IsCritical(s));
        _pinBlink.Interval = anyCritical ? 700 : 1500;
        if (anyCharging || anyCritical) { if (!_pinBlink.Enabled) _pinBlink.Start(); }
        else if (_pinBlink.Enabled) { _pinBlink.Stop(); _pinBlinkOn = false; }
    }

    private static bool IsCritical(DeviceStatus s) => s.Percent is <= 5 && !s.Charging;

    /// d = null: the device is offline (a dash is drawn). Skips the redraw when nothing visible changed.
    private void RenderPinned(PinnedIcon pin, string name, Color textColor, DeviceStatus? d)
    {
        int S = Math.Max(SystemInformation.SmallIconSize.Width, 16);
        bool onCable = d is { Percent: null, Wired: true };   // plugged in, nothing to show as a percentage
        bool glyph = d is not null && (onCable || (d.Charging && _pinBlinkOn));
        bool critical = d is not null && IsCritical(d);

        string key = $"{S}|{name}|{textColor.ToArgb()}|{d?.Percent}|{d?.Charging}|{d?.Kind}|{d?.Note}|{d is null}|{glyph}|{critical && _pinBlinkOn}";
        if (key == pin.LastKey) return;
        pin.LastKey = key;

        using (var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                if (glyph) DrawDeviceGlyph(g, S, d!.Kind, Green);
                else if (d?.Percent is int p)
                    DrawNumber(g, S, (critical && _pinBlinkOn) || (p < 10 && !critical && !d.Charging) ? Color.FromArgb(255, 72, 72) : textColor, Math.Clamp(p, 0, 100));
                else DrawDash(g, S, Color.Gray);
            }
            var icon = ToIcon(bmp);
            pin.Tray.Icon = icon;
            pin.Current?.Dispose();
            pin.Current = icon;
        }
        pin.Tray.Text = TrayText.Tooltip(name, d);
    }

    /// Fills the "Show in tray" submenu: online devices, plus pinned ones that are offline right now (so they can be unpinned).
    private void FillPinMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var entries = new List<(string Id, string Name, bool Online)>();
        foreach (var (id, st) in Slots())
            if (DeviceFilter.IsOnline(st) && entries.All(e => e.Id != id)) entries.Add((id, st.Name, true));
        foreach (var p in _pinned)
            if (entries.All(e => e.Id != p.Id)) entries.Add((p.Id, p.Name, false));

        if (entries.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem(L.T("Cihaz yok", "No devices")) { Enabled = false });
        foreach (var (id, name, isOnline) in entries)
        {
            var item = new ToolStripMenuItem(isOnline ? name : name + L.T(" (çevrimdışı)", " (offline)"))
                { Checked = _pinned.Any(p => p.Id == id) };
            item.Click += (_, _) => TogglePinned(id, name);
            menu.DropDownItems.Add(item);
        }
        if (entries.Any(e => e.Online && _pinned.All(p => p.Id != e.Id)))
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            var all = new ToolStripMenuItem(L.T("Tümünü tepsiye ekle", "Show all in tray"));
            all.Click += (_, _) => PinAll(entries.Where(e => e.Online).Select(e => new PinnedDevice(e.Id, e.Name)));
            menu.DropDownItems.Add(all);
        }
        // Text color per pinned device, to tell the icons apart
        if (_pinned.Count > 0)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < _pinned.Count; i++)
            {
                var pin = _pinned[i];
                var current = TrayPins.ColorOf(pin, i).ToArgb();
                var colorMenu = new ToolStripMenuItem(pin.Name + " – " + L.T("yazı rengi", "text color"));
                foreach (var (en, tr, value) in TrayPins.Palette)
                {
                    var c = value;
                    var item = new ToolStripMenuItem(L.T(tr, en)) { Checked = c.ToArgb() == current, ForeColor = Color.Black, BackColor = c };
                    item.Click += (_, _) => SetPinColor(pin.Id, c.ToArgb());
                    colorMenu.DropDownItems.Add(item);
                }
                menu.DropDownItems.Add(colorMenu);
            }
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(
            L.T("Hiçbiri seçili değilse tek kulaklık simgesi görünür", "With none checked, one headset icon is shown"))
            { Enabled = false });
    }

    /// "Show all in tray": pins every online device that isn't pinned yet (each gets its own color).
    private void PinAll(IEnumerable<PinnedDevice> devices)
    {
        foreach (var d in devices)
            if (_pinned.All(p => p.Id != d.Id)) _pinned = TrayPins.Toggle(_pinned, d);
        TrayPins.Save(_pinned);
        SyncPinned();
    }

    private void SetPinColor(string id, int argb)
    {
        int i = _pinned.FindIndex(p => p.Id == id);
        if (i < 0) return;
        _pinned[i] = _pinned[i] with { Color = argb };
        TrayPins.Save(_pinned);
        SyncPinned();
    }

    private void TogglePinned(string id, string name)
    {
        _pinned = TrayPins.Toggle(_pinned, new PinnedDevice(id, name));
        TrayPins.Save(_pinned);
        SyncPinned();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_popup.Visible) { _popup.Hide(); return; }
        // Clicking the icon first makes the panel lose focus and close; don't reopen it right away
        if ((DateTime.Now - _popup.LastHidden).TotalMilliseconds < 300) return;

        _popup.SetItems(Items);
        _popup.ShowAt(Cursor.Position);
        _ = RefreshAsync();
    }

    // An ASUS keyboard that fell asleep stops answering; keep showing its last reading, marked with its time
    private (int Pct, DateTime At)? _asusKbLast;

    private DeviceStatus KeepAsleepReading(DeviceStatus d)
    {
        if (d.Percent is int p) { _asusKbLast = (p, DateTime.Now); return d; }
        bool silent = d.Note == L.T("Uykuda / kapalı", "Asleep / off") || d.Note == L.T("Cevap yok", "No response");
        if (d.Present && silent && _asusKbLast is { } last)
            return d with { Percent = last.Pct, Charging = false, Note = L.T("Uykuda · son ", "Asleep · last ") + $"{last.At:HH:mm}" };
        return d;
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // Mouse and keyboards are read in the background; errors there never affect the headset
            var others = Task.Run(() =>
            {
                DeviceStatus m, am, k, ak;
                IReadOnlyList<DeviceStatus> lg;
                try { m = RogMouse.Read(); } catch (Exception ex) { Log.W("mouse: " + ex); m = _mouse with { Percent = null, Note = L.T("Okunamadı", "Read failed") }; }
                try { am = AtkMouse.Read(); } catch (Exception ex) { Log.W("ATK mouse: " + ex); am = _atkMouse with { Percent = null, Details = null, Note = L.T("Okunamadı", "Read failed") }; }
                try { k = Keychron.Read(); } catch (Exception ex) { Log.W("keyboard: " + ex); k = _keyboard with { Percent = null, Note = L.T("Okunamadı", "Read failed") }; }
                try { ak = AsusKeyboard.Read(); } catch (Exception ex) { Log.W("ASUS keyboard: " + ex); ak = _asusKeyboard with { Percent = null, Note = L.T("Okunamadı", "Read failed") }; }
                try { lg = Logitech.Read(); } catch (Exception ex) { Log.W("Logitech: " + ex); lg = _logitech; }
                return (m, am, k, ak, lg);
            });

            var info = await Task.Run(() => _device.Read());
            UpdateHeadsetStatus(info);
            UpdateTrayIcon(info);   // update the icon as soon as the headset is read, don't wait for the others

            (_mouse, _atkMouse, _keyboard, _asusKeyboard, _logitech) = await others;
            _asusKeyboard = KeepAsleepReading(_asusKeyboard);
            _xbox = XboxController.Read();
            try { _airpods = await AirPods.ReadAsync(); } catch (Exception ex) { Log.W("airpods: " + ex.Message); }
            CheckLowBattery();
            SyncPinned();
            if (_popup.Visible) _popup.SetItems(Items);
            UpdateTrayIcon(info);   // the set of charging devices may have changed
        }
        catch (Exception ex)
        {
            Log.W("refresh error: " + ex);
            _blink.Stop();
            SetIcon("!", Color.OrangeRed);
            var tip = L.T("Hata: ", "Error: ") + ex.Message;
            _tray.Text = tip.Length > 120 ? tip[..120] : tip;
        }
        finally
        {
            _busy = false;
            // While the headset is off, retry every 5 s so it shows up right after it's turned on; 30 s is enough once connected
            _timer.Interval = _headset.Percent is null ? 5_000 : 30_000;
        }
    }

    private void UpdateTrayIcon(BatteryInfo? info)
    {
        if (info is null)
        {
            _blink.Stop();
            SetIcon("–", Color.Gray);
            var tip = L.T("Kulaklık: ", "Headset: ") + (_device.LastError ?? L.T("bağlı değil", "not connected"));
            _tray.Text = tip.Length > 120 ? tip[..120] : tip;
            return;
        }

        _pct = (int)Math.Round(info.Percent);
        _state = info.ChargeState;
        string state = info.ChargeState switch
        {
            0 => L.T("pilde", "on battery"),
            1 => L.T("şarj oluyor", "charging"),
            2 => L.T("dolu", "full"),
            _ => L.T("durum bilinmiyor", "unknown state")
        };

        // Alternate between the two images only while some device is charging
        if (Items.Any(i => i.Charging))
        {
            if (!_blink.Enabled) { _blinkOn = false; _blink.Start(); }
        }
        else
        {
            _blink.Stop();
            _blinkOn = false;
        }
        RenderBattery();
        var t = $"{_headset.Name}: {L.Pct(info.Percent, "0.#")} ({state})";
        _tray.Text = t.Length > 120 ? t[..120] : t;   // at most 127 characters
    }

    private void UpdateHeadsetStatus(BatteryInfo? info)
    {
        if (info is null)
        {
            // Standby: the receiver is plugged in but the headset isn't connected. Listed with that note instead of being hidden.
            _headset = _headset with { Name = _device.ProductName ?? _headset.Name, Percent = null, Charging = false,
                                       Present = _device.Found, Standby = _device.ReceiverOnly,
                                       Note = _device.LastError ?? L.T("Bağlı değil", "Not connected") };
            return;
        }
        string conn = _device.Path?.Contains("pid_0a96", StringComparison.OrdinalIgnoreCase) == true ? Notes.Cable : Notes.Wireless;
        string note = Notes.Make(conn, charging: info.ChargeState == 1, full: info.ChargeState == 2);
        _headset = _headset with { Name = _device.ProductName ?? _headset.Name, Present = true, Standby = false,
                                   Percent = (int)Math.Round(info.Percent), Charging = info.ChargeState == 1, Note = note };
    }

    // Tray icon:
    //  - No device charging: static, just the headset percentage (fills the whole icon)
    //  - A device is charging: headset percentage <-> green icon of the charging device
    //    (more than one device charging: three green vertical batteries side by side)
    // Red when the headset is below 10 % (and not charging).
    // Everything is drawn pixel-aligned, without anti-aliasing.
    private static readonly Color Green = Color.FromArgb(60, 200, 90);

    private void RenderBattery()
    {
        // Draw at the tray's real icon size (100% DPI = 16px, 150% = 24px ...)
        int S = Math.Max(SystemInformation.SmallIconSize.Width, 16);
        bool low = _pct < 10 && _state != 1;
        Color c = low ? Color.FromArgb(255, 72, 72) : Color.White;
        var charging = Items.Where(i => i.Charging).Select(i => i.Kind).ToList();

        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (charging.Count == 0 || !_blinkOn)
                DrawHeadsetWithNumber(g, S, c);
            else if (charging.Count == 1)
                DrawDeviceGlyph(g, S, charging[0], Green);
            else
                DrawThreeBatteries(g, S, Green);
        }
        ApplyBitmap(bmp);
    }

    // 3x5 pixel digits: small enough to fit, very crisp at 2x scale
    private static readonly string[][] Glyphs3x5 =
    {
        new[] { "###", "#.#", "#.#", "#.#", "###" }, // 0
        new[] { ".#.", "##.", ".#.", ".#.", "###" }, // 1
        new[] { "###", "..#", "###", "#..", "###" }, // 2
        new[] { "###", "..#", "###", "..#", "###" }, // 3
        new[] { "#.#", "#.#", "###", "..#", "..#" }, // 4
        new[] { "###", "#..", "###", "..#", "###" }, // 5
        new[] { "###", "#..", "###", "#.#", "###" }, // 6
        new[] { "###", "..#", "..#", "..#", "..#" }, // 7
        new[] { "###", "#.#", "###", "#.#", "###" }, // 8
        new[] { "###", "#.#", "###", "..#", "###" }, // 9
    };

    private static void DrawDigits(Graphics g, Brush b, string text, Rectangle area)
    {
        int n = text.Length;
        int unitsW = n * 3 + (n - 1);
        // Horizontal scale from the width, vertical scale from the height (at most 2x):
        // digits get a bit tall/narrow but fill the icon much better
        int sx = Math.Max(1, area.Width / unitsW);
        int sy = Math.Max(1, Math.Min(area.Height / 5, sx * 2));
        int tw = unitsW * sx, th = 5 * sy;
        int x = area.X + (area.Width - tw + 1) / 2;
        int y = area.Y + (area.Height - th + 1) / 2;
        foreach (char ch in text)
        {
            var gl = Glyphs3x5[ch - '0'];
            for (int r = 0; r < 5; r++)
                for (int col = 0; col < 3; col++)
                    if (gl[r][col] == '#') g.FillRectangle(b, x + col * sx, y + r * sy, sx, sy);
            x += 4 * sx;
        }
    }

    // Shared drawing of headband + two ear cups; returns the inner area
    private static Rectangle DrawHeadsetShape(Graphics g, int S, Color c)
    {
        using var b = new SolidBrush(c);
        int cupW = Math.Max(2, S / 8);
        int cupTop = (int)Math.Round(S * 0.45);
        int cupBottom = S - 1;
        int band = Math.Max(2, S / 12);

        // Ear cups
        g.FillRectangle(b, 0, cupTop, cupW, cupBottom - cupTop + 1);
        g.FillRectangle(b, S - cupW, cupTop, cupW, cupBottom - cupTop + 1);

        // Headband: half ellipse rising from the middle of the cups
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(c, band))
            g.DrawArc(pen, cupW / 2f, band / 2f, S - cupW, 2f * (cupTop - band / 2f) , 180, 180);
        g.SmoothingMode = SmoothingMode.None;

        // Area for digits: between the cups, from just below the band to the bottom of the icon
        int top = band + Math.Max(1, S / 8);
        return new Rectangle(cupW + 1, top, S - 2 * cupW - 2, S - top);
    }

    // Percentage only: digits fill the whole icon, pixel-scaled and centered
    private void DrawHeadsetWithNumber(Graphics g, int S, Color c) => DrawNumber(g, S, c, _pct);

    private static void DrawNumber(Graphics g, int S, Color c, int pct)
    {
        using var b = new SolidBrush(c);
        string text = pct.ToString();
        int n = text.Length;
        int unitsW = n * 3 + (n - 1);

        int sx = Math.Max(1, Math.Min(S / unitsW, S / 7));   // a single digit is as thick as two
        int sy = Math.Max(1, Math.Min(S / 5, sx * 2));
        int tw = unitsW * sx, th = 5 * sy;
        int x = (S - tw) / 2, y = (S - th) / 2;

        foreach (char ch in text)
        {
            var gl = Glyphs3x5[ch - '0'];
            for (int r = 0; r < 5; r++)
                for (int col = 0; col < 3; col++)
                    if (gl[r][col] == '#') g.FillRectangle(b, x + col * sx, y + r * sy, sx, sy);
            x += 4 * sx;
        }
    }

    /// "No reading": a short gray bar in the middle of the icon
    private static void DrawDash(Graphics g, int S, Color c)
    {
        using var b = new SolidBrush(c);
        int w = Math.Max(4, S / 2), h = Math.Max(2, S / 8);
        g.FillRectangle(b, (S - w) / 2, (S - h) / 2, w, h);
    }

    private static void DrawDeviceGlyph(Graphics g, int S, DeviceKind kind, Color c)
    {
        using var b = new SolidBrush(c);
        int t = Math.Max(1, S / 12);   // line thickness
        switch (kind)
        {
            case DeviceKind.Earbuds:
            {
                // Two AirPods: round head + stem going down
                int head = Math.Max(3, S * 5 / 16), stemW = Math.Max(1, S / 8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (int cx in new[] { S / 4, S * 3 / 4 })
                {
                    g.FillEllipse(b, cx - head / 2f, 1, head, head);
                    g.FillRectangle(b, cx + (cx < S / 2 ? head / 2 - stemW : -head / 2), head / 2, stemW, S - head / 2 - 1);
                }
                g.SmoothingMode = SmoothingMode.None;
                break;
            }

            case DeviceKind.Headset:
                DrawHeadsetShape(g, S, c);
                break;

            case DeviceKind.Mouse:
            {
                // Vertical mouse body, button split and wheel
                int w = (int)(S * 0.56), h = S - 2;
                var body = new Rectangle((S - w) / 2, 1, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(c, t + 0.5f))
                using (var p = RoundedPath(body, w / 2f))
                    g.DrawPath(pen, p);
                g.SmoothingMode = SmoothingMode.None;
                int midX = S / 2;
                int split = body.Y + (int)(h * 0.42);
                g.FillRectangle(b, body.X + t, split, body.Width - 2 * t, t);       // horizontal split
                g.FillRectangle(b, midX - t / 2, body.Y + t, t, split - body.Y - t); // vertical split
                break;
            }

            case DeviceKind.Gamepad:
            {
                // Controller: rounded body, d-pad cross on the left, two buttons on the right
                int w = S, h = (int)(S * 0.62);
                var body = new Rectangle(0, (S - h) / 2, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(c, t + 0.5f))
                using (var p = RoundedPath(body, h / 2f))
                    g.DrawPath(pen, p);
                g.SmoothingMode = SmoothingMode.None;
                int arm = Math.Max(1, S / 8), cx = body.X + w / 4, cy = body.Y + h / 2;
                g.FillRectangle(b, cx - arm, cy - t / 2, 2 * arm + 1, t);
                g.FillRectangle(b, cx - t / 2, cy - arm, t, 2 * arm + 1);
                int dot = Math.Max(1, S / 8);
                g.FillRectangle(b, body.Right - w / 4 - dot, cy - dot, dot, dot);
                g.FillRectangle(b, body.Right - w / 4 + 1, cy, dot, dot);
                break;
            }

            case DeviceKind.Keyboard:
            {
                // Horizontal keyboard: frame + key dots + space bar
                int h = (int)(S * 0.62);
                var body = new Rectangle(0, (S - h) / 2, S, h);
                g.FillRectangle(b, body.X, body.Y, body.Width, t);
                g.FillRectangle(b, body.X, body.Bottom - t, body.Width, t);
                g.FillRectangle(b, body.X, body.Y, t, body.Height);
                g.FillRectangle(b, body.Right - t, body.Y, t, body.Height);

                int key = Math.Max(1, S / 12), gap = Math.Max(1, S / 12);
                int cols = (body.Width - 2 * t - gap) / (key + gap);
                int startX = body.X + (body.Width - (cols * (key + gap) - gap)) / 2;
                int row1 = body.Y + t + gap, row2 = row1 + key + gap;
                for (int i = 0; i < cols; i++)
                {
                    g.FillRectangle(b, startX + i * (key + gap), row1, key, key);
                    g.FillRectangle(b, startX + i * (key + gap), row2, key, key);
                }
                int spaceY = row2 + key + gap;
                if (spaceY + key <= body.Bottom - t)
                    g.FillRectangle(b, body.X + body.Width / 4, spaceY, body.Width / 2, key);
                break;
            }
        }
    }

    private static void DrawThreeBatteries(Graphics g, int S, Color c)
    {
        using var b = new SolidBrush(c);
        int gap = Math.Max(1, S / 12);
        int w = (S - 2 * gap) / 3;
        int nubH = Math.Max(1, S / 12), nubW = Math.Max(1, w / 2);
        int top = 1 + nubH, h = S - 2 - nubH;
        int x0 = (S - (3 * w + 2 * gap)) / 2;
        for (int i = 0; i < 3; i++)
        {
            int x = x0 + i * (w + gap);
            g.FillRectangle(b, x + (w - nubW) / 2, top - nubH, nubW, nubH); // terminal
            g.FillRectangle(b, x, top, w, h);                               // body
        }
    }

    private static GraphicsPath RoundedPath(Rectangle r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Bahnschrift (normal width) for readable digits (ships with Windows 10/11), otherwise Segoe UI
    private static FontFamily? _family;
    private static FontFamily PickFont()
    {
        if (_family != null) return _family;
        foreach (var name in new[] { "Bahnschrift", "Segoe UI" })
        {
            var f = FontFamily.Families.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f != null) return _family = f;
        }
        return _family = FontFamily.GenericSansSerif;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// An icon that owns its own handle (the temporary GDI handle is released right away).
    private static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    private void ApplyBitmap(Bitmap bmp)
    {
        var icon = ToIcon(bmp);
        _tray.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private void SetIcon(string text, Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float size = text.Length >= 3 ? 15f : 21f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, 32, 32), fmt);
        }
        ApplyBitmap(bmp);
    }

    /// Diagnostics: dump of all HID devices + brand-specific probes (ASUS, ATK/VXE).
    private static void RunDiagnostics()
    {
        Log.W("========== Diagnostics started ==========");
        Log.W($"BatteryTray {typeof(TrayApp).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}");
        try
        {
            foreach (var g in DeviceList.Local.GetHidDevices().GroupBy(d => (d.VendorID, d.ProductID)).OrderBy(g => g.Key.VendorID))
            {
                var first = g.First();
                string mfr = "?", name = "?";
                try { mfr = first.GetManufacturer(); } catch { }
                try { name = first.GetProductName(); } catch { }
                Log.W($"[HID] VID=0x{g.Key.VendorID:X4} PID=0x{g.Key.ProductID:X4} \"{mfr}\" \"{name}\" ({g.Count()} interfaces)");
                foreach (var d in g)
                {
                    string usages = "?";
                    try
                    {
                        usages = string.Join(",", d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues())
                            .Select(u => "0x" + (u >> 16).ToString("X4")).Distinct());
                    }
                    catch { }
                    Log.W($"        pages={usages} in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} " +
                          $"feat={d.GetMaxFeatureReportLength()} {d.DevicePath}");
                }
            }
        }
        catch (Exception ex) { Log.W("[HID] dump error: " + ex.Message); }

        try { AsusKeyboard.Diagnose(); } catch (Exception ex) { Log.W("[ASUS diag] " + ex); }
        try { AtkMouse.Diagnose(); } catch (Exception ex) { Log.W("[ATK diag] " + ex); }
        Log.W("========== Diagnostics finished ==========");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _headsetPoll.Dispose();
            _popup.Dispose();
            AirPods.Stop();
            _blink.Dispose();
            _pinBlink.Dispose();
            foreach (var p in _pinIcons.Values) { p.Tray.Visible = false; p.Tray.Dispose(); p.Current?.Dispose(); }
            _tray.Visible = false;
            _tray.Dispose();
            _currentIcon?.Dispose();
            _device.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// Start with Windows: HKCU\...\Run key (no admin rights needed)
internal static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BatteryTray";
    private const string OldName = "HS80Tray";   // entry left by the old version

    public static bool IsEnabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enable)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, writable: true)
                      ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        if (enable) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
        k.DeleteValue(OldName, throwOnMissingValue: false);   // remove the old HS80Tray entry
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "BatteryTray_SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
