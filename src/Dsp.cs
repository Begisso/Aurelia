using System;
using System.Threading;

namespace Aurelia
{
    sealed class DspSnapshot
    {
        public readonly EqState State;
        public readonly bool Power;
        public DspSnapshot(EqState s, bool power) { State = s; Power = power; }
    }

    /// Biquad coefficient design (RBJ cookbook + Butterworth cascades). Each section = b0,b1,b2,a1,a2 (a0 normalised).
    static class FilterDesign
    {
        public const int MaxSections = 8;

        public static int Design(FilterType type, double f0, double gainDb, double q, int slope, double fs, double[] c)
        {
            double nyq = fs * 0.5;
            if (f0 > nyq * 0.985) f0 = nyq * 0.985;
            if (f0 < 5) f0 = 5;
            if (q < Band.MinQ) q = Band.MinQ;
            if (q > Band.MaxQ) q = Band.MaxQ;
            switch (type)
            {
                case FilterType.Bell: Peak(f0, gainDb, q, fs, c, 0); return 1;
                case FilterType.LowShelf: Shelf(false, f0, gainDb, q, fs, c, 0); return 1;
                case FilterType.HighShelf: Shelf(true, f0, gainDb, q, fs, c, 0); return 1;
                case FilterType.TiltShelf:
                    Shelf(false, f0, -gainDb * 0.5, 0.5, fs, c, 0);
                    Shelf(true, f0, gainDb * 0.5, 0.5, fs, c, 5);
                    return 2;
                case FilterType.Notch: Rbj(3, f0, q, fs, c, 0); return 1;
                case FilterType.BandPass: Rbj(2, f0, q, fs, c, 0); return 1;
                case FilterType.LowCut: return Butter(true, f0, q, slope, fs, c);
                case FilterType.HighCut: return Butter(false, f0, q, slope, fs, c);
            }
            return 0;
        }

        static void Peak(double f0, double g, double q, double fs, double[] c, int o)
        {
            double A = Math.Pow(10, g / 40), w = 2 * Math.PI * f0 / fs, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            double a0 = 1 + al / A;
            c[o] = (1 + al * A) / a0; c[o + 1] = -2 * cs / a0; c[o + 2] = (1 - al * A) / a0;
            c[o + 3] = -2 * cs / a0; c[o + 4] = (1 - al / A) / a0;
        }

        static void Shelf(bool high, double f0, double g, double q, double fs, double[] c, int o)
        {
            double A = Math.Pow(10, g / 40), w = 2 * Math.PI * f0 / fs, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            double sa = 2 * Math.Sqrt(A) * al;
            double b0, b1, b2, a0, a1, a2;
            if (!high)
            {
                b0 = A * ((A + 1) - (A - 1) * cs + sa); b1 = 2 * A * ((A - 1) - (A + 1) * cs); b2 = A * ((A + 1) - (A - 1) * cs - sa);
                a0 = (A + 1) + (A - 1) * cs + sa; a1 = -2 * ((A - 1) + (A + 1) * cs); a2 = (A + 1) + (A - 1) * cs - sa;
            }
            else
            {
                b0 = A * ((A + 1) + (A - 1) * cs + sa); b1 = -2 * A * ((A - 1) + (A + 1) * cs); b2 = A * ((A + 1) + (A - 1) * cs - sa);
                a0 = (A + 1) - (A - 1) * cs + sa; a1 = 2 * ((A - 1) - (A + 1) * cs); a2 = (A + 1) - (A - 1) * cs - sa;
            }
            c[o] = b0 / a0; c[o + 1] = b1 / a0; c[o + 2] = b2 / a0; c[o + 3] = a1 / a0; c[o + 4] = a2 / a0;
        }

        // kind: 0 lowpass, 1 highpass, 2 bandpass (0 dB peak), 3 notch
        static void Rbj(int kind, double f0, double q, double fs, double[] c, int o)
        {
            double w = 2 * Math.PI * f0 / fs, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            double a0 = 1 + al, b0, b1, b2;
            switch (kind)
            {
                case 0: b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = (1 - cs) / 2; break;
                case 1: b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = (1 + cs) / 2; break;
                case 2: b0 = al; b1 = 0; b2 = -al; break;
                default: b0 = 1; b1 = -2 * cs; b2 = 1; break;
            }
            c[o] = b0 / a0; c[o + 1] = b1 / a0; c[o + 2] = b2 / a0; c[o + 3] = -2 * cs / a0; c[o + 4] = (1 - al) / a0;
        }

        static void FirstOrder(bool hp, double f0, double fs, double[] c, int o)
        {
            double K = Math.Tan(Math.PI * f0 / fs);
            if (hp) { c[o] = 1 / (1 + K); c[o + 1] = -1 / (1 + K); }
            else { c[o] = K / (1 + K); c[o + 1] = K / (1 + K); }
            c[o + 2] = 0; c[o + 3] = (K - 1) / (K + 1); c[o + 4] = 0;
        }

        static int Butter(bool hp, double f0, double q, int slope, double fs, double[] c)
        {
            int order = Math.Max(1, Math.Min(16, slope / 6));
            if (order == 1) { FirstOrder(hp, f0, fs, c, 0); return 1; }
            bool odd = (order & 1) == 1;
            int n = 0;
            if (odd) { FirstOrder(hp, f0, fs, c, 0); n = 1; }
            int pairs = order / 2;
            double res = q / 0.70710678;
            for (int k = 1; k <= pairs; k++)
            {
                double phi = odd ? k * Math.PI / order : (2 * k - 1) * Math.PI / (2.0 * order);
                double qk = 1.0 / (2 * Math.Cos(phi));
                if (k == pairs) qk = Math.Max(0.1, Math.Min(40, qk * res)); // resonance goes on the sharpest section
                Rbj(hp ? 1 : 0, f0, qk, fs, c, n * 5);
                n++;
            }
            return n;
        }

        /// |H|^2 of a section cascade at angular frequency w (given cos/sin of w and 2w).
        public static double MagSq(double[] c, int count, double cw, double sw, double c2w, double s2w)
        {
            double m = 1;
            for (int s = 0; s < count; s++)
            {
                int o = s * 5;
                double nr = c[o] + c[o + 1] * cw + c[o + 2] * c2w, ni = c[o + 1] * sw + c[o + 2] * s2w;
                double dr = 1 + c[o + 3] * cw + c[o + 4] * c2w, di = c[o + 3] * sw + c[o + 4] * s2w;
                m *= (nr * nr + ni * ni) / (dr * dr + di * di + 1e-300);
            }
            return m;
        }
    }

    /// Cascade of biquads (transposed direct form II, double precision) with optional parallel dry/wet blend.
    sealed class Chain
    {
        public readonly double[] C = new double[FilterDesign.MaxSections * 5];
        public readonly double[] Z = new double[FilterDesign.MaxSections * 2];
        public int Count;
        public double Mix = 1;
        public FilterType Type;

        public void Reset() { Array.Clear(Z, 0, Z.Length); }

        public void Process(double[] x, double[] tmp, int n)
        {
            if (Count == 0 || Mix <= 0) return;
            double[] buf = x;
            bool blend = Mix < 0.9999;
            if (blend) { Array.Copy(x, tmp, n); buf = tmp; }
            double[] c = C, z = Z;
            for (int s = 0; s < Count; s++)
            {
                int o = s * 5;
                double b0 = c[o], b1 = c[o + 1], b2 = c[o + 2], a1 = c[o + 3], a2 = c[o + 4];
                double z1 = z[s * 2], z2 = z[s * 2 + 1];
                for (int i = 0; i < n; i++)
                {
                    double xi = buf[i];
                    double y = b0 * xi + z1;
                    z1 = b1 * xi - a1 * y + z2;
                    z2 = b2 * xi - a2 * y;
                    buf[i] = y;
                }
                z[s * 2] = z1; z[s * 2 + 1] = z2;
            }
            if (blend)
            {
                double m = Mix;
                for (int i = 0; i < n; i++) x[i] += m * (tmp[i] - x[i]);
            }
        }
    }

    /// Lock-free ring of the latest mono samples for the spectrum analyzer (written by audio thread, read by UI).
    sealed class AnalyzerTap
    {
        public const int Size = 8192, Mask = Size - 1;
        public readonly float[] Pre = new float[Size], Post = new float[Size];
        public volatile int Pos;
        public volatile int SampleRate = 48000;
        public volatile bool Enabled;
        public int LastWriteTick;

        public void WritePre(float[] L, float[] R, int n)
        {
            int p = Pos;
            for (int i = 0; i < n; i++) Pre[(p + i) & Mask] = (L[i] + R[i]) * 0.5f;
        }

        public void WritePost(float[] L, float[] R, int n)
        {
            int p = Pos;
            for (int i = 0; i < n; i++) Post[(p + i) & Mask] = (L[i] + R[i]) * 0.5f;
            Pos = (p + n) & Mask;
            LastWriteTick = Environment.TickCount;
        }
    }

    /// The whole processing chain. Runs only on the audio thread; Apply() is called there when a new snapshot arrives.
    sealed class Processor
    {
        double fs = 48000;
        readonly Chain[] mid = new Chain[EqState.MaxBands], side = new Chain[EqState.MaxBands];
        readonly Band[] designed = new Band[EqState.MaxBands];
        int bandCount;
        double[] bufA = new double[0], bufB = new double[0], tmp = new double[0];

        // Look-ahead peak limiter
        double[] dlL = new double[512], dlR = new double[512];
        int dlPos, dlLen;
        double limEnv, limGain = 1, limEnvRel, limAtt, limRel;
        const double Ceiling = 0.944; // -0.5 dBFS

        double outGain = 1;
        bool power = true, sleeping;
        double mix = 1, mixStep;
        double denorm = 1e-24;

        public readonly AnalyzerTap Tap = new AnalyzerTap();
        public volatile float PeakL, PeakR;

        public Processor()
        {
            for (int i = 0; i < EqState.MaxBands; i++) { mid[i] = new Chain(); side[i] = new Chain(); }
        }

        public double SampleRate { get { return fs; } }

        public void Configure(double sampleRate)
        {
            fs = sampleRate;
            Tap.SampleRate = (int)sampleRate;
            for (int i = 0; i < EqState.MaxBands; i++) { designed[i] = null; mid[i].Count = 0; side[i].Count = 0; }
            dlLen = Math.Max(16, Math.Min(dlL.Length, (int)(0.0015 * fs)));
            limAtt = 1 - Math.Exp(-1 / (dlLen / 4.0));
            limRel = 1 - Math.Exp(-1 / (0.080 * fs));
            limEnvRel = Math.Exp(-1 / (0.040 * fs));
            mixStep = 1.0 / (0.03 * fs);
            ResetStates();
        }

        void ResetStates()
        {
            for (int i = 0; i < EqState.MaxBands; i++) { mid[i].Reset(); side[i].Reset(); }
            Array.Clear(dlL, 0, dlL.Length); Array.Clear(dlR, 0, dlR.Length);
            dlPos = 0; limEnv = 0; limGain = 1;
        }

        public void Apply(DspSnapshot snap)
        {
            if (snap == null) return;
            power = snap.Power;
            var st = snap.State;
            int n = Math.Min(st.Bands.Count, EqState.MaxBands);
            for (int i = 0; i < n; i++)
            {
                var b = st.Bands[i];
                if (designed[i] != null && designed[i].SameAs(b)) continue;
                DesignBand(i, b);
                designed[i] = b;
            }
            for (int i = n; i < EqState.MaxBands; i++) { mid[i].Count = 0; side[i].Count = 0; designed[i] = null; }
            bandCount = n;
            outGain = Math.Pow(10, st.OutputGain / 20);
        }

        void DesignBand(int i, Band b)
        {
            if (!b.Enabled) { mid[i].Count = 0; side[i].Count = 0; return; }
            double p = b.Placement;
            double wM = p <= 0 ? 1 : 1 - p, wS = p >= 0 ? 1 : 1 + p;
            if (b.HasGain)
            {
                DesignChain(mid[i], b, b.Gain * wM, 1, wM > 0.001 && Math.Abs(b.Gain * wM) > 0.005);
                DesignChain(side[i], b, b.Gain * wS, 1, wS > 0.001 && Math.Abs(b.Gain * wS) > 0.005);
            }
            else
            {
                DesignChain(mid[i], b, 0, wM, wM > 0.001);
                DesignChain(side[i], b, 0, wS, wS > 0.001);
            }
        }

        void DesignChain(Chain ch, Band b, double gain, double mixW, bool active)
        {
            if (!active) { ch.Count = 0; return; }
            int prev = ch.Count;
            FilterType prevType = ch.Type;
            int n = FilterDesign.Design(b.Type, b.Freq, gain, b.Q, b.Slope, fs, ch.C);
            if (n != prev || prevType != b.Type) ch.Reset();
            ch.Type = b.Type;
            ch.Mix = mixW;
            ch.Count = n;
        }

        void Ensure(int n)
        {
            if (bufA.Length >= n) return;
            int sz = Math.Max(n, 4096);
            bufA = new double[sz]; bufB = new double[sz]; tmp = new double[sz];
        }

        public void Process(float[] L, float[] R, int n)
        {
            if (n <= 0) return;
            bool tap = Tap.Enabled;
            if (tap) Tap.WritePre(L, R, n);

            if (!power && mix <= 0)
            {
                sleeping = true; // fully bypassed: no DSP work at all
                Meter(L, R, n);
                if (tap) Tap.WritePost(L, R, n);
                return;
            }
            if (sleeping) { ResetStates(); sleeping = false; }
            Ensure(n);
            double[] A = bufA, B = bufB;

            for (int i = 0; i < n; i++)
            {
                double l = L[i], r = R[i];
                A[i] = (l + r) * 0.5;  // Mid
                B[i] = (l - r) * 0.5;  // Side
            }
            denorm = -denorm;
            A[0] += denorm; B[0] += denorm;

            for (int b = 0; b < bandCount; b++)
            {
                mid[b].Process(A, tmp, n);
                side[b].Process(B, tmp, n);
            }

            for (int i = 0; i < n; i++)
            {
                double m = A[i], s = B[i];
                A[i] = m + s; B[i] = m - s; // back to L/R
            }

            Limiter(A, B, n);

            double target = power ? 1 : 0;
            if (mix == target && target == 1)
            {
                for (int i = 0; i < n; i++) { L[i] = (float)A[i]; R[i] = (float)B[i]; }
            }
            else
            {
                double mx = mix;
                for (int i = 0; i < n; i++)
                {
                    if (mx < target) { mx += mixStep; if (mx > target) mx = target; }
                    else if (mx > target) { mx -= mixStep; if (mx < target) mx = target; }
                    L[i] = (float)(L[i] + mx * (A[i] - L[i]));
                    R[i] = (float)(R[i] + mx * (B[i] - R[i]));
                }
                mix = mx;
            }
            Meter(L, R, n);
            if (tap) Tap.WritePost(L, R, n);
        }

        void Meter(float[] L, float[] R, int n)
        {
            float pl = 0, pr = 0;
            for (int i = 0; i < n; i++)
            {
                float a = L[i] < 0 ? -L[i] : L[i]; if (a > pl) pl = a;
                float b = R[i] < 0 ? -R[i] : R[i]; if (b > pr) pr = b;
            }
            if (pl > PeakL) PeakL = pl;
            if (pr > PeakR) PeakR = pr;
        }

        void Limiter(double[] Lb, double[] Rb, int n)
        {
            double og = outGain, env = limEnv, g = limGain;
            int pos = dlPos, len = dlLen;
            double[] dl = dlL, dr = dlR;
            for (int i = 0; i < n; i++)
            {
                double l = Lb[i] * og, r = Rb[i] * og;
                double p = Math.Max(l < 0 ? -l : l, r < 0 ? -r : r);
                env = p > env ? p : env * limEnvRel;
                double tg = env > Ceiling ? Ceiling / env : 1;
                g += (tg - g) * (tg < g ? limAtt : limRel);
                double ol = dl[pos] * g, orr = dr[pos] * g;
                dl[pos] = l; dr[pos] = r;
                if (++pos >= len) pos = 0;
                if (ol > 0.999) ol = 0.999; else if (ol < -0.999) ol = -0.999;
                if (orr > 0.999) orr = 0.999; else if (orr < -0.999) orr = -0.999;
                Lb[i] = ol; Rb[i] = orr;
            }
            limEnv = env; limGain = g; dlPos = pos;
        }
    }
}
