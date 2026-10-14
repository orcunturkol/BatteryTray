using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// Small dark Windows 11-style panel that opens when the tray icon is clicked.
/// For each device: icon, name, status note, percentage and a small battery bar.
internal sealed class PopupForm : Form
{
    private IReadOnlyList<DeviceStatus> _items = Array.Empty<DeviceStatus>();
    private IReadOnlyList<DeviceStatus> _nearby = Array.Empty<DeviceStatus>();
    private int _tab;   // 0 = My devices, 1 = Nearby

    /// Data for the "Nearby" tab, refreshed every second while the panel is open
    public Func<IReadOnlyList<DeviceStatus>>? NearbyProvider { get; set; }
    /// Cheap refresh of the "My devices" tab (cached values only), called every second while the panel is open
    public Func<IReadOnlyList<DeviceStatus>>? ItemsProvider { get; set; }
    private readonly System.Windows.Forms.Timer _nearTimer = new() { Interval = 1000 };
    public DateTime LastHidden { get; private set; }

    // Layout sizes at 100 % scaling (96 DPI); D() scales them to the display's DPI
    private const int W = 320, Header = 52, RowH = 62, DetailsRowH = 80, PartsRowH = 118, Footer = 14;

    private float _k = 1f;
    private int D(int v) => (int)Math.Round(v * _k);

    /// Font size given in points at 96 DPI, converted to pixels and scaled, so text and layout scale together
    private Font F(string family, float pt) => new(family, pt * 96f / 72f * _k, GraphicsUnit.Pixel);

    // Devices with more than one battery (AirPods) get a taller row with columns;
    // devices with extra details (a mouse's DPI / polling rate) get one more text line
    private int RowHeight(DeviceStatus d) =>
        D(d.Parts is { Count: > 0 } ? PartsRowH : d.Details is { Length: > 0 } ? DetailsRowH : RowH);

    private static readonly Color Bg = Color.FromArgb(32, 32, 32);
    private static readonly Color RowBg = Color.FromArgb(43, 43, 43);
    private static readonly Color Text1 = Color.FromArgb(245, 245, 245);
    private static readonly Color Text2 = Color.FromArgb(160, 160, 160);
    private static readonly Color Track = Color.FromArgb(70, 70, 70);

    private readonly string _iconFont;

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        KeyPreview = true;
        _k = DeviceDpi / 96f;
        Width = D(W);
        Height = D(Header + RowH * 3 + Footer);

        _iconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        Deactivate += (_, _) => HidePopup();
        MouseClick += OnClick;
        _nearTimer.Tick += (_, _) =>
        {
            if (!Visible) return;
            if (_tab == 1) RefreshNearby();
            else if (ItemsProvider is not null) SetItems(ItemsProvider());
        };
        _nearTimer.Start();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW (hidden from Alt+Tab)
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: rounded corners + dark theme
        try
        {
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
        catch { /* these attributes don't exist on Windows 10, that's fine */ }
    }

    public void SetItems(IReadOnlyList<DeviceStatus> items)
    {
        _items = items;
        Relayout();
    }

    private void RefreshNearby()
    {
        _nearby = NearbyProvider?.Invoke() ?? Array.Empty<DeviceStatus>();
        Relayout();
    }

    private IReadOnlyList<DeviceStatus> Rows => _tab == 0 ? _items : _nearby;

    /// Resize to fit the rows; keep the bottom edge fixed while the panel is open
    private void Relayout()
    {
        _k = DeviceDpi / 96f;
        Width = D(W);
        int h = D(Header) + (Rows.Count == 0 ? D(RowH) : Rows.Sum(RowHeight)) + D(Footer);
        if (h != Height)
        {
            int bottom = Bottom;
            Height = h;
            if (Visible) Top = bottom - h;
        }
        Invalidate();
    }

    private void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Y > D(Header - 6)) return;
        int tab = e.X < Width / 2 ? 0 : 1;
        if (tab == _tab) return;
        _tab = tab;
        if (_tab == 1) RefreshNearby(); else Relayout();
    }

    /// Centers the panel right above the clicked point (the tray icon).
    public void ShowAt(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = anchor.X - Width / 2;
        x = Math.Clamp(x, wa.Left + D(8), wa.Right - Width - D(8));
        int y = wa.Bottom - Height - D(12);   // assumes the taskbar is at the bottom
        if (anchor.Y < wa.Top)              // taskbar at the top
            y = wa.Top + D(12);
        Location = new Point(x, y);
        Show();
        Activate();
    }

    private void HidePopup()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.Now;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var nameFont = F("Segoe UI Semibold", 10f);
        using var noteFont = F("Segoe UI", 8.5f);
        using var pctFont = F("Segoe UI Semibold", 13f);
        using var iconFont = new Font(_iconFont, 18f * _k, GraphicsUnit.Pixel);
        using var t1 = new SolidBrush(Text1);
        using var t2 = new SolidBrush(Text2);

        // Tabs
        using (var tabFont = F("Segoe UI Semibold", 10f))
        {
            string[] tabs = { L.T("Cihazlarım", "My devices"), L.T("Yakındakiler", "Nearby") };
            for (int i = 0; i < 2; i++)
            {
                var r = new Rectangle(i * Width / 2, D(10), Width / 2, D(28));
                TextRenderer.DrawText(g, tabs[i], tabFont, r, i == _tab ? Text1 : Text2,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (i == _tab)
                    using (var ub = new SolidBrush(Color.FromArgb(60, 200, 90)))
                    using (var up = Rounded(new Rectangle(r.X + r.Width / 2 - D(24), r.Bottom + D(2), D(48), D(3)), D(1)))
                        g.FillPath(ub, up);
            }
        }

        int y = D(Header);
        if (Rows.Count == 0)
        {
            TextRenderer.DrawText(g, _tab == 1 ? L.T("Yakında Bluetooth cihazı bulunamadı", "No Bluetooth devices nearby") : L.T("Cihaz yok", "No devices"), noteFont,
                new Rectangle(D(10), y, Width - D(20), D(RowH - 8)), Text2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        foreach (var it in Rows)
        {
            var row = new Rectangle(D(10), y, Width - D(20), RowHeight(it) - D(8));
            using (var rb = new SolidBrush(RowBg))
            using (var rp = Rounded(row, D(8)))
                g.FillPath(rb, rp);

            // Device icon (Segoe Fluent / MDL2 glyphs)
            string glyph = it.Kind switch
            {
                DeviceKind.Headset or DeviceKind.Earbuds => "",
                DeviceKind.Other => "\uE702",   // Bluetooth
                DeviceKind.Mouse => "",
                _ => "",
            };
            var iconRect = new Rectangle(row.X + D(10), row.Y, D(32), D(RowH - 8));   // icon always in the top part
            TextRenderer.DrawText(g, glyph, iconFont, iconRect, Text1,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Name and note
            int tx = row.X + D(50);
            int textW = row.Width - D(50) - (it.Parts is { Count: > 0 } ? D(12) : D(90));
            TextRenderer.DrawText(g, it.Name, nameFont, new Rectangle(tx, row.Y + D(8), textW, D(22)), Text1,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string note = it.Note;   // the format is defined in one place by Notes.Make
            TextRenderer.DrawText(g, note, noteFont, new Rectangle(tx, row.Y + D(31), textW, D(18)), Text2,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (it.Details is { Length: > 0 } details)
                TextRenderer.DrawText(g, details, noteFont, new Rectangle(tx, row.Y + D(50), row.Width - D(50) - D(12), D(18)), Text2,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Right side: percentage + bar
            int rx = row.Right - D(80);
            if (it.Parts is { Count: > 0 } parts)
            {
                // Left / Right / Case: equal columns side by side in the lower part
                int pad = D(12), gap = D(8);
                int colW = (row.Width - 2 * pad - gap * (parts.Count - 1)) / parts.Count;
                int cy = row.Y + D(54);
                using var labelFont = F("Segoe UI", 8.5f);
                using var valFont = F("Segoe UI Semibold", 11.5f);
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    int cx = row.X + pad + i * (colW + gap);
                    TextRenderer.DrawText(g, part.Label, labelFont, new Rectangle(cx, cy, colW, D(16)), Text2,
                        TextFormatFlags.Left | TextFormatFlags.NoPadding);

                    string val = part.Percent is int pp
                        ? (part.Charging ? "⚡" : "") + (part.Approx ? "~" : "") + L.Pct(pp)
                        : "—";
                    TextRenderer.DrawText(g, val, valFont, new Rectangle(cx, cy + D(16), colW, D(24)),
                        part.Percent is null ? Text2 : Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding);

                    var bar = new Rectangle(cx, cy + D(42), colW, D(5));
                    using (var tb = new SolidBrush(Track))
                    using (var tp = Rounded(bar, D(2)))
                        g.FillPath(tb, tp);
                    if (part.Percent is int fp2)
                    {
                        Color pc = part.Charging || fp2 >= 25 ? Color.FromArgb(60, 200, 90)
                                 : fp2 < 10 ? Color.FromArgb(240, 70, 70) : Color.FromArgb(240, 190, 40);
                        int fw2 = Math.Max(D(5), bar.Width * fp2 / 100);
                        using var fb2 = new SolidBrush(pc);
                        using var fpath = Rounded(new Rectangle(bar.X, bar.Y, fw2, bar.Height), D(2));
                        g.FillPath(fb2, fpath);
                    }
                }
            }
            else if (it.Percent is int p)
            {
                Color c = it.Charging ? Color.FromArgb(60, 200, 90)
                        : p < 10 ? Color.FromArgb(240, 70, 70)
                        : p < 25 ? Color.FromArgb(240, 190, 40)
                        : Color.FromArgb(60, 200, 90);

                string pt = (it.Charging ? "⚡" : "") + (it.Approx ? "~" : "") + L.Pct(p);
                TextRenderer.DrawText(g, pt, pctFont, new Rectangle(rx, row.Y + D(6), D(70), D(28)), Text1,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);

                var bar = new Rectangle(rx + D(10), row.Y + D(36), D(60), D(6));
                using (var tb = new SolidBrush(Track))
                using (var tp = Rounded(bar, D(3)))
                    g.FillPath(tb, tp);
                int fw = Math.Max(D(6), bar.Width * p / 100);
                using (var fb = new SolidBrush(c))
                using (var fp = Rounded(new Rectangle(bar.X, bar.Y, fw, bar.Height), D(3)))
                    g.FillPath(fb, fp);
            }
            else if (it.Wired)
            {
                // Cable mode: a horizontal green plug instead of a percentage
                DrawPlug(g, row.Right - D(14), row.Y + D(RowH - 8) / 2, Color.FromArgb(60, 200, 90), _k);   // centered on the top part
            }
            else
            {
                TextRenderer.DrawText(g, "—", pctFont, new Rectangle(rx, row.Y + D(14), D(70), D(28)), Text2,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            y += RowHeight(it);
        }
    }

    /// Horizontal plug: cable on the left, body in the middle, two prongs on the right. (right, cy) = right edge and vertical center.
    private static void DrawPlug(Graphics g, int right, int cy, Color c, float k)
    {
        int S(int v) => (int)Math.Round(v * k);
        using var b = new SolidBrush(c);
        using var pen = new Pen(c, 3f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        int prongLen = S(8), bodyW = S(16), bodyH = S(18), cableLen = S(14);
        int bodyRight = right - prongLen;
        var body = new Rectangle(bodyRight - bodyW, cy - bodyH / 2, bodyW, bodyH);

        // Prongs
        g.FillRectangle(b, bodyRight, cy - S(6), prongLen, S(3));
        g.FillRectangle(b, bodyRight, cy + S(3), prongLen, S(3));
        // Body (rounded on the left)
        using (var gp = Rounded(body, S(5))) g.FillPath(b, gp);
        // Cable
        g.DrawLine(pen, body.X - cableLen, cy, body.X, cy);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (r.Width < d) d = r.Width;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
