using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Aurelia
{
    sealed class TopBar : WidgetHost
    {
        readonly MainForm form;
        readonly Controller ctl;
        public readonly PowerToggle Power;
        readonly Button_ prevPreset, presetBtn, nextPreset, saveBtn, analyzerBtn, outputBtn, gearBtn;
        Bitmap logo;

        public TopBar(MainForm f, Controller c)
        {
            form = f; ctl = c;
            BackColor = Theme.Bar;
            Power = Add(new PowerToggle { On = ctl.Power, Tip = "Power: process audio (off = clean bypass)" });
            Power.Toggled = on => form.SetPower(on);
            prevPreset = Add(new Button_ { Icon = Glyph.Left, Flat = true, Tip = "Previous preset" });
            prevPreset.Click = () => StepPreset(-1);
            presetBtn = Add(new Button_ { DropArrow = true, Tip = "Presets" });
            presetBtn.Click = () => form.ShowPresetMenu(this, new Point(presetBtn.Bounds.X, presetBtn.Bounds.Bottom + 2));
            nextPreset = Add(new Button_ { Icon = Glyph.Right, Flat = true, Tip = "Next preset" });
            nextPreset.Click = () => StepPreset(1);
            saveBtn = Add(new Button_ { Text = "Save", Tip = "Save preset" });
            saveBtn.Click = () => form.SavePreset(false);
            analyzerBtn = Add(new Button_ { DropArrow = true, Flat = true, Tip = "Spectrum analyzer" });
            analyzerBtn.Click = () => form.ShowAnalyzerMenu(this, new Point(analyzerBtn.Bounds.X, analyzerBtn.Bounds.Bottom + 2));
            outputBtn = Add(new Button_ { Icon = Glyph.Speaker, DropArrow = true, Tip = "Output device (what you listen on)" });
            outputBtn.Click = () => form.ShowOutputMenu(this, new Point(outputBtn.Bounds.X, outputBtn.Bounds.Bottom + 2));
            gearBtn = Add(new Button_ { Icon = Glyph.Gear, Flat = true, Tip = "Settings" });
            gearBtn.Click = () => form.ShowSettingsMenu(this, new Point(gearBtn.Bounds.Right - Theme.S(260), gearBtn.Bounds.Bottom + 2));
            logo = AppIcon.Render(Theme.S(28), true);
            Height = Theme.S(50);
            RefreshInfo();
        }

        void StepPreset(int d)
        {
            var names = new List<string>(ctl.Cfg.Presets.Keys);
            if (names.Count == 0) return;
            int i = names.FindIndex(n => string.Equals(n, ctl.Cfg.PresetName, StringComparison.OrdinalIgnoreCase));
            i = i < 0 ? (d > 0 ? 0 : names.Count - 1) : (i + d + names.Count) % names.Count;
            ctl.LoadPreset(names[i]);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (gearBtn == null) return;
            int h = Height, bh = Theme.S(30), y = (h - bh) / 2;
            Power.Bounds = new Rectangle(Theme.S(150), (h - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
            int x = Theme.S(204);
            prevPreset.Bounds = new Rectangle(x, y, Theme.S(28), bh); x += Theme.S(30);
            presetBtn.Bounds = new Rectangle(x, y, Theme.S(250), bh); x += Theme.S(252);
            nextPreset.Bounds = new Rectangle(x, y, Theme.S(28), bh); x += Theme.S(34);
            saveBtn.Bounds = new Rectangle(x, y, Theme.S(62), bh); x += Theme.S(62);
            gearBtn.Bounds = new Rectangle(Width - Theme.S(44), y, Theme.S(34), bh);
            int aw = Theme.S(150);
            int ow = Math.Min(Theme.S(320), Math.Max(Theme.S(120), gearBtn.Bounds.X - Theme.S(16) - aw - (x + Theme.S(20))));
            outputBtn.Bounds = new Rectangle(gearBtn.Bounds.X - Theme.S(8) - ow, y, ow, bh);
            analyzerBtn.Bounds = new Rectangle(outputBtn.Bounds.X - Theme.S(8) - aw, y, aw, bh);
            Invalidate();
        }

        public void RefreshInfo()
        {
            Power.On = ctl.Power;
            string name = ctl.Cfg.PresetName;
            if (string.IsNullOrEmpty(name)) presetBtn.Text = ctl.State.Bands.Count == 0 ? "Flat" : "Unsaved";
            else presetBtn.Text = name + (ctl.PresetModified ? "  •" : "");
            outputBtn.Text = string.IsNullOrEmpty(ctl.OutputName) ? "No output" : ctl.OutputName;
            Invalidate();
        }

        public void SyncAnalyzer()
        {
            string[] modes = { "Off", "Pre", "Post", "Pre + Post" };
            analyzerBtn.Text = "Analyzer: " + modes[ctl.Cfg.AnalyzerMode];
            Invalidate();
        }

        /// Routing details (capture → output, rate, latency) live in the output button's tooltip.
        public void SetOutputTip(string tip) { outputBtn.Tip = tip; }

        protected override void PaintBackground(Graphics g)
        {
            g.Clear(Theme.Bar);
            using (var pen = new Pen(Theme.BarLine)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            int ly = (Height - logo.Height) / 2;
            g.DrawImageUnscaled(logo, Theme.S(14), ly);
            var tr = new Rectangle(Theme.S(14) + logo.Width + Theme.S(8), 0, Theme.S(110), Height);
            Theme.Text_(g, "Aurelia", Theme.Title, AppIcon.Tentacle, tr, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing) { if (disposing && logo != null) logo.Dispose(); base.Dispose(disposing); }
    }

    /// One line of controls for the selected band (left) and the output gain (right).
    sealed class BottomBar : WidgetHost
    {
        readonly Controller ctl;
        readonly EqView eq;
        readonly PowerToggle bandPower;
        readonly Button_ bandBtn, shapeBtn, delBtn;
        readonly Knob kFreq, kGain, kQ, kMS;
        readonly Segmented placement;
        readonly Widget[] bandWidgets;
        public readonly Knob Output;
        int bandEnd;

        public BottomBar(MainForm f, Controller c, EqView e)
        {
            ctl = c; eq = e;
            BackColor = Theme.Bar;
            SetStyle(ControlStyles.Selectable, false); // keep keyboard focus on the EQ display (arrows, Del)

            bandPower = Add(new PowerToggle { Tip = "Enable / bypass band (double-click node)" });
            bandPower.Toggled = on => EditBand(b => b.Enabled = on);
            bandBtn = Add(new Button_ { DropArrow = true, Flat = true, Tip = "Pick a band" });
            bandBtn.Click = ShowBandList;
            shapeBtn = Add(new Button_ { DropArrow = true, Tip = "Filter shape (and slope for cuts)" });
            shapeBtn.Click = ShowShapeMenu;
            kFreq = Add(new Knob { Label = "FREQ", Min = EqView.FMin, Max = EqView.FMax, Log = true, Default = 1000, Format = Num.FormatFreq, Tip = "Frequency (10 Hz - 24 kHz)" });
            kFreq.Changed = v => EditBand(b => b.Freq = v);
            kGain = Add(new Knob { Label = "GAIN", Min = -Band.MaxGain, Max = Band.MaxGain, Bipolar = true, Default = 0, Format = Num.FormatDb, Tip = "Gain (±30 dB)" });
            kGain.Changed = v => EditBand(b => b.Gain = Math.Round(v, 2));
            kQ = Add(new Knob { Label = "Q", Min = Band.MinQ, Max = Band.MaxQ, Log = true, Default = 1, Format = Num.FormatQ, Tip = "Q / bandwidth (0.025 - 40); for cuts: resonance" });
            kQ.Changed = v => EditBand(b => b.Q = v);
            placement = Add(new Segmented { Options = new[] { "Stereo", "M/S" }, Tip = "Process this band on L/R together, or on Mid/Side" });
            placement.SelectedChanged = i => EditBand(b => b.MidSide = i == 1);
            kMS = Add(new Knob { Label = "MID · SIDE", Min = -1, Max = 1, Bipolar = true, Default = 0, Format = Num.FormatMS, Accent = Theme.Side,
                                 Tip = "Mid ← → Side. Centre = both. Double-click = centre, right-click = type (mid / side / -0.5 ...)" });
            kMS.Changed = v => EditBand(b => b.MS = Math.Round(v, 3));
            delBtn = Add(new Button_ { Icon = Glyph.Close, Flat = true, Tip = "Delete band (Del / Alt+click node)" });
            delBtn.Click = () => { if (eq.Selected >= 0) eq.DeleteBand(eq.Selected); };
            bandWidgets = new Widget[] { bandPower, bandBtn, shapeBtn, kFreq, kGain, kQ, placement, kMS, delBtn };
            foreach (var k in new[] { kFreq, kGain, kQ, kMS }) { k.Compact = true; k.KnobSize = Theme.S(30); }

            Output = Add(new Knob { Label = "OUTPUT", Min = -24, Max = 24, Default = 0, Bipolar = true, Compact = true, Format = Num.FormatDb, Tip = "Output gain (a limiter prevents clipping)" });
            Output.Changed = v => { ctl.State.OutputGain = Math.Round(v, 1); f.EffectsEdited(); };

            Height = Theme.S(58);
            eq.BandUiChanged = SyncBand;
            SyncBand();
            SyncOutput();
        }

        void EditBand(Action<Band> change)
        {
            var b = eq.SelectedBand;
            if (b == null) return;
            change(b);
            eq.BandsChanged();
        }

        void SyncBand()
        {
            var b = eq.SelectedBand;
            bool vis = b != null;
            foreach (var w in bandWidgets) w.Visible = vis;
            if (vis)
            {
                int n = eq.Selected + 1;
                bandPower.On = b.Enabled;
                bandBtn.Text = "Band " + n;
                bandBtn.TextColor = eq.BandColor(b);
                bandBtn.Tip = "Band " + n + " of " + ctl.State.Bands.Count + " (max " + EqState.MaxBands + "). Click to pick another.";
                shapeBtn.Text = Band.TypeNames[(int)b.Type] + (b.HasSlope ? "  " + b.Slope + " dB/oct" : "");
                kFreq.Value = Math.Max(EqView.FMin, Math.Min(EqView.FMax, b.Freq));
                kGain.Value = b.Gain;
                kGain.Enabled = b.HasGain;
                kQ.Value = b.Q;
                kQ.Default = b.HasSlope || b.Type == FilterType.LowShelf || b.Type == FilterType.HighShelf ? 0.7071 : 1;
                kQ.Enabled = !(b.HasSlope && b.Slope == 6);
                placement.Selected = b.MidSide ? 1 : 0;
                kMS.Value = b.MS;
                kMS.Enabled = b.MidSide;
            }
            Invalidate();
        }

        public void SyncOutput()
        {
            Output.Value = ctl.State.OutputGain;
            Invalidate();
        }

        void ShowBandList()
        {
            var m = Theme.Menu();
            var bands = ctl.State.Bands;
            for (int i = 0; i < bands.Count; i++)
            {
                int idx = i;
                var b = bands[i];
                var it = Theme.Item((i + 1) + "   " + Band.TypeNames[(int)b.Type] + "   " + b.Describe() + (b.Enabled ? "" : "   (off)"), delegate { eq.SelectBand(idx); });
                it.Checked = i == eq.Selected;
                m.Items.Add(it);
            }
            PopupAbove(m, bandBtn);
        }

        void ShowShapeMenu()
        {
            var b = eq.SelectedBand;
            if (b == null) return;
            var m = Theme.Menu();
            eq.AddShapeItems(m.Items);
            if (b.HasSlope) { m.Items.Add(new ToolStripSeparator()); eq.AddSlopeItems(m.Items); }
            PopupAbove(m, shapeBtn);
        }

        void PopupAbove(ContextMenuStrip m, Widget anchor)
        {
            m.Closed += delegate { BeginInvoke((MethodInvoker)delegate { m.Dispose(); }); };
            m.Show(this, new Point(anchor.Bounds.X, anchor.Bounds.Y - Theme.S(2)), ToolStripDropDownDirection.AboveRight);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Output == null) return;
            int mid = Height / 2, x = Theme.S(14);
            bandPower.Bounds = new Rectangle(x, mid - Theme.S(13), Theme.S(26), Theme.S(26)); x += Theme.S(32);
            bandBtn.Bounds = new Rectangle(x, mid - Theme.S(14), Theme.S(84), Theme.S(28)); x += Theme.S(90);
            shapeBtn.Bounds = new Rectangle(x, mid - Theme.S(14), Theme.S(150), Theme.S(28)); x += Theme.S(164);
            kFreq.Bounds = new Rectangle(x, mid - Theme.S(20), Theme.S(96), Theme.S(40)); x += Theme.S(100);
            kGain.Bounds = new Rectangle(x, mid - Theme.S(20), Theme.S(94), Theme.S(40)); x += Theme.S(98);
            kQ.Bounds = new Rectangle(x, mid - Theme.S(20), Theme.S(78), Theme.S(40)); x += Theme.S(90);
            placement.Bounds = new Rectangle(x, mid - Theme.S(13), Theme.S(96), Theme.S(26)); x += Theme.S(104);
            kMS.Bounds = new Rectangle(x, mid - Theme.S(20), Theme.S(92), Theme.S(40)); x += Theme.S(96);
            delBtn.Bounds = new Rectangle(x, mid - Theme.S(13), Theme.S(26), Theme.S(26)); x += Theme.S(26);
            bandEnd = x;
            Output.Bounds = new Rectangle(Width - Theme.S(124), mid - Theme.S(20), Theme.S(110), Theme.S(40));
            Invalidate();
        }

        protected override void PaintBackground(Graphics g)
        {
            g.Clear(Theme.Bar);
            using (var pen = new Pen(Theme.BarLine)) g.DrawLine(pen, 0, 0, Width, 0);
            var b = eq.SelectedBand;
            if (b != null)
            {
                // colour strip identifies the band being edited
                using (var br = new SolidBrush(eq.BandColor(b))) g.FillRectangle(br, Theme.S(14), 0, bandEnd - Theme.S(14), Theme.S(2));
            }
            else
            {
                Theme.Text_(g, "Click a band node to edit it  ·  double-click the graph to add one", Theme.UI, Theme.TextFaint,
                            new Rectangle(Theme.S(16), 0, Math.Max(0, Output.Bounds.X - Theme.S(32)), Height),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    sealed class Banner : WidgetHost
    {
        readonly Label_ text;
        readonly Button_ action;
        public Action ActionClick;

        public Banner()
        {
            BackColor = Color.FromArgb(18, 36, 62);
            text = Add(new Label_ { Color = Theme.AccentHi });
            action = Add(new Button_ { Tip = "" });
            action.Click = () => { if (ActionClick != null) ActionClick(); };
            Height = Theme.S(36);
        }

        public void Set(string message, string actionText)
        {
            text.Text = message ?? "";
            action.Text = actionText ?? "";
            action.Visible = !string.IsNullOrEmpty(actionText);
            OnResize(EventArgs.Empty);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (action == null) return;
            int aw = action.Visible ? Theme.S(130) : 0;
            action.Bounds = new Rectangle(Width - aw - Theme.S(12), (Height - Theme.S(26)) / 2, aw, Theme.S(26));
            text.Bounds = new Rectangle(Theme.S(16), 0, Width - aw - Theme.S(40), Height);
        }
    }

    sealed class MainForm : Form
    {
        readonly Controller ctl;
        readonly TopBar top;
        readonly Banner banner;
        readonly EqView eq;
        readonly BottomBar bottom;
        readonly NotifyIcon tray;
        readonly Icon icoOn, icoOff, appIcon;
        readonly Timer statusTimer = new Timer();
        bool exiting, everShown;

        public MainForm(Controller c)
        {
            ctl = c;
            Text = "Aurelia";
            Font = Theme.UI;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            MinimumSize = new Size(Theme.S(1000), Theme.S(640));
            StartPosition = FormStartPosition.Manual;
            var cfg = ctl.Cfg;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size sz = new Size(Math.Min(wa.Width - 40, Theme.S(1180)), Math.Min(wa.Height - 40, Theme.S(800)));
            if (cfg.WinW > 200 && cfg.WinH > 200) sz = new Size(cfg.WinW, cfg.WinH);
            var loc = new Point(wa.X + (wa.Width - sz.Width) / 2, wa.Y + (wa.Height - sz.Height) / 2);
            if (cfg.WinX != int.MinValue)
            {
                var r = new Rectangle(cfg.WinX, cfg.WinY, sz.Width, sz.Height);
                foreach (var s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) { loc = r.Location; break; }
            }
            Bounds = new Rectangle(loc, sz);

            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            icoOn = AppIcon.MakeIcon(SystemInformation.SmallIconSize.Width, true);
            icoOff = AppIcon.MakeIcon(SystemInformation.SmallIconSize.Width, false);

            eq = new EqView(ctl) { Dock = DockStyle.Fill };
            eq.Edited = () => top.RefreshInfo();
            bottom = new BottomBar(this, ctl, eq) { Dock = DockStyle.Bottom };
            banner = new Banner { Dock = DockStyle.Top, Visible = false };
            top = new TopBar(this, ctl) { Dock = DockStyle.Top };
            Controls.Add(eq);
            Controls.Add(bottom);
            Controls.Add(banner);
            Controls.Add(top);

            tray = new NotifyIcon();
            tray.Text = "Aurelia";
            tray.Icon = ctl.Power ? icoOn : icoOff;
            tray.ContextMenuStrip = Theme.Menu();
            tray.ContextMenuStrip.Opening += (s, e) => BuildTrayMenu(tray.ContextMenuStrip);
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
            tray.Visible = true;

            ctl.Changed += RefreshStatus;
            ctl.PresetsChanged += () => { top.RefreshInfo(); };
            ctl.StateReplaced += () => { eq.StateReplaced(); bottom.SyncOutput(); top.RefreshInfo(); };

            statusTimer.Interval = 1000;
            statusTimer.Tick += delegate { RefreshStatus(); };
            statusTimer.Start();
        }

        public void HideTrayIcon() { tray.Visible = false; }
        public EqView Eq { get { return eq; } }

        /// Used by --screenshot: a representative editing session.
        public void Demo()
        {
            var bands = ctl.State.Bands;
            bands.Add(new Band { Type = FilterType.LowCut, Freq = 28, Slope = 24, Q = 0.7071 });
            bands.Add(new Band { Type = FilterType.HighShelf, Freq = 9000, Gain = 4.5, Q = 0.7071, MidSide = true, MS = 1 });
            bands.Add(new Band { Type = FilterType.Notch, Freq = 3150, Q = 6 });
            bands.Add(new Band { Type = FilterType.Bell, Freq = 140, Gain = 5, Q = 1.4, MidSide = true, MS = -1 });
            ctl.StateEdited();
            eq.StateReplaced();
            eq.SelectBand(bands.Count - 3);
            top.RefreshInfo();
        }

        public void Start(bool hidden)
        {
            var h = Handle; // window exists for BeginInvoke even while hidden in the tray
            ctl.Initialize(this);
            eq.StateReplaced();
            bottom.SyncOutput();
            top.SyncAnalyzer();
            RefreshStatus();
            if (!hidden) ShowFromTray();
            else eq.SetActive(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        // ---------------------------------------------------------------- window / tray

        public bool NoActivate;
        protected override bool ShowWithoutActivation { get { return NoActivate; } }

        public void ShowFromTray()
        {
            if (exiting) return;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            everShown = true;
            eq.SetActive(true);
            if (NoActivate) return;
            Activate();
            Win32.SetForegroundWindow(Handle);
        }

        void HideToTray()
        {
            eq.SetActive(false);
            Hide();
            if (!ctl.Cfg.TrayHintShown)
            {
                ctl.Cfg.TrayHintShown = true;
                ctl.MarkDirty();
                tray.ShowBalloonTip(3000, "Aurelia keeps running", "Audio processing continues in the background. Click the tray icon to open, right-click to exit.", ToolTipIcon.Info);
            }
        }

        void ToggleWindow()
        {
            if (Visible && WindowState != FormWindowState.Minimized) HideToTray();
            else ShowFromTray();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && everShown && Visible) HideToTray();
            RememberBounds();
        }

        protected override void OnMove(EventArgs e) { base.OnMove(e); RememberBounds(); }

        void RememberBounds()
        {
            if (!everShown || WindowState != FormWindowState.Normal || ctl == null || ctl.Cfg == null) return;
            ctl.Cfg.WinX = Left; ctl.Cfg.WinY = Top; ctl.Cfg.WinW = Width; ctl.Cfg.WinH = Height;
            ctl.MarkDirty();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && ctl.Cfg.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
            if (!e.Cancel) ExitApp();
        }

        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            statusTimer.Stop();
            eq.SetActive(false);
            ctl.Shutdown();
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16;
            if (m.Msg == WM_QUERYENDSESSION || (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero))
            {
                try { ctl.Shutdown(); } catch { }
            }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- state

        public void SetPower(bool on)
        {
            ctl.SetPower(on);
            top.Power.On = on;
            top.Invalidate();
            tray.Icon = on ? icoOn : icoOff;
        }

        public void EffectsEdited()
        {
            ctl.StateEdited();
            top.RefreshInfo();
        }

        void RefreshStatus()
        {
            if (exiting) return;
            top.RefreshInfo();
            var e = ctl.Engine;
            string route;
            if (!string.IsNullOrEmpty(ctl.Problem)) route = "Not routing";
            else if (e.Connected)
                route = ctl.SourceName + "  →  " + ctl.OutputName + "\n" +
                        (e.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz  ·  ~" + e.LatencyMs + " ms  ·  " +
                        (e.Idle ? "Idle (silence)" : "Playing") + (ctl.Power ? "" : "  ·  Bypassed");
            else route = ctl.SourceName + "  →  " + ctl.OutputName + "\n" + e.Status;
            top.SetOutputTip("Output device (what you listen on)\n" + route);
            tray.Text = Trunc("Aurelia - " + (ctl.Power ? "On" : "Bypassed") + (string.IsNullOrEmpty(ctl.Cfg.PresetName) ? "" : " - " + ctl.Cfg.PresetName), 63);

            if (!string.IsNullOrEmpty(ctl.Problem)) { banner.Set(ctl.Problem, null); if (!banner.Visible) banner.Visible = true; }
            else if (banner.Visible) banner.Visible = false;
        }

        static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        // ---------------------------------------------------------------- menus

        static void Popup(Control owner, ContextMenuStrip m, Point at)
        {
            m.Closed += delegate { owner.BeginInvoke((MethodInvoker)delegate { m.Dispose(); }); };
            m.Show(owner, at);
        }

        void AddPresetItems(ToolStripItemCollection items)
        {
            if (ctl.Cfg.Presets.Count == 0)
            {
                var none = new ToolStripMenuItem("No saved presets yet");
                none.Enabled = false;
                items.Add(none);
            }
            foreach (var name in ctl.Cfg.Presets.Keys)
            {
                string n = name;
                var it = Theme.Item(n, delegate { ctl.LoadPreset(n); });
                it.Checked = string.Equals(n, ctl.Cfg.PresetName, StringComparison.OrdinalIgnoreCase);
                items.Add(it);
            }
        }

        public void ShowPresetMenu(Control owner, Point at)
        {
            var m = Theme.Menu();
            AddPresetItems(m.Items);
            m.Items.Add(new ToolStripSeparator());
            string cur = ctl.Cfg.PresetName;
            bool has = !string.IsNullOrEmpty(cur) && ctl.Cfg.Presets.ContainsKey(cur);
            m.Items.Add(Theme.Item("Save as new preset...", delegate { SavePreset(true); }));
            if (has)
            {
                var ow = Theme.Item("Save changes to \"" + cur + "\"", delegate { ctl.SavePreset(cur); top.RefreshInfo(); });
                ow.Enabled = ctl.PresetModified;
                m.Items.Add(ow);
                m.Items.Add(Theme.Item("Rename \"" + cur + "\"...", delegate
                {
                    string n = InputBox.Ask(this, "Rename preset", "New name:", cur);
                    if (!string.IsNullOrEmpty(n)) ctl.RenamePreset(cur, n);
                }));
                m.Items.Add(Theme.Item("Delete \"" + cur + "\"", delegate
                {
                    if (MessageBox.Show(this, "Delete preset \"" + cur + "\"?", "Aurelia", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        ctl.DeletePreset(cur);
                }));
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Reset to flat", delegate { ctl.ResetEq(); }));
            Popup(owner, m, at);
        }

        public void SavePreset(bool asNew)
        {
            string cur = ctl.Cfg.PresetName;
            if (!asNew && !string.IsNullOrEmpty(cur) && ctl.Cfg.Presets.ContainsKey(cur))
            {
                ctl.SavePreset(cur);
                top.RefreshInfo();
                return;
            }
            string n = InputBox.Ask(this, "Save preset", "Preset name:", string.IsNullOrEmpty(cur) ? "My preset" : cur + " 2");
            if (string.IsNullOrEmpty(n)) return;
            n = Settings.CleanName(n);
            if (n.Length == 0) return;
            if (ctl.Cfg.Presets.ContainsKey(n) &&
                MessageBox.Show(this, "Replace the existing preset \"" + n + "\"?", "Aurelia", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ctl.SavePreset(n);
            top.RefreshInfo();
        }

        void AddOutputItems(ToolStripItemCollection items)
        {
            int count = 0;
            foreach (var d in ctl.Devices_)
            {
                if (string.Equals(d.Id, ctl.SourceId, StringComparison.OrdinalIgnoreCase)) continue;
                string id = d.Id;
                var it = Theme.Item(d.Name, delegate { ctl.SelectOutput(id); });
                it.Checked = string.Equals(d.Id, ctl.OutputId, StringComparison.OrdinalIgnoreCase);
                items.Add(it);
                count++;
            }
            if (count == 0) { var none = new ToolStripMenuItem("No output devices"); none.Enabled = false; items.Add(none); }
        }

        public void ShowOutputMenu(Control owner, Point at)
        {
            ctl.ApplyRouting();
            var m = Theme.Menu();
            AddOutputItems(m.Items);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Windows sound settings...", delegate { try { Process.Start("ms-settings:sound"); } catch { } }));
            Popup(owner, m, at);
        }

        public void ShowAnalyzerMenu(Control owner, Point at)
        {
            var m = Theme.Menu();
            AddAnalyzerItems(m.Items);
            Popup(owner, m, at);
        }

        void AddAnalyzerItems(ToolStripItemCollection items)
        {
            string[] modes = { "Off", "Pre (input)", "Post (output)", "Pre + Post" };
            for (int i = 0; i < 4; i++)
            {
                int mode = i;
                var it = Theme.Item(modes[i], delegate { ctl.Cfg.AnalyzerMode = mode; ctl.MarkDirty(); top.SyncAnalyzer(); eq.Invalidate(); });
                it.Checked = ctl.Cfg.AnalyzerMode == i;
                items.Add(it);
            }
        }

        static ToolStripMenuItem Sub(string text)
        {
            var it = new ToolStripMenuItem(text);
            it.DropDown.Renderer = new DarkRenderer();
            ((ToolStripDropDownMenu)it.DropDown).ShowImageMargin = false;
            ((ToolStripDropDownMenu)it.DropDown).ShowCheckMargin = true;
            return it;
        }

        public void ShowSettingsMenu(Control owner, Point at)
        {
            var m = Theme.Menu();
            var auto = Theme.Item("Start with Windows", delegate { ctl.SetAutoStart(!ctl.AutoStartEnabled); });
            auto.Checked = ctl.AutoStartEnabled;
            var tr = Theme.Item("Close button minimizes to tray", delegate { ctl.Cfg.CloseToTray = !ctl.Cfg.CloseToTray; ctl.MarkDirty(); });
            tr.Checked = ctl.Cfg.CloseToTray;
            m.Items.Add(auto);
            m.Items.Add(tr);
            m.Items.Add(new ToolStripSeparator());

            var src = Sub("Capture from (device apps play to)");
            foreach (var d in ctl.Devices_)
            {
                string id = d.Id;
                var it = Theme.Item(d.Name + (d.LooksVirtual ? "   [virtual]" : ""), delegate { ctl.SelectSource(id); });
                it.Checked = string.Equals(d.Id, ctl.SourceId, StringComparison.OrdinalIgnoreCase);
                src.DropDownItems.Add(it);
            }
            if (src.DropDownItems.Count == 0) { var none = new ToolStripMenuItem("No devices"); none.Enabled = false; src.DropDownItems.Add(none); }
            m.Items.Add(src);
            var an = Sub("Analyzer");
            AddAnalyzerItems(an.DropDownItems);
            m.Items.Add(an);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Open settings folder", delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + ctl.Cfg.FilePath + "\""); } catch { }
            }));
            m.Items.Add(Theme.Item("Help / shortcuts", delegate { ShowHelp(); }));
            m.Items.Add(Theme.Item("Exit Aurelia", delegate { ExitApp(); }));
            Popup(owner, m, at);
        }

        void ShowHelp()
        {
            MessageBox.Show(this,
                "EQ display\n" +
                "  Double-click empty area: add band (up to 20)\n" +
                "  Drag node: frequency + gain (cut/notch/band-pass: Q)\n" +
                "  Shift+drag: fine   ·   Ctrl+drag: frequency only\n" +
                "  Mouse wheel on node: Q (bandwidth)\n" +
                "  Double-click node / middle-click: bypass band\n" +
                "  Alt+click node or Del: delete band\n" +
                "  Right-click node: shape, slope, Stereo / Mid / Side\n" +
                "  Arrow keys: nudge selected band (Shift = fine)\n\n" +
                "Bottom bar\n" +
                "  The selected band: on/off, band picker, shape (+ slope for cuts), frequency, gain, Q,\n" +
                "  Stereo or Mid/Side, delete. Output gain on the right.\n\n" +
                "Knobs\n" +
                "  Drag up/down (Shift = fine), wheel, double-click = default, right-click = type a value\n\n" +
                "Routing\n" +
                "  Apps play to the capture device (made the Windows default while Aurelia runs);\n" +
                "  Aurelia processes it and plays it on your output device. On exit the default is restored.",
                "Aurelia - help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void BuildTrayMenu(ContextMenuStrip m)
        {
            m.Items.Clear();
            var open = Theme.Item("Open Aurelia", delegate { ShowFromTray(); });
            open.Font = Theme.UIBold;
            m.Items.Add(open);
            var pw = Theme.Item("Power", delegate { SetPower(!ctl.Power); });
            pw.Checked = ctl.Power;
            m.Items.Add(pw);
            var pr = Sub("Presets");
            AddPresetItems(pr.DropDownItems);
            m.Items.Add(pr);
            var outp = Sub("Output");
            AddOutputItems(outp.DropDownItems);
            m.Items.Add(outp);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Exit", delegate { ExitApp(); }));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                statusTimer.Dispose();
                if (icoOn != null) icoOn.Dispose();
                if (icoOff != null) icoOff.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
