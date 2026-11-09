using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Aurelia
{
    sealed class TopBar : WidgetHost
    {
        readonly MainForm form;
        readonly Controller ctl;
        public readonly PowerToggle Power;
        readonly Button_ prevPreset, presetBtn, nextPreset, saveBtn, outputBtn, gearBtn;
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
            int ow = Math.Min(Theme.S(320), Math.Max(Theme.S(120), gearBtn.Bounds.X - Theme.S(10) - (x + Theme.S(20))));
            outputBtn.Bounds = new Rectangle(gearBtn.Bounds.X - Theme.S(8) - ow, y, ow, bh);
            Invalidate();
        }

        public void RefreshInfo()
        {
            Power.On = ctl.Power;
            string name = ctl.Cfg.PresetName;
            if (string.IsNullOrEmpty(name)) presetBtn.Text = ctl.State.Bands.Count == 0 && ctl.State.Clarity == 0 && ctl.State.Loudness == 0 ? "Flat" : "Unsaved";
            else presetBtn.Text = name + (ctl.PresetModified ? "  •" : "");
            outputBtn.Text = string.IsNullOrEmpty(ctl.OutputName) ? "No output" : ctl.OutputName;
            Invalidate();
        }

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

    sealed class BottomBar : WidgetHost
    {
        readonly MainForm form;
        readonly Controller ctl;
        public readonly Knob Clarity, Loudness, Output;
        readonly Button_ analyzerBtn;
        string line1 = "", line2 = "";
        Color dot = Theme.TextFaint;

        public BottomBar(MainForm f, Controller c)
        {
            form = f; ctl = c;
            BackColor = Theme.Bar;
            Clarity = Add(new Knob { Label = "CLARITY", Min = 0, Max = 10, Default = 0, Compact = true, Tip = "Harmonic exciter + air: crisper highs and detail" });
            Clarity.Changed = v => { ctl.State.Clarity = Math.Round(v, 1); form.EffectsEdited(); };
            Loudness = Add(new Knob { Label = "LOUDNESS", Min = 0, Max = 10, Default = 0, Compact = true, Tip = "Dynamic boost: lifts quiet passages, tames peaks" });
            Loudness.Changed = v => { ctl.State.Loudness = Math.Round(v, 1); form.EffectsEdited(); };
            Output = Add(new Knob { Label = "OUTPUT", Min = -24, Max = 24, Default = 0, Bipolar = true, Compact = true, Format = Num.FormatDb, Tip = "Output gain (a limiter prevents clipping)" });
            Output.Changed = v => { ctl.State.OutputGain = Math.Round(v, 1); form.EffectsEdited(); };
            foreach (var k in new[] { Clarity, Loudness })
                k.Format = v => v.ToString("0.0", CultureInfo.InvariantCulture);
            analyzerBtn = Add(new Button_ { DropArrow = true, Flat = true, Tip = "Spectrum analyzer" });
            analyzerBtn.Click = () => form.ShowAnalyzerMenu(this, new Point(analyzerBtn.Bounds.X, analyzerBtn.Bounds.Y - Theme.S(110)));
            Height = Theme.S(58);
            SyncKnobs();
        }

        public void SyncKnobs()
        {
            Clarity.Value = ctl.State.Clarity;
            Loudness.Value = ctl.State.Loudness;
            Output.Value = ctl.State.OutputGain;
            string[] modes = { "Off", "Pre", "Post", "Pre + Post" };
            analyzerBtn.Text = "Analyzer: " + modes[ctl.Cfg.AnalyzerMode];
            Invalidate();
        }

        public void SetStatus(string l1, string l2, Color d)
        {
            if (l1 == line1 && l2 == line2 && d == dot) return;
            line1 = l1; line2 = l2; dot = d;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (analyzerBtn == null) return;
            int y = (Height - Theme.S(40)) / 2;
            Clarity.Bounds = new Rectangle(Theme.S(16), y, Theme.S(120), Theme.S(40));
            Loudness.Bounds = new Rectangle(Theme.S(146), y, Theme.S(120), Theme.S(40));
            Output.Bounds = new Rectangle(Theme.S(276), y, Theme.S(124), Theme.S(40));
            analyzerBtn.Bounds = new Rectangle(Theme.S(416), (Height - Theme.S(28)) / 2, Theme.S(150), Theme.S(28));
            Invalidate();
        }

        protected override void PaintBackground(Graphics g)
        {
            g.Clear(Theme.Bar);
            using (var pen = new Pen(Theme.BarLine)) g.DrawLine(pen, 0, 0, Width, 0);
            int x0 = Theme.S(580), right = Width - Theme.S(14);
            if (right - x0 < Theme.S(60)) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int dy = Height / 2 - Theme.S(14);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, right - Theme.S(8), dy + Theme.S(4), Theme.S(8), Theme.S(8));
            Theme.Text_(g, line1, Theme.UI, Theme.TextDim, new Rectangle(x0, dy, right - x0 - Theme.S(14), Theme.S(16)),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Theme.Text_(g, line2, Theme.UISmall, Theme.TextFaint, new Rectangle(x0, dy + Theme.S(16), right - x0 - Theme.S(14), Theme.S(14)),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
            MinimumSize = new Size(Theme.S(960), Theme.S(640));
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
            bottom = new BottomBar(this, ctl) { Dock = DockStyle.Bottom };
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
            ctl.StateReplaced += () => { eq.StateReplaced(); bottom.SyncKnobs(); top.RefreshInfo(); };

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
            bottom.SyncKnobs();
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
            string l1, l2;
            Color dot;
            if (!string.IsNullOrEmpty(ctl.Problem))
            {
                l1 = "Not routing"; l2 = ""; dot = Theme.Warn;
            }
            else
            {
                l1 = Short(ctl.SourceName) + "  →  " + Short(ctl.OutputName);
                if (e.Connected)
                {
                    string state = e.Idle ? "Idle (silence)" : "Playing";
                    l2 = (e.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz  ·  ~" + e.LatencyMs + " ms  ·  " + state + (ctl.Power ? "" : "  ·  Bypassed");
                    dot = e.Idle ? Theme.AccentDim : Color.FromArgb(120, 220, 120);
                }
                else { l2 = e.Status; dot = Theme.Warn; }
            }
            bottom.SetStatus(l1, l2, dot);
            tray.Text = Trunc("Aurelia - " + (ctl.Power ? "On" : "Bypassed") + (string.IsNullOrEmpty(ctl.Cfg.PresetName) ? "" : " - " + ctl.Cfg.PresetName), 63);

            string msg = null, act = null;
            Action click = null;
            if (!string.IsNullOrEmpty(ctl.Problem)) msg = ctl.Problem;
            else if (ctl.FxSoundRunning)
            {
                msg = "FxSound is also running and processes the same audio, so you may hear it twice.";
                act = "Close FxSound";
                click = () => ctl.CloseFxSound();
            }
            banner.ActionClick = click;
            if (msg != null) { banner.Set(msg, act); if (!banner.Visible) banner.Visible = true; }
            else if (banner.Visible) banner.Visible = false;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            int p = s.IndexOf(" (");
            string name = p > 0 ? s.Substring(0, p) : s;
            string dev = p > 0 ? s.Substring(p + 2).TrimEnd(')') : "";
            return dev.Length > 0 ? name + " (" + Trunc(dev, 28) + ")" : name;
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
            m.Items.Add(Theme.Item("Import FxSound presets (.fac)...", delegate { ImportFac(); }));
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

        void ImportFac()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Import FxSound presets";
                d.Filter = "FxSound presets (*.fac)|*.fac|All files (*.*)|*.*";
                d.Multiselect = true;
                string dir = Path.Combine(FxSoundImport.FxSoundDataDir, "Presets");
                if (Directory.Exists(dir)) d.InitialDirectory = dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                int n = ctl.ImportFac(d.FileNames);
                MessageBox.Show(this, n == 0 ? "No presets could be read from the selected files." : "Imported " + n + " preset" + (n == 1 ? "" : "s") + ".",
                                "Aurelia", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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
                var it = Theme.Item(modes[i], delegate { ctl.Cfg.AnalyzerMode = mode; ctl.MarkDirty(); bottom.SyncKnobs(); eq.Invalidate(); });
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
