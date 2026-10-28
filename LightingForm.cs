using System.Drawing;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// Keyboard lighting window (ROG Azoth): effect, colour, brightness and speed, applied with the "Apply" button.
/// Nothing is sent while the controls are being changed.
internal sealed class LightingForm : Form
{
    private static readonly Color Bg = Color.FromArgb(32, 32, 32);
    private static readonly Color Field = Color.FromArgb(52, 52, 52);
    private static readonly Color Text1 = Color.FromArgb(245, 245, 245);
    private static readonly Color Text2 = Color.FromArgb(160, 160, 160);
    private static readonly Color Good = Color.FromArgb(60, 200, 90);
    private static readonly Color Warn = Color.FromArgb(240, 190, 40);

    private readonly ComboBox _effect = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly Button _color = new() { FlatStyle = FlatStyle.Flat, Height = 30 };
    private readonly TrackBar _brightness = new() { Minimum = 0, Maximum = 100, TickFrequency = 25, SmallChange = 5, LargeChange = 25, AutoSize = false, Height = 32 };
    private readonly Label _brightnessValue = new() { AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly CheckBox _save = new() { AutoSize = true };
    private readonly Button _apply = new() { FlatStyle = FlatStyle.Flat, AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _off = new() { FlatStyle = FlatStyle.Flat, AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(350, 0) };
    private readonly Label _colorLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _speedLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private Color _picked;

    private static readonly AuraEffect[] Effects =
        { AuraEffect.Static, AuraEffect.Breathing, AuraEffect.ColorCycle, AuraEffect.Reactive, AuraEffect.StarryNight, AuraEffect.RainDrop };

    private static string EffectName(AuraEffect e) => e switch
    {
        AuraEffect.Static => L.T("Sabit", "Static"),
        AuraEffect.Breathing => L.T("Nefes alma", "Breathing"),
        AuraEffect.ColorCycle => L.T("Renk döngüsü", "Color cycle"),
        AuraEffect.Reactive => L.T("Tuş tepkisi", "Reactive (keys light up)"),
        AuraEffect.StarryNight => L.T("Yıldızlı gece", "Starry night"),
        _ => L.T("Yağmur damlası", "Rain drop"),
    };

    public LightingForm()
    {
        Text = L.T("Klavye aydınlatması", "Keyboard lighting");
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        BackColor = Bg;
        ForeColor = Text1;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var s = AsusLighting.Load();
        _picked = Color.FromArgb(s.Color.R, s.Color.G, s.Color.B);

        foreach (var e in Effects) _effect.Items.Add(EffectName(e));
        _effect.SelectedIndex = Math.Max(0, Array.IndexOf(Effects, s.Effect));
        foreach (var t in new[] { L.T("Yavaş", "Slow"), L.T("Normal", "Normal"), L.T("Hızlı", "Fast") }) _speed.Items.Add(t);
        _speed.SelectedIndex = (int)s.Speed;
        _brightness.Value = s.Brightness;
        _save.Checked = s.SaveToKeyboard;
        _save.Text = L.T("Klavyeye kaydet (kapatınca da kalır)", "Save on the keyboard (keeps it after closing)");

        _colorLabel.Text = L.T("Renk", "Color");
        _speedLabel.Text = L.T("Hız", "Speed");
        _apply.Text = L.T("Uygula", "Apply");
        _off.Text = L.T("Işıkları kapat", "Lights off");

        // Dark look for the plain controls
        foreach (var c in new Control[] { _effect, _speed })
        {
            c.BackColor = Field;
            c.ForeColor = Text1;
        }
        foreach (var b in new[] { _apply, _off })
        {
            b.BackColor = Field;
            b.ForeColor = Text1;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        }
        _apply.BackColor = Color.FromArgb(40, 120, 70);
        _color.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
        _brightness.BackColor = Bg;
        _status.ForeColor = Text2;

        var grid = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 14),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));

        void Row(Control label, Control field)
        {
            int r = grid.RowCount++;
            label.Margin = new Padding(0, 8, 14, 8);
            field.Margin = new Padding(0, 6, 0, 6);
            field.Dock = DockStyle.Fill;
            grid.Controls.Add(label, 0, r);
            grid.Controls.Add(field, 1, r);
        }

        Control Plain(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

        var brightnessRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        brightnessRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brightnessRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _brightness.Dock = DockStyle.Fill;
        brightnessRow.Controls.Add(_brightness, 0, 0);
        brightnessRow.Controls.Add(_brightnessValue, 1, 0);

        Row(Plain(L.T("Efekt", "Effect")), _effect);
        Row(_colorLabel, _color);
        Row(Plain(L.T("Parlaklık", "Brightness")), brightnessRow);
        Row(_speedLabel, _speed);

        var saveRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        saveRow.Controls.Add(_save);
        grid.Controls.Add(saveRow, 0, grid.RowCount++);
        grid.SetColumnSpan(saveRow, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_off);
        grid.Controls.Add(buttons, 0, grid.RowCount++);
        grid.SetColumnSpan(buttons, 2);

        _status.Text = L.T("Siyah renk = rastgele renkler. Armoury Crate açıksa önce kapat.",
                           "Black = random colors. If Armoury Crate is running, close it first.");
        grid.Controls.Add(_status, 0, grid.RowCount++);
        grid.SetColumnSpan(_status, 2);

        Controls.Add(grid);
        AcceptButton = _apply;

        _effect.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _brightness.ValueChanged += (_, _) => _brightnessValue.Text = L.Pct(_brightness.Value);
        _color.Click += (_, _) => PickColor();
        _apply.Click += async (_, _) => await ApplyAsync(null);
        _off.Click += async (_, _) => await ApplyAsync(0);

        _brightnessValue.Text = L.Pct(_brightness.Value);
        ShowColor();
        UpdateEnabled();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); }   // dark title bar (Windows 10 1809+ / 11)
        catch { }
    }

    private AuraEffect SelectedEffect => Effects[Math.Max(0, _effect.SelectedIndex)];

    private void UpdateEnabled()
    {
        _color.Enabled = LightingSettings.UsesColor(SelectedEffect);
        _colorLabel.Enabled = _color.Enabled;
        _speed.Enabled = LightingSettings.UsesSpeed(SelectedEffect);
        _speedLabel.Enabled = _speed.Enabled;
        ShowColor();
    }

    private void ShowColor()
    {
        _color.BackColor = _color.Enabled ? _picked : Field;
        _color.ForeColor = _picked.GetBrightness() > 0.55f ? Color.Black : Color.White;
        _color.Text = _color.Enabled ? $"#{_picked.R:X2}{_picked.G:X2}{_picked.B:X2}" : "—";
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _picked, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _picked = dlg.Color;
        ShowColor();
    }

    private LightingSettings Current() => new(SelectedEffect, new Rgb(_picked.R, _picked.G, _picked.B),
        _brightness.Value, (AuraSpeed)Math.Max(0, _speed.SelectedIndex), _save.Checked);

    private void SetBusy(bool busy)
    {
        _apply.Enabled = !busy;
        _off.Enabled = !busy;
    }

    /// brightnessOverride: "Lights off" sends 0 without touching the remembered brightness.
    private async Task ApplyAsync(int? brightnessOverride)
    {
        var chosen = Current();
        AsusLighting.Save(chosen);   // remember the window's choices for next time
        var send = brightnessOverride is int b ? chosen with { Brightness = b } : chosen;

        SetBusy(true);
        _status.ForeColor = Text2;
        _status.Text = L.T("Gönderiliyor…", "Sending…");
        LightingResult result;
        try { result = await Task.Run(() => AsusLighting.Apply(send)); }
        catch (Exception ex)
        {
            Log.W("[Azoth RGB] " + ex);
            result = new LightingResult(false, ex.Message);
        }
        if (IsDisposed) return;

        SetBusy(false);
        _status.ForeColor = result.Ok ? Good : Warn;
        _status.Text = result.Message;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
