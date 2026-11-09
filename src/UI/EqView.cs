using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace Aurelia
{
    /// Radix-2 FFT magnitude spectrum of the newest N samples in an AnalyzerTap ring.
    sealed class Spectrum
    {
        public const int N = 4096;
        readonly float[] win = new float[N];
        readonly double[] re = new double[N], im = new double[N], cosT = new double[N / 2], sinT = new double[N / 2];
        readonly int[] rev = new int[N];
        readonly double norm;

        public Spectrum()
        {
            double sum = 0;
            for (int i = 0; i < N; i++) { win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1))); sum += win[i]; }
            norm = 2.0 / sum;
            int bits = 0;
            while ((1 << bits) < N) bits++;
            for (int i = 0; i < N; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                rev[i] = r;
            }
            for (int i = 0; i < N / 2; i++) { cosT[i] = Math.Cos(2 * Math.PI * i / N); sinT[i] = -Math.Sin(2 * Math.PI * i / N); }
        }

        public void Compute(float[] ring, int pos, float[] outDb)
        {
            int mask = AnalyzerTap.Mask;
            for (int i = 0; i < N; i++)
            {
                int k = rev[i];
                re[k] = ring[(pos - N + i) & mask] * win[i];
                im[k] = 0;
            }
            for (int size = 2; size <= N; size <<= 1)
            {
                int half = size >> 1, step = N / size;
                for (int s = 0; s < N; s += size)
                {
                    for (int j = 0, t = 0; j < half; j++, t += step)
                    {
                        int a = s + j, b = a + half;
                        double wr = cosT[t], wi = sinT[t];
                        double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                    }
                }
            }
            double n2 = norm * norm;
            for (int k = 0; k < N / 2; k++)
                outDb[k] = (float)(10 * Math.Log10((re[k] * re[k] + im[k] * im[k]) * n2 + 1e-20));
        }
    }

    /// Parametric EQ editor: frequency-response display, draggable band nodes, analyzer and meter.
    /// The selected band's controls live in the bottom bar (BandUiChanged tells it to refresh).
    sealed class EqView : WidgetHost
    {
        public const double FMin = 10, FMax = 24000;
        readonly Controller ctl;
        public Action Edited;
        public Action BandUiChanged;

        public int Selected = -1;
        int hoverNode = -1, dragNode = -1;
        Point dragStart;
        double dFreq, dGain, dQ;
        Point mouse = new Point(-10000, -10000);
        string flash;
        int flashUntil;

        Rectangle plot, meterRect;
        double range = 12;
        Bitmap layer;
        bool layerDirty = true;

        // curve geometry
        int cols, colStep;
        float[] colX = new float[0];
        double[] colF = new double[0], cw = new double[0], sw = new double[0], c2w = new double[0], s2w = new double[0];
        readonly float[][] bandDb = new float[EqState.MaxBands][];
        float[] sumMid = new float[0], sumSide = new float[0];
        bool showSide, curvesDirty = true;
        double curveFs;
        readonly double[] coef = new double[FilterDesign.MaxSections * 5];

        // analyzer
        readonly Spectrum spec = new Spectrum();
        readonly float[] bins = new float[Spectrum.N / 2];
        float[] dispPre = new float[0], dispPost = new float[0], colTilt = new float[0], colKf = new float[0];
        int[] colK0 = new int[0], colK1 = new int[0];
        int specFs, specCols = -1;
        bool specVisible;
        const float SpecTop = 0, SpecBottom = -96, Floor = -160;

        readonly Timer timer = new Timer();
        bool active = true;
        float mL = -120, mR = -120, hL = -120, hR = -120;
        int holdTL, holdTR;

        readonly Button_ rangeBtn;

        public EqView(Controller c)
        {
            ctl = c;
            range = ctl.Cfg.DisplayRange;
            TabStop = true;

            rangeBtn = Add(new Button_ { Flat = true, TextColor = Theme.Accent, Tip = "Display range" });
            rangeBtn.Click = ShowRangeMenu;

            timer.Interval = 33;
            timer.Tick += delegate { Frame(); };
            timer.Start();
            EnsureColors();
            SyncPanel();
        }

        Band Sel { get { return Selected >= 0 && Selected < ctl.State.Bands.Count ? ctl.State.Bands[Selected] : null; } }
        public Band SelectedBand { get { return Sel; } }
        List<Band> Bands { get { return ctl.State.Bands; } }

        /// Alternative signal source for analyzer/meter (used by the --screenshot demo).
        public Processor AltDsp;
        Processor Dsp { get { return AltDsp ?? ctl.Engine.Dsp; } }

        public void SetActive(bool on)
        {
            active = on;
            timer.Enabled = on;
            Dsp.Tap.Enabled = on && ctl.Cfg.AnalyzerMode != 0;
            if (on) Invalidate();
        }

        public void StateReplaced()
        {
            Selected = -1;
            hoverNode = dragNode = -1;
            EnsureColors();
            curvesDirty = layerDirty = true;
            SyncPanel();
            Invalidate();
        }

        void EnsureColors()
        {
            var used = new bool[Theme.BandColors.Length];
            foreach (var b in Bands)
            {
                if (b.ColorIndex >= 0 && b.ColorIndex < used.Length && !used[b.ColorIndex]) used[b.ColorIndex] = true;
                else b.ColorIndex = -1;
            }
            foreach (var b in Bands)
            {
                if (b.ColorIndex >= 0) continue;
                for (int i = 0; i < used.Length; i++) if (!used[i]) { b.ColorIndex = i; used[i] = true; break; }
                if (b.ColorIndex < 0) b.ColorIndex = 0;
            }
        }

        public Color BandColor(Band b) { return Theme.BandColors[Math.Max(0, b.ColorIndex) % Theme.BandColors.Length]; }

        // ------------------------------------------------------------ geometry

        float X(double f) { return plot.X + (float)(plot.Width * Math.Log(f / FMin) / Math.Log(FMax / FMin)); }
        double FreqAt(double x) { return FMin * Math.Pow(FMax / FMin, (x - plot.X) / Math.Max(1.0, plot.Width)); }
        double HalfH { get { return plot.Height / 2.0 - Theme.S(16); } }
        float Y(double db) { return (float)(plot.Y + plot.Height / 2.0 - db / range * HalfH); }
        double DbAt(double y) { return (plot.Y + plot.Height / 2.0 - y) / HalfH * range; }
        float SpecY(float db) { return plot.Y + (SpecTop - db) / (SpecTop - SpecBottom) * plot.Height; }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
        }

        void LayoutAll()
        {
            int right = Theme.S(58), labels = Theme.S(22);
            plot = new Rectangle(0, Theme.S(4), Math.Max(10, Width - right), Math.Max(10, Height - Theme.S(4) - labels));
            meterRect = new Rectangle(Width - Theme.S(16), plot.Y + Theme.S(30), Theme.S(10), plot.Height - Theme.S(30));
            rangeBtn.Bounds = new Rectangle(plot.Right + Theme.S(2), plot.Y + Theme.S(2), Theme.S(52), Theme.S(22));

            colStep = Math.Max(1, Theme.S(2));
            cols = plot.Width / colStep + 2;
            if (colX.Length != cols)
            {
                colX = new float[cols]; colF = new double[cols];
                cw = new double[cols]; sw = new double[cols]; c2w = new double[cols]; s2w = new double[cols];
                sumMid = new float[cols]; sumSide = new float[cols];
                dispPre = new float[cols]; dispPost = new float[cols];
                for (int i = 0; i < cols; i++) { dispPre[i] = Floor; dispPost[i] = Floor; }
                for (int i = 0; i < bandDb.Length; i++) bandDb[i] = null;
            }
            for (int i = 0; i < cols; i++) { colX[i] = plot.X + i * colStep; colF[i] = FreqAt(colX[i]); }
            curveFs = 0; // force trig refresh
            specCols = -1;
            curvesDirty = layerDirty = true;
            SyncPanel();
            Invalidate();
        }

        // ------------------------------------------------------------ curves

        void EnsureCurves()
        {
            double fs = ctl.Engine.SampleRate > 0 ? ctl.Engine.SampleRate : 48000;
            if (fs != curveFs)
            {
                for (int i = 0; i < cols; i++)
                {
                    double f = Math.Min(colF[i], fs * 0.4995);
                    double w = 2 * Math.PI * f / fs;
                    cw[i] = Math.Cos(w); sw[i] = Math.Sin(w); c2w[i] = Math.Cos(2 * w); s2w[i] = Math.Sin(2 * w);
                }
                curveFs = fs;
                curvesDirty = true;
            }
            if (!curvesDirty) return;
            Array.Clear(sumMid, 0, cols);
            Array.Clear(sumSide, 0, cols);
            showSide = false;
            for (int bi = 0; bi < Bands.Count; bi++)
            {
                var b = Bands[bi];
                if (bandDb[bi] == null || bandDb[bi].Length != cols) bandDb[bi] = new float[cols];
                var arr = bandDb[bi];
                Response(b, b.Gain, fs, arr, 1, null);
                if (!b.Enabled) continue;
                double p = b.Placement;
                if (p == 0) { for (int i = 0; i < cols; i++) { sumMid[i] += arr[i]; sumSide[i] += arr[i]; } continue; }
                showSide = true;
                double wM = p <= 0 ? 1 : 1 - p, wS = p >= 0 ? 1 : 1 + p;
                if (b.HasGain)
                {
                    if (wM > 0.001) Response(b, b.Gain * wM, fs, null, 1, sumMid);
                    if (wS > 0.001) Response(b, b.Gain * wS, fs, null, 1, sumSide);
                }
                else
                {
                    for (int i = 0; i < cols; i++) { sumMid[i] += (float)(arr[i] * wM); sumSide[i] += (float)(arr[i] * wS); }
                }
            }
            curvesDirty = false;
            layerDirty = true;
        }

        void Response(Band b, double gain, double fs, float[] into, double scale, float[] addTo)
        {
            int n = FilterDesign.Design(b.Type, b.Freq, gain, b.Q, b.Slope, fs, coef);
            for (int i = 0; i < cols; i++)
            {
                double db = 10 * Math.Log10(FilterDesign.MagSq(coef, n, cw[i], sw[i], c2w[i], s2w[i]) + 1e-30) * scale;
                if (db < -200) db = -200; else if (db > 200) db = 200;
                if (into != null) into[i] = (float)db;
                if (addTo != null) addTo[i] += (float)db;
            }
        }

        float ClampY(float y) { return Math.Max(plot.Top - 40, Math.Min(plot.Bottom + 40, y)); }

        // ------------------------------------------------------------ painting

        void EnsureLayer()
        {
            int w = Math.Max(1, Width), h = Math.Max(1, Height);
            if (layer != null && !layerDirty && layer.Width == w && layer.Height == h) return;
            if (layer == null || layer.Width != w || layer.Height != h)
            {
                if (layer != null) layer.Dispose();
                layer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            }
            using (var g = Graphics.FromImage(layer))
            {
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(32, 33, 38), Color.FromArgb(17, 18, 21), 90f))
                    g.FillRectangle(br, 0, 0, w, h);
                DrawGrid(g);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.SetClip(plot);
                DrawBandShapes(g);
                g.ResetClip();
            }
            layerDirty = false;
        }

        void DrawGrid(Graphics g)
        {
            using (var minor = new Pen(Color.FromArgb(33, 34, 40)))
            using (var major = new Pen(Color.FromArgb(46, 47, 55)))
            using (var zero = new Pen(Color.FromArgb(78, 79, 90)))
            {
                for (double dec = 10; dec <= 10000; dec *= 10)
                    for (int m = 1; m <= 9; m++)
                    {
                        double f = dec * m;
                        if (f < FMin || f > FMax) continue;
                        float x = X(f);
                        g.DrawLine(m == 1 ? major : minor, x, plot.Top, x, plot.Bottom);
                    }
                double step = range / 4;
                for (int k = -4; k <= 4; k++)
                {
                    float y = Y(k * step);
                    g.DrawLine(k == 0 ? zero : major, plot.Left, y, plot.Right, y);
                    string s = (k > 0 ? "+" : "") + (k * step).ToString(step < 1 ? "0.##" : "0.#", CultureInfo.InvariantCulture);
                    var r = new Rectangle(plot.Right + Theme.S(4), (int)y - Theme.S(8), Theme.S(34), Theme.S(16));
                    if (r.Top > rangeBtn.Bounds.Bottom - Theme.S(4) || k < 4)
                        Theme.Text_(g, s, Theme.UISmall, k == 0 ? Theme.Accent : Theme.A(Theme.Accent, 190), r, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }
            double[] labels = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
            foreach (var f in labels)
            {
                float x = X(f);
                string s = f >= 1000 ? (f / 1000).ToString("0", CultureInfo.InvariantCulture) + "k" : f.ToString("0", CultureInfo.InvariantCulture);
                Theme.TextC(g, s, Theme.UISmall, Theme.TextFaint, new Rectangle((int)x - Theme.S(24), plot.Bottom + Theme.S(3), Theme.S(48), Theme.S(16)));
            }
        }

        PointF[] ShapePoints(float[] arr)
        {
            float y0 = Y(0);
            var pts = new PointF[cols + 2];
            pts[0] = new PointF(colX[0], y0);
            for (int i = 0; i < cols; i++) pts[i + 1] = new PointF(colX[i], ClampY(Y(arr[i])));
            pts[cols + 1] = new PointF(colX[cols - 1], y0);
            return pts;
        }

        void DrawBandShapes(Graphics g)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < Bands.Count; i++)
                {
                    if ((i == Selected) != (pass == 1)) continue;
                    var arr = bandDb[i];
                    if (arr == null) continue;
                    var b = Bands[i];
                    var c = BandColor(b);
                    bool sel = i == Selected;
                    var pts = ShapePoints(arr);
                    if (b.Enabled)
                    {
                        using (var br = new SolidBrush(Theme.A(c, sel ? 72 : 30))) g.FillPolygon(br, pts);
                    }
                    var line = new PointF[cols];
                    Array.Copy(pts, 1, line, 0, cols);
                    using (var pen = new Pen(b.Enabled ? Theme.A(c, sel ? 235 : 120) : Color.FromArgb(70, 150, 150, 160), sel ? 1.6f : 1.1f))
                    {
                        if (!b.Enabled) pen.DashStyle = DashStyle.Dash;
                        g.DrawLines(pen, line);
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (cols < 2 || colX.Length != cols) LayoutAll();
            EnsureCurves();
            EnsureLayer();
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(layer, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            g.SetClip(plot);
            if (specVisible) DrawSpectrum(g);
            DrawSums(g);
            DrawNodes(g);
            g.ResetClip();

            PaintWidgets(g, e.ClipRectangle);
            DrawMeter(g);
            DrawOverlay(g);
        }

        void DrawSums(Graphics g)
        {
            var pts = new PointF[cols];
            if (showSide)
            {
                for (int i = 0; i < cols; i++) pts[i] = new PointF(colX[i], ClampY(Y(sumSide[i])));
                using (var pen = new Pen(Theme.A(Theme.Side, 220), 1.7f)) g.DrawLines(pen, pts);
                Tag_(g, "S", Theme.Side, pts[cols - 1].Y);
            }
            for (int i = 0; i < cols; i++) pts[i] = new PointF(colX[i], ClampY(Y(sumMid[i])));
            using (var pen = new Pen(Theme.Accent, 2.2f)) g.DrawLines(pen, pts);
            if (showSide) Tag_(g, "M", Theme.Accent, pts[cols - 1].Y);
        }

        void Tag_(Graphics g, string s, Color c, float y)
        {
            var r = new Rectangle(plot.Right - Theme.S(20), (int)y - Theme.S(16), Theme.S(16), Theme.S(14));
            Theme.TextC(g, s, Theme.UITiny, c, r);
        }

        /// Vertical drag scale: shelves/tilt sit at half their gain, so they move 2 dB of gain per dB of travel.
        static double GainDragFactor(Band b) { return b.Type == FilterType.Bell ? 1 : 2; }

        PointF NodePos(Band b)
        {
            double db = 0;
            switch (b.Type)
            {
                case FilterType.Bell: db = b.Gain; break;
                case FilterType.LowShelf: case FilterType.HighShelf: case FilterType.TiltShelf: db = b.Gain / 2; break; // on the curve at f0
                case FilterType.LowCut: case FilterType.HighCut:
                    double fs = curveFs > 0 ? curveFs : 48000;
                    int n = FilterDesign.Design(b.Type, b.Freq, 0, b.Q, b.Slope, fs, coef);
                    double w = 2 * Math.PI * Math.Min(b.Freq, fs * 0.49) / fs;
                    db = 10 * Math.Log10(FilterDesign.MagSq(coef, n, Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w)) + 1e-30);
                    break;
            }
            float y = Y(Math.Max(-range * 1.05, Math.Min(range * 1.05, db)));
            return new PointF(X(Math.Max(FMin, Math.Min(FMax, b.Freq))), y);
        }

        void DrawNodes(Graphics g)
        {
            float r0 = Theme.SF(6.5);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < Bands.Count; i++)
                {
                    bool sel = i == Selected;
                    if (sel != (pass == 1)) continue;
                    var b = Bands[i];
                    var p = NodePos(b);
                    var c = BandColor(b);
                    bool hot = i == hoverNode || i == dragNode;
                    float r = sel ? Theme.SF(8) : (hot ? Theme.SF(7.5) : r0);
                    if (sel || hot)
                        using (var br = new SolidBrush(Theme.A(c, 60))) g.FillEllipse(br, p.X - r * 1.9f, p.Y - r * 1.9f, r * 3.8f, r * 3.8f);
                    using (var br = new SolidBrush(b.Enabled ? (hot ? Theme.Mix(c, Color.White, 0.25) : c) : Color.FromArgb(60, 61, 68)))
                        g.FillEllipse(br, p.X - r, p.Y - r, r * 2, r * 2);
                    if (!b.Enabled) using (var pen = new Pen(c, 1.5f)) g.DrawEllipse(pen, p.X - r, p.Y - r, r * 2, r * 2);
                    if (sel) using (var pen = new Pen(Color.White, Theme.SF(2))) g.DrawEllipse(pen, p.X - r, p.Y - r, r * 2, r * 2);
                    if (b.MidSide)
                    {
                        string t = b.MS < -0.5 ? "M" : (b.MS > 0.5 ? "S" : "MS");
                        Theme.TextC(g, t, Theme.UITiny, Theme.Side, new Rectangle((int)(p.X - Theme.S(14)), (int)(p.Y + r + Theme.S(1)), Theme.S(28), Theme.S(12)));
                    }
                }
        }

        void DrawSpectrum(Graphics g)
        {
            int mode = ctl.Cfg.AnalyzerMode;
            var pts = new PointF[cols + 2];
            if ((mode & 1) != 0)
            {
                Fill(pts, dispPre);
                using (var br = new SolidBrush(Color.FromArgb(22, 110, 150, 255))) g.FillPolygon(br, pts);
            }
            if ((mode & 2) != 0)
            {
                Fill(pts, dispPost);
                using (var br = new SolidBrush(Color.FromArgb(26, 225, 228, 236))) g.FillPolygon(br, pts);
                var line = new PointF[cols];
                Array.Copy(pts, 1, line, 0, cols);
                using (var pen = new Pen(Color.FromArgb(80, 210, 214, 224), 1f)) g.DrawLines(pen, line);
            }
        }

        void Fill(PointF[] pts, float[] d)
        {
            float bottom = plot.Bottom + 2;
            pts[0] = new PointF(colX[0], bottom);
            for (int i = 0; i < cols; i++) pts[i + 1] = new PointF(colX[i], Math.Min(bottom, SpecY(d[i])));
            pts[cols + 1] = new PointF(colX[cols - 1], bottom);
        }

        void DrawMeter(Graphics g)
        {
            var r = meterRect;
            int bw = (r.Width - Theme.S(2)) / 2;
            DrawBar(g, new Rectangle(r.X, r.Y, bw, r.Height), mL, hL);
            DrawBar(g, new Rectangle(r.Right - bw, r.Y, bw, r.Height), mR, hR);
        }

        void DrawBar(Graphics g, Rectangle r, float db, float hold)
        {
            using (var br = new SolidBrush(Color.FromArgb(28, 29, 34))) g.FillRectangle(br, r);
            float t = Math.Max(0, Math.Min(1, (db + 60) / 60f));
            int h = (int)(r.Height * t);
            if (h > 0)
            {
                var fr = new Rectangle(r.X, r.Bottom - h, r.Width, h);
                using (var br = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(255, 90, 70), Theme.AccentDim, 90f))
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { Color.FromArgb(255, 90, 70), Theme.Accent, Theme.AccentDim };
                    blend.Positions = new[] { 0f, 0.12f, 1f };
                    br.InterpolationColors = blend;
                    g.FillRectangle(br, fr);
                }
            }
            if (hold > -59)
            {
                int y = r.Bottom - (int)(r.Height * Math.Max(0, Math.Min(1, (hold + 60) / 60f)));
                using (var pen = new Pen(hold > -0.5f ? Color.FromArgb(255, 90, 70) : Theme.AccentHi)) g.DrawLine(pen, r.X, y, r.Right - 1, y);
            }
        }

        void DrawOverlay(Graphics g)
        {
            int n = dragNode >= 0 ? dragNode : hoverNode;
            if (n >= 0 && n < Bands.Count)
            {
                var b = Bands[n];
                var p = NodePos(b);
                string l1 = (n + 1) + "  " + Band.TypeNames[(int)b.Type] + (b.MidSide ? "  ·  " + Num.FormatMS(b.MS) : "") + (b.Enabled ? "" : "  ·  off");
                string l2 = b.Describe();
                int w = Theme.S(190), h = Theme.S(40);
                int x = (int)p.X + Theme.S(14), y = (int)p.Y - h - Theme.S(10);
                if (x + w > plot.Right - Theme.S(4)) x = (int)p.X - w - Theme.S(14);
                if (y < plot.Top + Theme.S(4)) y = (int)p.Y + Theme.S(14);
                var r = new Rectangle(x, y, w, h);
                Theme.FillRound(g, Color.FromArgb(235, 22, 23, 27), r, Theme.SF(6));
                Theme.StrokeRound(g, Theme.A(BandColor(b), 170), r, Theme.SF(6), 1f);
                Theme.Text_(g, l1, Theme.UITiny, Theme.TextDim, new Rectangle(x + Theme.S(9), y + Theme.S(4), w - Theme.S(14), Theme.S(15)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Theme.Text_(g, l2, Theme.UI, Theme.Text, new Rectangle(x + Theme.S(9), y + Theme.S(19), w - Theme.S(14), Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            else if (plot.Contains(mouse))
            {
                string s = Num.FormatFreq(FreqAt(mouse.X)) + "   " + Num.FormatDb(DbAt(mouse.Y));
                Theme.Text_(g, s, Theme.UISmall, Theme.TextFaint, new Rectangle(plot.X + Theme.S(10), plot.Y + Theme.S(6), Theme.S(200), Theme.S(16)), TextFormatFlags.Left);
            }
            if (Bands.Count == 0)
            {
                Theme.TextC(g, "Double-click anywhere to add a band  ·  up to " + EqState.MaxBands + " bands", Theme.UI, Theme.TextFaint,
                            new Rectangle(plot.X, plot.Y + plot.Height / 2 - Theme.S(40), plot.Width, Theme.S(20)));
            }
            if (flash != null)
            {
                var r = new Rectangle(plot.X + plot.Width / 2 - Theme.S(160), plot.Y + Theme.S(14), Theme.S(320), Theme.S(28));
                Theme.FillRound(g, Color.FromArgb(235, 20, 34, 56), r,Theme.SF(6));
                Theme.TextC(g, flash, Theme.UI, Theme.AccentHi, r);
            }
        }

        // ------------------------------------------------------------ animation (analyzer + meter)

        void Frame()
        {
            if (!active) return;
            var dsp = Dsp;
            var tap = dsp.Tap;
            int mode = ctl.Cfg.AnalyzerMode;
            tap.Enabled = mode != 0;
            int now = Environment.TickCount;
            bool dirty = false;
            bool fresh = mode != 0 && (AltDsp != null || ctl.Engine.Connected) && unchecked(now - tap.LastWriteTick) < 150;
            if (mode != 0 && (fresh || specVisible))
            {
                specVisible = UpdateSpectrum(fresh, tap, mode);
                dirty = true;
            }
            else if (mode == 0 && specVisible) { specVisible = false; dirty = true; }

            float pl = dsp.PeakL, pr = dsp.PeakR;
            dsp.PeakL = 0; dsp.PeakR = 0;
            dirty |= Meter(ref mL, ref hL, ref holdTL, pl, now);
            dirty |= Meter(ref mR, ref hR, ref holdTR, pr, now);

            if (ctl.Engine.SampleRate > 0 && ctl.Engine.SampleRate != curveFs) dirty = true;
            if (flash != null && now - flashUntil > 0) { flash = null; dirty = true; }
            if (dirty) Invalidate();
        }

        static bool Meter(ref float level, ref float hold, ref int holdTick, float peak, int now)
        {
            float db = peak > 1e-6f ? (float)(20 * Math.Log10(peak)) : -120;
            float before = level, beforeHold = hold;
            level = db > level ? db : Math.Max(db, level - 1.6f);
            if (level > hold) { hold = level; holdTick = now; }
            else if (now - holdTick > 1200) hold = Math.Max(level, hold - 1.2f);
            if (level < -61) level = -120;
            if (hold < -61) hold = -120;
            return Math.Abs(level - before) > 0.05f || Math.Abs(hold - beforeHold) > 0.05f;
        }

        void PrepareSpectrumMap(int fs)
        {
            if (specCols == cols && specFs == fs) return;
            colK0 = new int[cols]; colK1 = new int[cols]; colKf = new float[cols]; colTilt = new float[cols];
            double binHz = fs / (double)Spectrum.N;
            int maxK = Spectrum.N / 2 - 2;
            for (int i = 0; i < cols; i++)
            {
                double f0 = FreqAt(colX[i] - colStep / 2.0), f1 = FreqAt(colX[i] + colStep / 2.0);
                int k0 = (int)Math.Ceiling(f0 / binHz), k1 = (int)Math.Floor(f1 / binHz);
                if (colF[i] >= fs / 2.0) { colK0[i] = -1; colK1[i] = -1; colKf[i] = -1; }
                else if (k1 >= k0 && k0 >= 1) { colK0[i] = k0; colK1[i] = Math.Min(k1, maxK); colKf[i] = -1; }
                else { colK0[i] = colK1[i] = 0; colKf[i] = (float)Math.Min(maxK, colF[i] / binHz); }
                colTilt[i] = (float)(4.5 * Math.Log(colF[i] / 1000.0, 2));
            }
            specCols = cols; specFs = fs;
        }

        bool UpdateSpectrum(bool fresh, AnalyzerTap tap, int mode)
        {
            bool any = false;
            if (fresh) PrepareSpectrumMap(tap.SampleRate);
            int pos = tap.Pos;
            for (int which = 0; which < 2; which++)
            {
                if ((mode & (1 << which)) == 0) continue;
                float[] disp = which == 0 ? dispPre : dispPost;
                if (fresh)
                {
                    spec.Compute(which == 0 ? tap.Pre : tap.Post, pos, bins);
                    for (int i = 0; i < cols; i++)
                    {
                        float v;
                        if (colK0[i] < 0 && colKf[i] < 0) v = Floor;
                        else if (colKf[i] >= 0)
                        {
                            int k = (int)colKf[i]; float t = colKf[i] - k;
                            v = bins[k] + (bins[k + 1] - bins[k]) * t;
                        }
                        else
                        {
                            v = bins[colK0[i]];
                            for (int k = colK0[i] + 1; k <= colK1[i]; k++) if (bins[k] > v) v = bins[k];
                        }
                        v += colTilt[i];
                        float d = disp[i];
                        d = v > d ? d + (v - d) * 0.65f : Math.Max(v, d - 1.4f);
                        disp[i] = d;
                        if (d > SpecBottom) any = true;
                    }
                }
                else
                {
                    for (int i = 0; i < cols; i++)
                    {
                        float d = disp[i] - 2.5f;
                        if (d < Floor) d = Floor;
                        disp[i] = d;
                        if (d > SpecBottom) any = true;
                    }
                }
            }
            return any || fresh;
        }

        // ------------------------------------------------------------ editing

        public void BandsChanged()
        {
            curvesDirty = layerDirty = true;
            SyncPanel();
            Invalidate();
            ctl.StateEdited();
            if (Edited != null) Edited();
        }

        public void SelectBand(int i) { Select(i); }

        void Select(int i)
        {
            if (i >= Bands.Count) i = -1;
            if (i == Selected) return;
            Selected = i;
            layerDirty = true;
            SyncPanel();
            Invalidate();
        }

        void SyncPanel()
        {
            rangeBtn.Text = range.ToString("0", CultureInfo.InvariantCulture) + " dB";
            if (BandUiChanged != null) BandUiChanged();
        }

        void AddBand(double freq, double gain)
        {
            if (Bands.Count >= EqState.MaxBands) { Flash("All " + EqState.MaxBands + " bands are in use"); return; }
            var b = new Band();
            b.Freq = Math.Round(Math.Max(FMin, Math.Min(FMax, freq)), 1);
            if (freq < 28) { b.Type = FilterType.LowCut; b.Q = 0.7071; b.Slope = 24; }
            else if (freq > 16000) { b.Type = FilterType.HighCut; b.Q = 0.7071; b.Slope = 12; }
            else { b.Type = FilterType.Bell; b.Gain = Math.Round(Math.Max(-Band.MaxGain, Math.Min(Band.MaxGain, gain)), 1); b.Q = 1; }
            Bands.Add(b);
            EnsureColors();
            Selected = Bands.Count - 1;
            BandsChanged();
        }

        public void DeleteBand(int i)
        {
            if (i < 0 || i >= Bands.Count) return;
            Bands.RemoveAt(i);
            for (int k = i; k < bandDb.Length - 1; k++) bandDb[k] = bandDb[k + 1];
            bandDb[bandDb.Length - 1] = null;
            Selected = -1;
            hoverNode = -1;
            BandsChanged();
        }

        void Flash(string s) { flash = s; flashUntil = Environment.TickCount + 1600; Invalidate(); }

        int NodeAt(Point p)
        {
            float best = Theme.SF(11) * Theme.SF(11);
            int idx = -1;
            for (int i = Bands.Count - 1; i >= 0; i--)
            {
                var q = NodePos(Bands[i]);
                float dx = q.X - p.X, dy = q.Y - p.Y, d = dx * dx + dy * dy;
                if (i == Selected) d *= 0.6f; // prefer the selected node when overlapping
                if (d < best) { best = d; idx = i; }
            }
            return idx;
        }

        protected override bool PreferHost(Point p, Widget w)
        {
            return w == rangeBtn ? false : (dragNode >= 0);
        }

        protected override void HostMouseDown(MouseEventArgs e)
        {
            int n = plot.Contains(e.Location) ? NodeAt(e.Location) : -1;
            if (n >= 0)
            {
                if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Alt) != 0) { DeleteBand(n); return; }
                Select(n);
                if (e.Button == MouseButtons.Right) { ShowBandMenu(e.Location); return; }
                if (e.Button == MouseButtons.Middle) { Bands[n].Enabled = !Bands[n].Enabled; BandsChanged(); return; }
                if (e.Button == MouseButtons.Left)
                {
                    var b = Bands[n];
                    dragNode = n; dragStart = e.Location; dFreq = b.Freq; dGain = b.Gain; dQ = b.Q;
                    Capture = true;
                    Cursor = Cursors.SizeAll;
                }
                return;
            }
            if (!plot.Contains(e.Location)) return;
            if (e.Button == MouseButtons.Right) { ShowEmptyMenu(e.Location); return; }
            if (e.Button == MouseButtons.Left) Select(-1);
        }

        protected override void HostMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            if (dragNode >= 0 && (e.Button & MouseButtons.Left) != 0 && dragNode < Bands.Count)
            {
                var b = Bands[dragNode];
                double fine = (ModifierKeys & Keys.Shift) != 0 ? 0.15 : 1;
                bool ctrl = (ModifierKeys & Keys.Control) != 0; // Ctrl: frequency only
                double dx = (e.X - dragStart.X) * fine, dy = (e.Y - dragStart.Y) * fine;
                b.Freq = Math.Round(Math.Max(FMin, Math.Min(FMax, dFreq * Math.Pow(FMax / FMin, dx / Math.Max(1, plot.Width)))), 2);
                if (!ctrl)
                {
                    if (b.HasGain) b.Gain = Math.Round(Math.Max(-Band.MaxGain, Math.Min(Band.MaxGain, dGain - dy / HalfH * range * GainDragFactor(b))), 2);
                    else b.Q = Math.Max(Band.MinQ, Math.Min(Band.MaxQ, dQ * Math.Pow(2, -dy / Theme.S(60))));
                }
                BandsChanged();
                return;
            }
            int n = plot.Contains(e.Location) ? NodeAt(e.Location) : -1;
            if (n != hoverNode) { hoverNode = n; }
            Cursor = n >= 0 ? Cursors.Hand : (plot.Contains(e.Location) ? Cursors.Cross : Cursors.Default);
            Invalidate();
        }

        protected override void HostMouseUp(MouseEventArgs e)
        {
            if (dragNode >= 0)
            {
                dragNode = -1;
                Capture = false;
                Cursor = Cursors.Hand;
                Invalidate();
            }
        }

        protected override void HostWheel(MouseEventArgs e)
        {
            int n = plot.Contains(e.Location) ? NodeAt(e.Location) : -1;
            if (n < 0) n = Selected;
            if (n < 0 || n >= Bands.Count) return;
            var b = Bands[n];
            double f = Math.Pow((ModifierKeys & Keys.Shift) != 0 ? 1.02 : 1.12, e.Delta / 120.0);
            b.Q = Math.Max(Band.MinQ, Math.Min(Band.MaxQ, b.Q * f));
            Select(n);
            BandsChanged();
        }

        protected override void HostDoubleClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !plot.Contains(e.Location)) return;
            int n = NodeAt(e.Location);
            if (n >= 0) { Bands[n].Enabled = !Bands[n].Enabled; BandsChanged(); return; }
            AddBand(FreqAt(e.X), DbAt(e.Y));
        }

        protected override bool IsInputKey(Keys k)
        {
            Keys c = k & Keys.KeyCode;
            return c == Keys.Left || c == Keys.Right || c == Keys.Up || c == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var b = Sel;
            if (e.KeyCode == Keys.Escape) { Select(-1); return; }
            if (b == null) return;
            bool fine = e.Shift;
            switch (e.KeyCode)
            {
                case Keys.Delete: case Keys.Back: DeleteBand(Selected); return;
                case Keys.Left: b.Freq = Math.Max(FMin, b.Freq / Math.Pow(2, fine ? 1 / 96.0 : 1 / 24.0)); break;
                case Keys.Right: b.Freq = Math.Min(FMax, b.Freq * Math.Pow(2, fine ? 1 / 96.0 : 1 / 24.0)); break;
                case Keys.Up:
                    if (b.HasGain) b.Gain = Math.Min(Band.MaxGain, Math.Round(b.Gain + (fine ? 0.1 : 0.5), 2));
                    else b.Q = Math.Min(Band.MaxQ, b.Q * 1.1);
                    break;
                case Keys.Down:
                    if (b.HasGain) b.Gain = Math.Max(-Band.MaxGain, Math.Round(b.Gain - (fine ? 0.1 : 0.5), 2));
                    else b.Q = Math.Max(Band.MinQ, b.Q / 1.1);
                    break;
                default: return;
            }
            e.Handled = true;
            BandsChanged();
        }

        // ------------------------------------------------------------ menus

        void Popup(ContextMenuStrip m, Point at)
        {
            m.Closed += delegate { BeginInvoke((MethodInvoker)delegate { m.Dispose(); }); };
            m.Show(this, at);
        }

        public void SetType(FilterType t)
        {
            var b = Sel;
            if (b == null || b.Type == t) return;
            bool wasCut = b.HasSlope;
            b.Type = t;
            if (b.HasSlope && !wasCut) b.Q = 0.7071;
            else if (!b.HasSlope && wasCut) b.Q = t == FilterType.LowShelf || t == FilterType.HighShelf ? 0.7071 : 1;
            BandsChanged();
        }

        public void AddShapeItems(ToolStripItemCollection items)
        {
            var b = Sel;
            for (int t = 0; t < Band.TypeNames.Length; t++)
            {
                var tt = (FilterType)t;
                var it = Theme.Item(Band.TypeNames[t], delegate { SetType(tt); });
                it.Checked = b != null && b.Type == tt;
                items.Add(it);
            }
        }

        public void AddSlopeItems(ToolStripItemCollection items)
        {
            var b = Sel;
            foreach (int s in Band.Slopes)
            {
                int ss = s;
                var it = Theme.Item(s + " dB/oct", delegate { var x = Sel; if (x != null) { x.Slope = ss; BandsChanged(); } });
                it.Checked = b != null && b.Slope == s;
                items.Add(it);
            }
        }

        void ShowBandMenu(Point at)
        {
            var b = Sel;
            if (b == null) return;
            var m = Theme.Menu();
            var shape = new ToolStripMenuItem("Shape");
            AddShapeItems(shape.DropDownItems);
            shape.DropDown.Renderer = new DarkRenderer();
            m.Items.Add(shape);
            if (b.HasSlope)
            {
                var slope = new ToolStripMenuItem("Slope");
                AddSlopeItems(slope.DropDownItems);
                slope.DropDown.Renderer = new DarkRenderer();
                m.Items.Add(slope);
            }
            m.Items.Add(new ToolStripSeparator());
            var st = Theme.Item("Stereo", delegate { var x = Sel; if (x != null) { x.MidSide = false; BandsChanged(); } });
            st.Checked = !b.MidSide;
            var mid = Theme.Item("Mid only", delegate { var x = Sel; if (x != null) { x.MidSide = true; x.MS = -1; BandsChanged(); } });
            mid.Checked = b.MidSide && b.MS <= -0.999;
            var side = Theme.Item("Side only", delegate { var x = Sel; if (x != null) { x.MidSide = true; x.MS = 1; BandsChanged(); } });
            side.Checked = b.MidSide && b.MS >= 0.999;
            var ms = Theme.Item("Mid/Side (knob)", delegate { var x = Sel; if (x != null) { x.MidSide = true; BandsChanged(); } });
            ms.Checked = b.MidSide && b.MS > -0.999 && b.MS < 0.999;
            m.Items.AddRange(new ToolStripItem[] { st, mid, side, ms, new ToolStripSeparator() });
            var en = Theme.Item("Enabled", delegate { var x = Sel; if (x != null) { x.Enabled = !x.Enabled; BandsChanged(); } });
            en.Checked = b.Enabled;
            m.Items.Add(en);
            m.Items.Add(Theme.Item("Reset gain", delegate { var x = Sel; if (x != null) { x.Gain = 0; BandsChanged(); } }));
            m.Items.Add(Theme.Item("Delete band", delegate { if (Selected >= 0) DeleteBand(Selected); }));
            Popup(m, at);
        }

        void ShowEmptyMenu(Point at)
        {
            var m = Theme.Menu();
            double f = FreqAt(at.X), db = DbAt(at.Y);
            var add = Theme.Item("Add band at " + Num.FormatFreq(f), delegate { AddBand(f, db); });
            add.Enabled = Bands.Count < EqState.MaxBands;
            m.Items.Add(add);
            var clear = Theme.Item("Remove all bands", delegate
            {
                if (Bands.Count == 0) return;
                Bands.Clear();
                for (int i = 0; i < bandDb.Length; i++) bandDb[i] = null;
                Selected = -1;
                BandsChanged();
            });
            clear.Enabled = Bands.Count > 0;
            m.Items.Add(clear);
            Popup(m, at);
        }

        void ShowRangeMenu()
        {
            var m = Theme.Menu();
            foreach (int r in new[] { 3, 6, 12, 24, 30 })
            {
                int rr = r;
                var it = Theme.Item("±" + r + " dB", delegate
                {
                    range = rr;
                    ctl.Cfg.DisplayRange = rr;
                    ctl.MarkDirty();
                    layerDirty = true;
                    SyncPanel();
                    Invalidate();
                });
                it.Checked = (int)range == r;
                m.Items.Add(it);
            }
            Popup(m, new Point(rangeBtn.Bounds.Right - Theme.S(100), rangeBtn.Bounds.Bottom));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (layer != null) layer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
