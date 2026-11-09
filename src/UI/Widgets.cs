using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Aurelia
{
    /// Lightweight owner-drawn widget. Hosted by WidgetHost which does hit testing and mouse capture.
    abstract class Widget
    {
        public Rectangle Bounds;
        public bool Visible = true, Enabled = true;
        public bool Hover, Pressed;
        public string Tip;
        public WidgetHost Host;

        public abstract void Paint(Graphics g);
        public virtual void OnDown(MouseEventArgs e) { }
        public virtual void OnDrag(MouseEventArgs e) { }
        public virtual void OnUp(MouseEventArgs e, bool inside) { }
        public virtual void OnWheel(int delta) { }
        public virtual void OnDoubleClick(MouseEventArgs e) { }
        public virtual Cursor Cursor { get { return Cursors.Hand; } }
        public void Invalidate() { if (Host != null) Host.Invalidate(Rectangle.Inflate(Bounds, 4, 4)); }
    }

    class WidgetHost : Control
    {
        public readonly List<Widget> Widgets = new List<Widget>();
        Widget hot, captured;
        readonly ToolTip tip = new ToolTip();
        string tipText;

        public WidgetHost()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            tip.InitialDelay = 500;
            tip.ReshowDelay = 100;
        }

        public T Add<T>(T w) where T : Widget { w.Host = this; Widgets.Add(w); return w; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PaintBackground(g);
            PaintWidgets(g, e.ClipRectangle);
            PaintOverlay(g);
        }

        protected void PaintWidgets(Graphics g, Rectangle clip)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var w in Widgets)
                if (w.Visible && w.Bounds.IntersectsWith(clip)) w.Paint(g);
        }

        protected virtual void PaintBackground(Graphics g) { g.Clear(BackColor); }
        protected virtual void PaintOverlay(Graphics g) { }

        protected Widget HitWidget(Point p)
        {
            for (int i = Widgets.Count - 1; i >= 0; i--)
            {
                var w = Widgets[i];
                if (w.Visible && w.Bounds.Contains(p)) return w;
            }
            return null;
        }

        protected virtual void HostMouseDown(MouseEventArgs e) { }
        protected virtual void HostMouseMove(MouseEventArgs e) { }
        protected virtual void HostMouseUp(MouseEventArgs e) { }
        protected virtual void HostWheel(MouseEventArgs e) { }
        protected virtual void HostDoubleClick(MouseEventArgs e) { }
        protected bool WidgetCaptured { get { return captured != null; } }
        /// Lets the host take a click even though a (background) widget is under the cursor.
        protected virtual bool PreferHost(Point p, Widget w) { return false; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (CanFocus) Focus();
            var w = HitWidget(e.Location);
            if (w != null && PreferHost(e.Location, w)) w = null;
            if (w != null)
            {
                if (!w.Enabled) return;
                if (e.Button != MouseButtons.Left) { w.OnDown(e); return; } // no capture for right/middle clicks
                captured = w; w.Pressed = true; Capture = true;
                w.OnDown(e);
                w.Invalidate();
                return;
            }
            HostMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (captured != null) { captured.OnDrag(e); return; }
            var w = HitWidget(e.Location);
            if (w != null && PreferHost(e.Location, w)) w = null;
            if (w != hot)
            {
                if (hot != null) { hot.Hover = false; hot.Invalidate(); }
                hot = w;
                if (hot != null) { hot.Hover = true; hot.Invalidate(); }
                string t = hot != null ? hot.Tip : null;
                if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
            }
            if (w != null) { Cursor = w.Enabled ? w.Cursor : Cursors.Default; return; }
            HostMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (captured != null)
            {
                var w = captured;
                captured = null;
                w.Pressed = false;
                Capture = false;
                w.OnUp(e, w.Bounds.Contains(e.Location));
                w.Invalidate();
                return;
            }
            HostMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var w = HitWidget(e.Location);
            if (w != null && PreferHost(e.Location, w)) w = null;
            if (w != null) { if (w.Enabled) w.OnWheel(e.Delta); return; }
            HostWheel(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var w = HitWidget(e.Location);
            if (w != null && PreferHost(e.Location, w)) w = null;
            if (w != null) { if (w.Enabled) w.OnDoubleClick(e); return; }
            HostDoubleClick(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != null && captured == null) { hot.Hover = false; hot.Invalidate(); hot = null; }
            HostMouseMove(new MouseEventArgs(MouseButtons.None, 0, -10000, -10000, 0));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }

    /// Rotary knob: vertical drag (Shift = fine), wheel, double-click = default, right-click = type a value.
    sealed class Knob : Widget
    {
        public string Label = "";
        public double Min = 0, Max = 1, Default = 0, Value;
        public bool Log, Bipolar;
        public string LeftHint, RightHint;
        public Func<double, string> Format;
        public Action<double> Changed;
        public Color Accent = Theme.Accent;
        public bool Compact;           // label/value to the right of the knob
        public int KnobSize;           // explicit diameter (0 = auto)

        double dragStartNorm;
        int dragStartY;

        public override Cursor Cursor { get { return Cursors.SizeNS; } }

        double Norm(double v)
        {
            if (Log) return Math.Log(Math.Max(v, Min) / Min) / Math.Log(Max / Min);
            return (v - Min) / (Max - Min);
        }

        double Denorm(double n)
        {
            n = Math.Max(0, Math.Min(1, n));
            if (Log) return Min * Math.Pow(Max / Min, n);
            return Min + n * (Max - Min);
        }

        public void Set(double v, bool notify)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (v == Value) return;
            Value = v;
            Invalidate();
            if (notify && Changed != null) Changed(v);
        }

        string Fmt(double v) { return Format != null ? Format(v) : v.ToString("0.0", CultureInfo.InvariantCulture); }

        public override void OnDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                string s = InputBox.Ask(Host.FindForm(), Label.Length > 0 ? Label : "Value", "Enter a value:", Fmt(Value));
                if (s != null)
                {
                    double v = ParseLoose(s);
                    if (!double.IsNaN(v)) Set(v, true);
                }
                return;
            }
            dragStartNorm = Norm(Value);
            dragStartY = e.Y;
        }

        /// Accepts "1.2k", "1,5", "-3 dB", "250hz", "mid", "side".
        double ParseLoose(string s)
        {
            s = s.Trim().ToLowerInvariant().Replace(',', '.');
            if (s.StartsWith("mid")) return -1;
            if (s.StartsWith("side")) return 1;
            if (s.StartsWith("st") || s == "m+s" || s == "m + s") return 0;
            double mult = 1;
            s = s.Replace("db", "").Replace("hz", "").Replace("%", "").Trim();
            if (s.EndsWith("k")) { mult = 1000; s = s.Substring(0, s.Length - 1); }
            double v;
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v * mult;
            return double.NaN;
        }

        public override void OnDrag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            double range = Theme.S(240) * ((Control.ModifierKeys & Keys.Shift) != 0 ? 6 : 1);
            Set(Denorm(dragStartNorm + (dragStartY - e.Y) / range), true);
        }

        public override void OnWheel(int delta)
        {
            double step = ((Control.ModifierKeys & Keys.Shift) != 0 ? 0.004 : 0.025) * delta / 120.0;
            Set(Denorm(Norm(Value) + step), true);
        }

        public override void OnDoubleClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Set(Default, true); }

        public override void Paint(Graphics g)
        {
            var b = Bounds;
            int d;
            Rectangle kr;
            if (Compact)
            {
                d = KnobSize > 0 ? KnobSize : Math.Min(b.Height, Theme.S(34));
                kr = new Rectangle(b.X, b.Y + (b.Height - d) / 2, d, d);
            }
            else
            {
                d = KnobSize > 0 ? KnobSize : Math.Min(b.Width - Theme.S(8), b.Height - Theme.S(34));
                kr = new Rectangle(b.X + (b.Width - d) / 2, b.Y + Theme.S(2), d, d);
            }
            bool en = Enabled;
            float ring = Math.Max(2f, d * 0.075f);
            var arcR = new RectangleF(kr.X + ring / 2, kr.Y + ring / 2, kr.Width - ring, kr.Height - ring);

            using (var pen = new Pen(Theme.Control, ring)) g.DrawArc(pen, arcR, 135, 270);
            double n = Norm(Value);
            float start = 135, sweep;
            if (Bipolar) { double c = Norm(Log ? Math.Sqrt(Min * Max) : (Min + Max) / 2); start = (float)(135 + 270 * c); sweep = (float)(270 * (n - c)); }
            else sweep = (float)(270 * n);
            if (Math.Abs(sweep) > 0.5f)
                using (var pen = new Pen(en ? Accent : Theme.TextFaint, ring)) { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawArc(pen, arcR, start, sweep); }

            float inset = ring * 1.9f;
            var body = new RectangleF(kr.X + inset, kr.Y + inset, kr.Width - inset * 2, kr.Height - inset * 2);
            Color top = Hover && en ? Color.FromArgb(78, 79, 90) : Color.FromArgb(64, 65, 74);
            using (var br = new LinearGradientBrush(body, top, Color.FromArgb(34, 35, 41), 90f)) g.FillEllipse(br, body);
            using (var pen = new Pen(Color.FromArgb(24, 0, 0, 0), 1f)) g.DrawEllipse(pen, body);

            double ang = (135 + 270 * n) * Math.PI / 180;
            float cx = body.X + body.Width / 2, cy = body.Y + body.Height / 2, r1 = body.Width * 0.18f, r2 = body.Width * 0.44f;
            using (var pen = new Pen(en ? Theme.Text : Theme.TextFaint, Math.Max(1.5f, d * 0.05f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx + (float)Math.Cos(ang) * r1, cy + (float)Math.Sin(ang) * r1, cx + (float)Math.Cos(ang) * r2, cy + (float)Math.Sin(ang) * r2);
            }

            Color vc = en ? Theme.Text : Theme.TextFaint;
            if (Compact)
            {
                int tx = kr.Right + Theme.S(7);
                var lr = new Rectangle(tx, b.Y + b.Height / 2 - Theme.S(15), b.Right - tx, Theme.S(14));
                var vr = new Rectangle(tx, b.Y + b.Height / 2, b.Right - tx, Theme.S(16));
                Theme.Text_(g, Label, Theme.UITiny, Theme.TextDim, lr, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Theme.Text_(g, Fmt(Value), Theme.UI, vc, vr, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                var vr = new Rectangle(b.X - Theme.S(6), kr.Bottom + Theme.S(2), b.Width + Theme.S(12), Theme.S(16));
                var lr = new Rectangle(b.X - Theme.S(6), vr.Bottom, b.Width + Theme.S(12), Theme.S(14));
                Theme.TextC(g, Fmt(Value), Theme.UI, vc, vr);
                Theme.TextC(g, Label, Theme.UITiny, Theme.TextDim, lr);
                if (LeftHint != null)
                {
                    Theme.Text_(g, LeftHint, Theme.UITiny, Theme.TextFaint, new Rectangle(kr.X - Theme.S(30), kr.Bottom - Theme.S(12), Theme.S(30), Theme.S(12)), TextFormatFlags.Right);
                    Theme.Text_(g, RightHint, Theme.UITiny, Theme.TextFaint, new Rectangle(kr.Right, kr.Bottom - Theme.S(12), Theme.S(30), Theme.S(12)), TextFormatFlags.Left);
                }
            }
        }
    }

    enum Glyph { None, Power, Left, Right, Close, Gear, Save, Speaker }

    sealed class Button_ : Widget
    {
        public string Text = "";
        public Glyph Icon;
        public bool DropArrow, Flat, Active;
        public Action Click;
        public Color? TextColor;

        public override void OnUp(MouseEventArgs e, bool inside) { if (inside && e.Button == MouseButtons.Left && Click != null) Click(); }

        public override void Paint(Graphics g)
        {
            var r = Bounds;
            bool en = Enabled;
            if (!Flat || Hover || Pressed)
            {
                Color bg = Pressed ? Theme.ControlHi : (Hover && en ? Theme.ControlHi : Theme.Control);
                if (Flat) bg = Theme.A(bg, 200);
                Theme.FillRound(g, bg, r, Theme.SF(5));
            }
            Color fg = !en ? Theme.TextFaint : (TextColor ?? (Active ? Theme.Accent : Theme.Text));
            int x = r.X + Theme.S(8);
            if (Icon != Glyph.None)
            {
                int gs = Theme.S(14);
                var gr = Text.Length == 0 ? new Rectangle(r.X + (r.Width - gs) / 2, r.Y + (r.Height - gs) / 2, gs, gs)
                                          : new Rectangle(x, r.Y + (r.Height - gs) / 2, gs, gs);
                DrawGlyph(g, Icon, gr, fg);
                x = gr.Right + Theme.S(6);
            }
            if (Text.Length > 0)
            {
                int right = r.Right - (DropArrow ? Theme.S(20) : Theme.S(8));
                var tr = new Rectangle(x, r.Y, right - x, r.Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (Icon == Glyph.None && !DropArrow ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
                if (Icon == Glyph.None && !DropArrow) tr = new Rectangle(r.X + Theme.S(4), r.Y, r.Width - Theme.S(8), r.Height);
                Theme.Text_(g, Text, Theme.UI, fg, tr, flags);
            }
            if (DropArrow)
            {
                float cx = r.Right - Theme.S(12), cy = r.Y + r.Height / 2f, s = Theme.SF(3.5);
                using (var pen = new Pen(Theme.TextDim, Theme.SF(1.4))) { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) }); }
            }
        }

        public static void DrawGlyph(Graphics g, Glyph gl, Rectangle r, Color c)
        {
            if (Theme.HasIconFont && (gl == Glyph.Gear || gl == Glyph.Speaker || gl == Glyph.Save))
            {
                string ch = gl == Glyph.Gear ? "" : (gl == Glyph.Speaker ? "" : "");
                var rr = Rectangle.Inflate(r, Theme.S(4), Theme.S(4));
                TextRenderer.DrawText(g, ch, Theme.Glyph, rr, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            float w = Math.Max(1.4f, Theme.SF(1.6));
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = r.Width / 2f;
            using (var pen = new Pen(c, w))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (gl)
                {
                    case Glyph.Power:
                        g.DrawArc(pen, cx - s * 0.8f, cy - s * 0.7f, s * 1.6f, s * 1.6f, -50, 280);
                        g.DrawLine(pen, cx, cy - s * 0.95f, cx, cy - s * 0.1f);
                        break;
                    case Glyph.Left:
                        g.DrawLines(pen, new[] { new PointF(cx + s * 0.3f, cy - s * 0.55f), new PointF(cx - s * 0.3f, cy), new PointF(cx + s * 0.3f, cy + s * 0.55f) });
                        break;
                    case Glyph.Right:
                        g.DrawLines(pen, new[] { new PointF(cx - s * 0.3f, cy - s * 0.55f), new PointF(cx + s * 0.3f, cy), new PointF(cx - s * 0.3f, cy + s * 0.55f) });
                        break;
                    case Glyph.Close:
                        g.DrawLine(pen, cx - s * 0.5f, cy - s * 0.5f, cx + s * 0.5f, cy + s * 0.5f);
                        g.DrawLine(pen, cx + s * 0.5f, cy - s * 0.5f, cx - s * 0.5f, cy + s * 0.5f);
                        break;
                    case Glyph.Gear:
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(pen, cx + (float)Math.Cos(a) * s * 0.55f, cy + (float)Math.Sin(a) * s * 0.55f, cx + (float)Math.Cos(a) * s * 0.92f, cy + (float)Math.Sin(a) * s * 0.92f);
                        }
                        g.DrawEllipse(pen, cx - s * 0.55f, cy - s * 0.55f, s * 1.1f, s * 1.1f);
                        g.DrawEllipse(pen, cx - s * 0.2f, cy - s * 0.2f, s * 0.4f, s * 0.4f);
                        break;
                    case Glyph.Speaker:
                        g.DrawPolygon(pen, new[] { new PointF(cx - s * 0.85f, cy - s * 0.3f), new PointF(cx - s * 0.45f, cy - s * 0.3f), new PointF(cx, cy - s * 0.75f),
                                                   new PointF(cx, cy + s * 0.75f), new PointF(cx - s * 0.45f, cy + s * 0.3f), new PointF(cx - s * 0.85f, cy + s * 0.3f) });
                        g.DrawArc(pen, cx - s * 0.2f, cy - s * 0.45f, s * 0.7f, s * 0.9f, -50, 100);
                        g.DrawArc(pen, cx - s * 0.35f, cy - s * 0.8f, s * 1.2f, s * 1.6f, -50, 100);
                        break;
                    case Glyph.Save:
                        g.DrawRectangle(pen, cx - s * 0.75f, cy - s * 0.75f, s * 1.5f, s * 1.5f);
                        g.DrawRectangle(pen, cx - s * 0.4f, cy - s * 0.75f, s * 0.8f, s * 0.5f);
                        break;
                }
            }
        }
    }

    /// Round power toggle (accent blue when on).
    sealed class PowerToggle : Widget
    {
        public bool On = true;
        public Action<bool> Toggled;

        public override void OnUp(MouseEventArgs e, bool inside)
        {
            if (!inside || e.Button != MouseButtons.Left) return;
            On = !On;
            Invalidate();
            if (Toggled != null) Toggled(On);
        }

        public override void Paint(Graphics g)
        {
            var r = Bounds;
            Color ring = On ? Theme.Accent : Theme.ControlLine;
            Color bg = On ? Theme.AccentBg : Theme.Control;
            if (Hover) bg = Theme.Mix(bg, Color.White, 0.06);
            using (var b = new SolidBrush(bg)) g.FillEllipse(b, r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f);
            using (var p = new Pen(ring, Math.Max(1.2f, Theme.SF(1.3)))) g.DrawEllipse(p, r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f);
            int gs = (int)(r.Width * 0.5);
            Button_.DrawGlyph(g, Glyph.Power, new Rectangle(r.X + (r.Width - gs) / 2, r.Y + (r.Height - gs) / 2 + 1, gs, gs), On ? Theme.AccentHi : Theme.TextDim);
        }
    }

    /// Two or more mutually exclusive options (e.g. Stereo | Mid/Side).
    sealed class Segmented : Widget
    {
        public string[] Options = new string[0];
        public int Selected;
        public Action<int> SelectedChanged;

        public override void OnUp(MouseEventArgs e, bool inside)
        {
            if (!inside || e.Button != MouseButtons.Left || Options.Length == 0) return;
            int i = (e.X - Bounds.X) * Options.Length / Math.Max(1, Bounds.Width);
            i = Math.Max(0, Math.Min(Options.Length - 1, i));
            if (i == Selected) return;
            Selected = i;
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(i);
        }

        public override void Paint(Graphics g)
        {
            var r = Bounds;
            Theme.FillRound(g, Theme.Control, r, Theme.SF(5));
            if (Options.Length == 0) return;
            float w = r.Width / (float)Options.Length;
            for (int i = 0; i < Options.Length; i++)
            {
                var sr = new RectangleF(r.X + w * i + 2, r.Y + 2, w - 4, r.Height - 4);
                bool sel = i == Selected;
                if (sel) Theme.FillRound(g, Color.FromArgb(28, 54, 90), sr,Theme.SF(4));
                Theme.TextC(g, Options[i], Theme.UISmall, sel ? Theme.AccentHi : (Enabled ? Theme.TextDim : Theme.TextFaint), Rectangle.Round(sr));
            }
        }
    }

    /// Static/background painter (non-interactive area that still blocks clicks from falling through).
    sealed class Box : Widget
    {
        public Color Fill = Theme.Panel;
        public Color Line = Theme.ControlLine;
        public float Radius = 10;
        public Action<Graphics, Rectangle> Painter;
        public override Cursor Cursor { get { return Cursors.Default; } }

        public override void Paint(Graphics g)
        {
            var r = new RectangleF(Bounds.X + 0.5f, Bounds.Y + 0.5f, Bounds.Width - 1, Bounds.Height - 1);
            if (Fill.A > 0) Theme.FillRound(g, Fill, r, Theme.SF(Radius));
            if (Line.A > 0) Theme.StrokeRound(g, Line, r, Theme.SF(Radius), 1f);
            if (Painter != null) Painter(g, Bounds);
        }
    }

    sealed class Label_ : Widget
    {
        public string Text = "";
        public Font Font;
        public Color Color = Theme.TextDim;
        public TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        public override Cursor Cursor { get { return Cursors.Default; } }
        public override void Paint(Graphics g) { Theme.Text_(g, Text, Font ?? Theme.UI, Color, Bounds, Flags); }
    }
}
