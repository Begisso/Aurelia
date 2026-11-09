using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Aurelia
{
    enum FilterType { Bell = 0, LowShelf = 1, HighShelf = 2, LowCut = 3, HighCut = 4, Notch = 5, BandPass = 6, TiltShelf = 7 }

    sealed class Band
    {
        public bool Enabled = true;
        public FilterType Type = FilterType.Bell;
        public double Freq = 1000;
        public double Gain = 0;      // dB
        public double Q = 1;
        public int Slope = 12;       // dB/oct, cut filters only
        public bool MidSide = false; // false = plain stereo band, true = Mid/Side band steered by MS
        public double MS = -1;       // -1 = Mid only, 0 = Mid+Side, +1 = Side only (used when MidSide)

        public int ColorIndex = -1;  // UI only (stable colour while bands are added/removed)

        /// Effective placement: 0 for stereo bands.
        public double Placement { get { return MidSide ? MS : 0; } }

        public static readonly int[] Slopes = { 6, 12, 18, 24, 36, 48, 72, 96 };
        public static readonly string[] TypeNames = { "Bell", "Low Shelf", "High Shelf", "Low Cut", "High Cut", "Notch", "Band Pass", "Tilt Shelf" };

        public const double MinFreq = 10, MaxFreq = 30000, MinQ = 0.025, MaxQ = 40, MaxGain = 30;

        public bool HasGain
        {
            get { return Type == FilterType.Bell || Type == FilterType.LowShelf || Type == FilterType.HighShelf || Type == FilterType.TiltShelf; }
        }
        public bool HasSlope { get { return Type == FilterType.LowCut || Type == FilterType.HighCut; } }

        public Band Clone() { return (Band)MemberwiseClone(); }

        public bool SameAs(Band o)
        {
            return o != null && Enabled == o.Enabled && Type == o.Type && Freq == o.Freq && Gain == o.Gain &&
                   Q == o.Q && Slope == o.Slope && MS == o.MS && MidSide == o.MidSide;
        }

        public string Serialize()
        {
            return string.Join(",", new string[] {
                ((int)Type).ToString(CultureInfo.InvariantCulture), Num.F(Freq), Num.F(Gain), Num.F(Q),
                Slope.ToString(CultureInfo.InvariantCulture), Num.F(MS), Enabled ? "1" : "0", MidSide ? "1" : "0" });
        }

        public static Band Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var p = s.Split(',');
            if (p.Length < 4) return null;
            var b = new Band();
            int t = (int)Num.P(p[0], 0);
            if (t < 0 || t > 7) t = 0;
            b.Type = (FilterType)t;
            b.Freq = Num.Clamp(Num.P(p[1], 1000), MinFreq, MaxFreq);
            b.Gain = Num.Clamp(Num.P(p[2], 0), -MaxGain, MaxGain);
            b.Q = Num.Clamp(Num.P(p[3], 1), MinQ, MaxQ);
            if (p.Length > 4) b.Slope = NearestSlope((int)Num.P(p[4], 12));
            if (p.Length > 5) b.MS = Num.Clamp(Num.P(p[5], 0), -1, 1);
            if (p.Length > 6) b.Enabled = p[6].Trim() != "0";
            if (p.Length > 7) b.MidSide = p[7].Trim() == "1";
            return b;
        }

        public static int NearestSlope(int s)
        {
            int best = 12, bd = int.MaxValue;
            foreach (int v in Slopes) { int d = Math.Abs(v - s); if (d < bd) { bd = d; best = v; } }
            return best;
        }

        public string Describe()
        {
            string f = Num.FormatFreq(Freq);
            if (HasGain) return f + "   " + Num.FormatDb(Gain) + "   Q " + Num.FormatQ(Q);
            if (HasSlope) return f + "   " + Slope + " dB/oct";
            return f + "   Q " + Num.FormatQ(Q);
        }
    }

    sealed class EqState
    {
        public const int MaxBands = 20;
        public List<Band> Bands = new List<Band>();
        public double Clarity, Loudness;          // 0..10
        public double OutputGain;                 // dB

        public EqState Clone()
        {
            var s = new EqState();
            foreach (var b in Bands) s.Bands.Add(b.Clone());
            s.Clarity = Clarity; s.Loudness = Loudness; s.OutputGain = OutputGain;
            return s;
        }

        public bool SameAs(EqState o)
        {
            if (o == null || o.Bands.Count != Bands.Count) return false;
            if (Clarity != o.Clarity || Loudness != o.Loudness || OutputGain != o.OutputGain) return false;
            for (int i = 0; i < Bands.Count; i++) if (!Bands[i].SameAs(o.Bands[i])) return false;
            return true;
        }

        public void WriteTo(Dictionary<string, string> d)
        {
            d["Clarity"] = Num.F(Clarity);
            d["Loudness"] = Num.F(Loudness);
            d["OutputGain"] = Num.F(OutputGain);
            d["Bands"] = Bands.Count.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < Bands.Count; i++) d["Band" + (i + 1)] = Bands[i].Serialize();
        }

        public static EqState ReadFrom(Dictionary<string, string> d)
        {
            var s = new EqState();
            if (d == null) return s;
            s.Clarity = Num.Clamp(Num.P(Get(d, "Clarity"), 0), 0, 10);
            s.Loudness = Num.Clamp(Num.P(Get(d, "Loudness"), 0), 0, 10);
            s.OutputGain = Num.Clamp(Num.P(Get(d, "OutputGain"), 0), -24, 24);
            int n = (int)Num.Clamp(Num.P(Get(d, "Bands"), 0), 0, MaxBands);
            for (int i = 0; i < n; i++)
            {
                var b = Band.Parse(Get(d, "Band" + (i + 1)));
                if (b != null) s.Bands.Add(b);
            }
            return s;
        }

        static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : null; }
    }

    static class Num
    {
        public static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        public static double P(string s, double def)
        {
            double v;
            if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v)) return v;
            return def;
        }
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public static string FormatFreq(double f)
        {
            if (f >= 10000) return (f / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " kHz";
            if (f >= 1000) return (f / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " kHz";
            if (f >= 100) return f.ToString("0", CultureInfo.InvariantCulture) + " Hz";
            return f.ToString("0.0", CultureInfo.InvariantCulture) + " Hz";
        }
        public static string FormatDb(double g)
        {
            return (g > 0.04 ? "+" : "") + g.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }
        public static string FormatQ(double q)
        {
            return q < 10 ? q.ToString("0.00", CultureInfo.InvariantCulture) : q.ToString("0.0", CultureInfo.InvariantCulture);
        }
        public static string FormatMS(double ms)
        {
            if (Math.Abs(ms) < 0.005) return "M + S";
            int pct = (int)Math.Round(Math.Abs(ms) * 100);
            if (pct >= 100) return ms < 0 ? "Mid" : "Side";
            return (ms < 0 ? "Mid " : "Side ") + pct + "%";
        }
    }

    /// Reads FxSound user presets (.fac) so existing custom presets carry over. Factory presets are never read.
    static class FxSoundImport
    {
        public static string FxSoundDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxSound"); }
        }

        public static string ReadSetting(string name)
        {
            try
            {
                string path = Path.Combine(FxSoundDataDir, "FxSound.settings");
                if (!File.Exists(path)) return null;
                var doc = new System.Xml.XmlDocument();
                doc.Load(path);
                foreach (System.Xml.XmlNode n in doc.GetElementsByTagName("VALUE"))
                {
                    var a = n.Attributes;
                    if (a != null && a["name"] != null && a["name"].Value == name && a["val"] != null) return a["val"].Value;
                }
            }
            catch { }
            return null;
        }

        public static List<KeyValuePair<string, EqState>> LoadUserPresets()
        {
            var list = new List<KeyValuePair<string, EqState>>();
            string dir = Path.Combine(FxSoundDataDir, "Presets");
            if (!Directory.Exists(dir)) return list;
            double q = Num.Clamp(Num.P(ReadSetting("filter_q"), 1.0), 0.1, 10);
            foreach (var f in Directory.GetFiles(dir, "*.fac"))
            {
                string name;
                var st = ParseFac(f, q, out name);
                if (st != null) list.Add(new KeyValuePair<string, EqState>(name, st));
            }
            return list;
        }

        static readonly Regex LineRx = new Regex(@"^\s*(-?[0-9.]+)\s*:\s*(.+?)\s*$");

        public static EqState ParseFac(string path, double q, out string name)
        {
            name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var lines = File.ReadAllLines(path, Encoding.Default);
                if (lines.Length < 10 || !lines[0].StartsWith("CLASS1")) return null;
                if (lines[2].Trim().Length > 0) name = lines[2].Trim();
                var main = new double[6];
                var st = new EqState();
                var cfs = new List<double>();
                var gains = new List<double>();
                double cf = -1;
                foreach (var raw in lines)
                {
                    var m = LineRx.Match(raw);
                    if (!m.Success) continue;
                    double v = Num.P(m.Groups[1].Value, 0);
                    string label = m.Groups[2].Value;
                    if (label.StartsWith("Main "))
                    {
                        int k;
                        if (int.TryParse(label.Substring(5), out k) && k >= 0 && k < 6) main[k] = v;
                    }
                    else if (label == "CF") cf = v;
                    else if (label == "Boost/Cut" && cf > 0)
                    {
                        if (cfs.Count < EqState.MaxBands) { cfs.Add(Num.Clamp(cf, Band.MinFreq, 24000)); gains.Add(Num.Clamp(v, -12, 12)); }
                        cf = -1;
                    }
                }
                // FxSound's sliders describe the curve the user sees. Overlapping bells would stack far deeper,
                // so fit the bell gains so the summed response hits each slider value at its centre frequency.
                var fitted = FitGraphic(cfs.ToArray(), gains.ToArray(), q);
                for (int i = 0; i < cfs.Count; i++)
                {
                    if (Math.Abs(fitted[i]) < 0.05) continue;
                    var b = new Band();
                    b.Type = FilterType.Bell;
                    b.Freq = cfs[i];
                    b.Gain = Math.Round(Num.Clamp(fitted[i], -Band.MaxGain, Band.MaxGain), 2);
                    b.Q = q;
                    st.Bands.Add(b);
                }
                // FxSound "Main" slots (0..127): 0 Fidelity, 1 Ambience, 3 Surround, 4 Dynamic Boost, 5 Bass Boost.
                // Fidelity -> Clarity, Dynamic Boost -> Loudness, Bass Boost -> a low-shelf band. Ambience/Surround are dropped.
                st.Clarity = Math.Round(Num.Clamp(main[0] / 127.0 * 10, 0, 10), 1);
                st.Loudness = Math.Round(Num.Clamp(main[4] / 127.0 * 10, 0, 10), 1);
                double bass = Num.Clamp(main[5] / 127.0 * 10, 0, 10);
                st.Bands.Sort(delegate(Band a, Band b) { return a.Freq.CompareTo(b.Freq); });
                if (bass > 0.05 && st.Bands.Count < EqState.MaxBands)
                {
                    var sh = new Band();
                    sh.Type = FilterType.LowShelf; sh.Freq = 120; sh.Gain = Math.Round(1.2 * bass, 1); sh.Q = 0.8;
                    st.Bands.Insert(0, sh);
                }
                return st;
            }
            catch { return null; }
        }

        static double BellDb(double fc, double g, double q, double at, double[] c)
        {
            FilterDesign.Design(FilterType.Bell, fc, g, q, 12, 48000, c);
            double w = 2 * Math.PI * Math.Min(at, 23900) / 48000;
            return 10 * Math.Log10(FilterDesign.MagSq(c, 1, Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w)));
        }

        /// Bell gains whose combined response equals 'target' at each centre (interaction-matrix solve + 3 refinements).
        public static double[] FitGraphic(double[] f, double[] target, double q)
        {
            int n = f.Length;
            var c = new double[5];
            if (n == 0) return new double[0];
            var B = new double[n, n];
            for (int k = 0; k < n; k++)
                for (int m = 0; m < n; m++) B[k, m] = BellDb(f[m], 12, q, f[k], c) / 12;
            var g = Solve(B, target);
            if (g == null) return (double[])target.Clone();
            for (int it = 0; it < 3; it++)
            {
                var r = new double[n];
                for (int k = 0; k < n; k++)
                {
                    double tot = 0;
                    for (int m = 0; m < n; m++) tot += BellDb(f[m], g[m], q, f[k], c);
                    r[k] = target[k] - tot;
                }
                var d = Solve(B, r);
                if (d == null) break;
                for (int k = 0; k < n; k++) g[k] += d[k];
            }
            foreach (var v in g) if (double.IsNaN(v) || Math.Abs(v) > 30) return (double[])target.Clone();
            return g;
        }

        static double[] Solve(double[,] A0, double[] b0)
        {
            int n = b0.Length;
            var A = (double[,])A0.Clone();
            var b = (double[])b0.Clone();
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                if (Math.Abs(A[piv, col]) < 1e-9) return null;
                if (piv != col)
                {
                    for (int k = 0; k < n; k++) { double t = A[col, k]; A[col, k] = A[piv, k]; A[piv, k] = t; }
                    double tb = b[col]; b[col] = b[piv]; b[piv] = tb;
                }
                for (int r = col + 1; r < n; r++)
                {
                    double fct = A[r, col] / A[col, col];
                    for (int k = col; k < n; k++) A[r, k] -= fct * A[col, k];
                    b[r] -= fct * b[col];
                }
            }
            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double s = b[r];
                for (int k = r + 1; k < n; k++) s -= A[r, k] * x[k];
                x[r] = s / A[r, r];
            }
            return x;
        }
    }
}
