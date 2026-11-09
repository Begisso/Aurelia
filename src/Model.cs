using System;
using System.Collections.Generic;
using System.Globalization;

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
        public double OutputGain;                 // dB

        public EqState Clone()
        {
            var s = new EqState();
            foreach (var b in Bands) s.Bands.Add(b.Clone());
            s.OutputGain = OutputGain;
            return s;
        }

        public bool SameAs(EqState o)
        {
            if (o == null || o.Bands.Count != Bands.Count) return false;
            if (OutputGain != o.OutputGain) return false;
            for (int i = 0; i < Bands.Count; i++) if (!Bands[i].SameAs(o.Bands[i])) return false;
            return true;
        }

        public void WriteTo(Dictionary<string, string> d)
        {
            d["OutputGain"] = Num.F(OutputGain);
            d["Bands"] = Bands.Count.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < Bands.Count; i++) d["Band" + (i + 1)] = Bands[i].Serialize();
        }

        public static EqState ReadFrom(Dictionary<string, string> d)
        {
            var s = new EqState();
            if (d == null) return s;
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
}
