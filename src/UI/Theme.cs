using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Aurelia
{
    static class Theme
    {
        public static float Scale = 1f;
        public static int S(double v) { return (int)Math.Round(v * Scale); }
        public static float SF(double v) { return (float)(v * Scale); }

        public static readonly Color Bg = Color.FromArgb(19, 20, 23);
        public static readonly Color Bar = Color.FromArgb(27, 28, 32);
        public static readonly Color BarLine = Color.FromArgb(40, 41, 47);
        public static readonly Color Panel = Color.FromArgb(236, 34, 35, 40);
        public static readonly Color Control = Color.FromArgb(44, 45, 52);
        public static readonly Color ControlHi = Color.FromArgb(56, 57, 66);
        public static readonly Color ControlLine = Color.FromArgb(66, 67, 76);
        public static readonly Color Text = Color.FromArgb(232, 232, 236);
        public static readonly Color TextDim = Color.FromArgb(160, 162, 170);
        public static readonly Color TextFaint = Color.FromArgb(105, 107, 115);
        public static readonly Color Accent = Color.FromArgb(77, 158, 255);
        public static readonly Color AccentHi = Color.FromArgb(140, 198, 255);
        public static readonly Color AccentDim = Color.FromArgb(46, 92, 156);
        public static readonly Color AccentBg = Color.FromArgb(20, 40, 66);     // dark blue tint behind accented items
        public static readonly Color Side = Color.FromArgb(96, 226, 180);       // mint, kept apart from the blue Mid curve
        public static readonly Color Warn = Color.FromArgb(255, 170, 64);

        public static readonly Color[] BandColors = {
            Color.FromArgb(198, 232, 58), Color.FromArgb(255, 84, 84), Color.FromArgb(78, 163, 255), Color.FromArgb(214, 92, 255),
            Color.FromArgb(139, 107, 255), Color.FromArgb(47, 214, 168), Color.FromArgb(255, 154, 60), Color.FromArgb(255, 95, 168),
            Color.FromArgb(79, 224, 255), Color.FromArgb(255, 216, 74), Color.FromArgb(156, 255, 92), Color.FromArgb(255, 122, 92),
            Color.FromArgb(92, 139, 255), Color.FromArgb(224, 92, 255), Color.FromArgb(92, 255, 208), Color.FromArgb(255, 179, 92),
            Color.FromArgb(255, 92, 122), Color.FromArgb(92, 200, 255), Color.FromArgb(176, 92, 255), Color.FromArgb(212, 255, 92)
        };

        public static Font UI, UISmall, UIBold, UITiny, Title, Glyph;

        public static void Init(float scale)
        {
            Scale = scale;
            UI = new Font("Segoe UI", 9f);
            UISmall = new Font("Segoe UI", 8f);
            UITiny = new Font("Segoe UI Semibold", 7f);
            UIBold = new Font("Segoe UI Semibold", 9f);
            Title = new Font("Segoe UI Light", 15f);
            Glyph = new Font("Segoe Fluent Icons", 10.5f);
            if (Glyph.Name != "Segoe Fluent Icons") { Glyph.Dispose(); Glyph = new Font("Segoe MDL2 Assets", 10.5f); }
            HasIconFont = Glyph.Name == "Segoe Fluent Icons" || Glyph.Name == "Segoe MDL2 Assets";
        }

        public static bool HasIconFont;

        public static Color A(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, RectangleF r, float rad, float w)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        public static void Text_(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static void TextC(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            Text_(g, s, f, c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new DarkRenderer();
            m.Font = UI;
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            return m;
        }

        public static ToolStripMenuItem Item(string text, EventHandler click)
        {
            var it = new ToolStripMenuItem(text);
            if (click != null) it.Click += click;
            return it;
        }

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        /// Dark title bar; on Windows 11 the caption and border take the exact top-bar colour so they blend in.
        /// Returns true when the custom caption colour was applied.
        public static bool DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            try
            {
                if (Win32.DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) Win32.DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int caption = ColorRef(Bar), border = ColorRef(Bar), text = ColorRef(TextDim);
                bool ok = Win32.DwmSetWindowAttribute(hwnd, 35, ref caption, 4) == 0;   // DWMWA_CAPTION_COLOR
                Win32.DwmSetWindowAttribute(hwnd, 34, ref border, 4);                    // DWMWA_BORDER_COLOR
                Win32.DwmSetWindowAttribute(hwnd, 36, ref text, 4);                      // DWMWA_TEXT_COLOR
                return ok;
            }
            catch { return false; }
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(30, 31, 36);
        static readonly Color Sel = Color.FromArgb(52, 53, 61);
        static readonly Color Line = Color.FromArgb(58, 59, 68);
        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Sel; } }
        public override Color MenuItemSelected { get { return Sel; } }
        public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
        public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
        public override Color MenuItemPressedGradientBegin { get { return Sel; } }
        public override Color MenuItemPressedGradientEnd { get { return Sel; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Back; } }
        public override Color CheckBackground { get { return Back; } }
        public override Color CheckSelectedBackground { get { return Sel; } }
        public override Color CheckPressedBackground { get { return Sel; } }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Min(r.Width, r.Height) * 0.32f;
            using (var pen = new Pen(Theme.Accent,Math.Max(1.6f, Theme.SF(1.8))))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(cx - s, cy), new PointF(cx - s * 0.3f, cy + s * 0.7f), new PointF(cx + s, cy - s * 0.7f) });
            }
        }
    }

    /// Small dark text-input dialog.
    sealed class InputBox : Form
    {
        readonly TextBox box;

        InputBox(string title, string prompt, string value)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bar;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            ClientSize = new Size(Theme.S(340), Theme.S(118));
            var lbl = new Label { Text = prompt, AutoSize = false, Location = new Point(Theme.S(14), Theme.S(12)), Size = new Size(Theme.S(312), Theme.S(20)), ForeColor = Theme.TextDim };
            box = new TextBox { Text = value, Location = new Point(Theme.S(14), Theme.S(36)), Size = new Size(Theme.S(312), Theme.S(24)), BackColor = Theme.Control, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            var ok = MakeButton("OK", DialogResult.OK, Theme.S(170));
            var cancel = MakeButton("Cancel", DialogResult.Cancel, Theme.S(252));
            AcceptButton = ok; CancelButton = cancel;
            Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        }

        Button MakeButton(string text, DialogResult r, int x)
        {
            var b = new Button { Text = text, DialogResult = r, Location = new Point(x, Theme.S(76)), Size = new Size(Theme.S(74), Theme.S(28)), FlatStyle = FlatStyle.Flat, BackColor = Theme.Control, ForeColor = Theme.Text };
            b.FlatAppearance.BorderColor = Theme.ControlLine;
            return b;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(Handle); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); box.SelectAll(); box.Focus(); }

        public static string Ask(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new InputBox(title, prompt, value ?? ""))
            {
                if (owner == null || !(owner is Form) || !((Form)owner).Visible) f.StartPosition = FormStartPosition.CenterScreen;
                return f.ShowDialog(owner) == DialogResult.OK ? f.box.Text : null;
            }
        }
    }

    static class AppIcon
    {
        // Two-colour moon jellyfish: solid dome + three wavy tentacles, transparent background.
        public static readonly Color Dome = Color.FromArgb(30, 111, 217);
        public static readonly Color Tentacle = Color.FromArgb(127, 200, 255);

        public static Bitmap Render(int size, bool on)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                Color dome = on ? Dome : Color.FromArgb(120, 124, 132);
                Color tent = on ? Tentacle : Color.FromArgb(185, 188, 194);

                // dome: upper half of an ellipse; the rim bows gently downward (parabola, deepest at the centre)
                float left = s * 0.08f, right = s * 0.92f, rim = s * 0.45f, top = s * 0.06f, cx = s * 0.5f, bow = s * 0.05f;
                using (var p = new GraphicsPath())
                {
                    p.AddArc(left, top, right - left, (rim - top) * 2, 180, 180);
                    // quadratic (control at cx, rim + 2*bow) written as a cubic: dips exactly `bow` below the rim ends
                    p.AddBezier(right, rim, right - (right - cx) * 2 / 3f, rim + bow * 4 / 3f, left + (cx - left) * 2 / 3f, rim + bow * 4 / 3f, left, rim);
                    p.CloseFigure();
                    using (var b = new SolidBrush(dome)) g.FillPath(b, p);
                }

                // tentacles, mirror-symmetric about the centre: straight middle, outer pair waving and flaring outward at the bottom
                float w = Math.Max(1.5f, s * (size <= 24 ? 0.10f : 0.075f));
                float gap = Math.Max(1f, s * 0.07f), y1 = s * 0.96f;
                float[] xs = { 0.30f, 0.50f, 0.70f };
                float[] len = { 0.90f, 1.0f, 0.90f };
                float[] dir = { 1f, 0f, -1f };
                using (var pen = new Pen(tent, w))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    for (int i = 0; i < 3; i++)
                    {
                        float x = s * xs[i], u = (x - cx) / (cx - left);
                        float y0 = rim + bow * (1 - u * u) + gap;   // follow the curved rim
                        float yEnd = y0 + (y1 - y0) * len[i], a = s * 0.07f * dir[i], flare = s * 0.06f * dir[i];
                        float h = (yEnd - y0) / 3f;
                        if (dir[i] == 0) g.DrawLine(pen, x, y0, x, yEnd);
                        else g.DrawBezier(pen, x, y0, x - a, y0 + h, x + a, y0 + 2 * h, x - flare, yEnd);
                    }
                }
            }
            return bmp;
        }

        public static Icon MakeIcon(int size, bool on)
        {
            using (var bmp = Render(size, on))
            {
                IntPtr h = bmp.GetHicon();
                using (var tmp = Icon.FromHandle(h))
                {
                    var ico = (Icon)tmp.Clone();
                    Win32.DestroyIcon(h);
                    return ico;
                }
            }
        }

        /// Writes a multi-resolution .ico (PNG-compressed entries) used as the exe's icon at build time.
        public static void SaveIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i], true))
                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create))
            using (var w = new System.IO.BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
