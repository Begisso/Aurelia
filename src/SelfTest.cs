using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Aurelia
{
    /// "Aurelia.exe --selftest": verifies filters, DSP, performance and the WASAPI path without changing
    /// the Windows default device and without producing audible output. Writes Aurelia-selftest.log.
    static class SelfTest
    {
        static StringBuilder sb;
        static void W(string s) { sb.AppendLine(s); }
        static string F(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        public static int Run()
        {
            sb = new StringBuilder();
            string log = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Aurelia-selftest.log");
            int failures = 0;
            try
            {
                W("Aurelia self-test  " + DateTime.Now.ToString("s") + "  " + (IntPtr.Size == 8 ? "x64" : "x86") + "  CLR " + Environment.Version);
                failures += FilterChecks();
                failures += ProcessorChecks();
                Bench();
                failures += DeviceChecks();
            }
            catch (Exception ex) { W("EXCEPTION: " + ex); failures++; }
            W("");
            W(failures == 0 ? "RESULT: ALL CHECKS PASSED" : "RESULT: " + failures + " CHECK(S) FAILED");
            File.WriteAllText(log, sb.ToString());
            return failures;
        }

        static int Expect(string what, double got, double want, double tol)
        {
            bool ok = Math.Abs(got - want) <= tol;
            W((ok ? "  ok   " : "  FAIL ") + what + ": " + F(got) + " (want " + F(want) + " ±" + F(tol) + ")");
            return ok ? 0 : 1;
        }

        static double Db(FilterType t, double f0, double g, double q, int slope, double at)
        {
            var c = new double[FilterDesign.MaxSections * 5];
            int n = FilterDesign.Design(t, f0, g, q, slope, 48000, c);
            double w = 2 * Math.PI * at / 48000;
            return 10 * Math.Log10(FilterDesign.MagSq(c, n, Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w)));
        }

        static int FilterChecks()
        {
            W("");
            W("[Filter design @48 kHz]");
            int f = 0;
            f += Expect("Bell 1k +6 dB at 1k", Db(FilterType.Bell, 1000, 6, 1, 12, 1000), 6, 0.01);
            f += Expect("Bell 1k -12 dB at 1k", Db(FilterType.Bell, 1000, -12, 4, 12, 1000), -12, 0.01);
            f += Expect("Low shelf 100 +6 at 20 Hz", Db(FilterType.LowShelf, 100, 6, 0.7071, 12, 20), 6, 0.3);
            f += Expect("Low shelf 100 +6 at 5 kHz", Db(FilterType.LowShelf, 100, 6, 0.7071, 12, 5000), 0, 0.05);
            f += Expect("High shelf 8k -6 at 20 kHz", Db(FilterType.HighShelf, 8000, -6, 0.7071, 12, 20000), -6, 0.4);
            f += Expect("Low cut 100/24 at 100 Hz", Db(FilterType.LowCut, 100, 0, 0.7071, 24, 100), -3.01, 0.05);
            f += Expect("Low cut 100/24 at 50 Hz", Db(FilterType.LowCut, 100, 0, 0.7071, 24, 50), -24.1, 0.3);
            f += Expect("Low cut 80/6 at 80 Hz", Db(FilterType.LowCut, 80, 0, 0.7071, 6, 80), -3.01, 0.05);
            f += Expect("High cut 5k/18 at 5 kHz", Db(FilterType.HighCut, 5000, 0, 0.7071, 18, 5000), -3.01, 0.05);
            f += Expect("High cut 2k/96 at 4 kHz", Db(FilterType.HighCut, 2000, 0, 0.7071, 96, 4000), -96.3, 3);
            f += Expect("Notch 1k at 2 kHz", Db(FilterType.Notch, 1000, 0, 4, 12, 2000), 0, 0.2);
            f += Expect("Band pass 1k at 1k", Db(FilterType.BandPass, 1000, 0, 2, 12, 1000), 0, 0.01);
            f += Expect("Tilt +6 at 20 Hz", Db(FilterType.TiltShelf, 1000, 6, 0.7071, 12, 20), -3, 0.3);
            f += Expect("Tilt +6 at 20 kHz", Db(FilterType.TiltShelf, 1000, 6, 0.7071, 12, 20000), 3, 0.4);
            bool notchDeep = Db(FilterType.Notch, 1000, 0, 4, 12, 1000) < -60;
            W((notchDeep ? "  ok   " : "  FAIL ") + "Notch 1k depth at 1k < -60 dB");
            if (!notchDeep) f++;
            return f;
        }

        static double Rms(float[] x, int from, int n)
        {
            double s = 0;
            for (int i = from; i < from + n; i++) s += x[i] * (double)x[i];
            return 20 * Math.Log10(Math.Sqrt(s / n) + 1e-20);
        }

        static double RunSine(EqState st, double freq, double ampL, double ampR, out double outL, out double outR)
        {
            var p = new Processor();
            p.Configure(48000);
            p.Apply(new DspSnapshot(st, true));
            int total = 48000;
            var L = new float[total];
            var R = new float[total];
            for (int i = 0; i < total; i++)
            {
                double s = Math.Sin(2 * Math.PI * freq * i / 48000);
                L[i] = (float)(s * ampL); R[i] = (float)(s * ampR);
            }
            double inL = Rms(L, 24000, 24000);
            for (int o = 0; o < total; o += 480)
            {
                var bl = new float[480]; var br = new float[480];
                Array.Copy(L, o, bl, 0, 480); Array.Copy(R, o, br, 0, 480);
                p.Process(bl, br, 480);
                Array.Copy(bl, 0, L, o, 480); Array.Copy(br, 0, R, o, 480);
            }
            outL = Rms(L, 24000, 24000);
            outR = Rms(R, 24000, 24000);
            return inL;
        }

        static int ProcessorChecks()
        {
            W("");
            W("[Processor]");
            int f = 0;
            double oL, oR, inL;

            inL = RunSine(new EqState(), 1000, 0.25, 0.25, out oL, out oR);
            f += Expect("Empty state is transparent (dB change)", oL - inL, 0, 0.01);

            var st = new EqState();
            st.Bands.Add(new Band { Type = FilterType.Bell, Freq = 1000, Gain = -20, Q = 1, MidSide = true, MS = -1 });
            inL = RunSine(st, 1000, 0.25, 0.25, out oL, out oR);
            f += Expect("Mid-only bell -20 dB on mono signal", oL - inL, -20, 0.05);
            inL = RunSine(st, 1000, 0.25, -0.25, out oL, out oR);
            f += Expect("Mid-only bell on pure-side signal (untouched)", oL - inL, 0, 0.05);
            st.Bands[0].MS = 1;
            inL = RunSine(st, 1000, 0.25, -0.25, out oL, out oR);
            f += Expect("Side-only bell -20 dB on pure-side signal", oL - inL, -20, 0.05);
            st.Bands[0].MidSide = false;
            inL = RunSine(st, 1000, 0.25, 0.0, out oL, out oR);
            f += Expect("Stereo bell -20 dB on left-only signal", oL - inL, -20, 0.05);
            st.Bands[0].MidSide = true; st.Bands[0].MS = 0;
            inL = RunSine(st, 1000, 0.25, 0.0, out oL, out oR);
            f += Expect("Mid/Side knob centred = stereo", oL - inL, -20, 0.05);

            var hot = new EqState();
            hot.Bands.Add(new Band { Type = FilterType.Bell, Freq = 1000, Gain = 18, Q = 1 });
            RunSine(hot, 1000, 0.9, 0.9, out oL, out oR);
            double peakDb = oL + 3.01; // sine peak = rms + 3 dB
            W((peakDb <= 0.01 ? "  ok   " : "  FAIL ") + "Limiter holds +18 dB boost of a -1 dBFS sine below 0 dBFS (peak ≈ " + F(peakDb) + " dBFS)");
            if (peakDb > 0.01) f++;

            // stability: random extreme settings, white noise, every output finite and within ±1
            var rnd = new Random(7);
            bool finite = true;
            for (int trial = 0; trial < 20 && finite; trial++)
            {
                var s = RandomState(rnd, true);
                var p = new Processor();
                p.Configure(trial % 2 == 0 ? 48000 : 44100);
                p.Apply(new DspSnapshot(s, true));
                var L = new float[512]; var R = new float[512];
                for (int blk = 0; blk < 200 && finite; blk++)
                {
                    for (int i = 0; i < 512; i++) { L[i] = (float)(rnd.NextDouble() * 2 - 1); R[i] = (float)(rnd.NextDouble() * 2 - 1); }
                    if (blk == 100) p.Apply(new DspSnapshot(RandomState(rnd, true), true)); // live change
                    p.Process(L, R, 512);
                    for (int i = 0; i < 512; i++)
                        if (float.IsNaN(L[i]) || float.IsInfinity(L[i]) || Math.Abs(L[i]) > 1f || float.IsNaN(R[i]) || Math.Abs(R[i]) > 1f) { finite = false; break; }
                }
            }
            W((finite ? "  ok   " : "  FAIL ") + "20 random extreme 20-band settings on full-scale noise stay finite and within ±1");
            if (!finite) f++;
            return f;
        }

        static EqState RandomState(Random rnd, bool full)
        {
            var s = new EqState();
            int n = full ? EqState.MaxBands : rnd.Next(1, EqState.MaxBands + 1);
            for (int i = 0; i < n; i++)
            {
                var b = new Band();
                b.Type = (FilterType)rnd.Next(0, 8);
                b.Freq = 10 * Math.Pow(2400, rnd.NextDouble());
                b.Gain = rnd.NextDouble() * 60 - 30;
                b.Q = 0.025 * Math.Pow(1600, rnd.NextDouble());
                b.Slope = Band.Slopes[rnd.Next(Band.Slopes.Length)];
                b.MidSide = rnd.Next(2) == 0;
                b.MS = rnd.NextDouble() * 2 - 1;
                s.Bands.Add(b);
            }
            s.Clarity = rnd.NextDouble() * 10;
            s.Loudness = rnd.NextDouble() * 10;
            s.OutputGain = rnd.NextDouble() * 24 - 12;
            return s;
        }

        static void Bench()
        {
            W("");
            W("[Performance: 60 s of 48 kHz stereo audio, 10 ms blocks, one core]");
            var typical = new EqState();
            double[] fr = { 62.5, 86, 221, 450, 630, 1250, 2700, 5300, 11000, 16000 };
            foreach (var x in fr) typical.Bands.Add(new Band { Freq = x, Gain = -6, Q = 1 });
            typical.Clarity = 5; typical.Loudness = 5;
            BenchOne("typical (10 bells + Clarity + Loudness)", typical);

            var worst = new EqState();
            for (int i = 0; i < EqState.MaxBands; i++)
            {
                var b = new Band { Freq = 20 * Math.Pow(1.4, i), Gain = 3, Q = 2 };
                if (i < 8) { b.Type = i % 2 == 0 ? FilterType.LowCut : FilterType.HighCut; b.Slope = 96; b.Freq = i % 2 == 0 ? 15 : 22000; }
                b.MidSide = i % 2 == 1; b.MS = 0.3;
                worst.Bands.Add(b);
            }
            worst.Clarity = 10; worst.Loudness = 10;
            BenchOne("worst case (20 bands, 8× 96 dB/oct cuts, M/S blends)", worst);
        }

        static void BenchOne(string name, EqState st)
        {
            var p = new Processor();
            p.Configure(48000);
            p.Apply(new DspSnapshot(st, true));
            var L = new float[480]; var R = new float[480];
            var rnd = new Random(1);
            for (int i = 0; i < 480; i++) { L[i] = (float)(rnd.NextDouble() - 0.5); R[i] = (float)(rnd.NextDouble() - 0.5); }
            for (int i = 0; i < 200; i++) p.Process(L, R, 480); // warm-up / JIT
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 6000; i++)
            {
                L[0] = (float)(rnd.NextDouble() - 0.5);
                p.Process(L, R, 480);
            }
            sw.Stop();
            double pct = sw.Elapsed.TotalSeconds / 60.0 * 100;
            W("  " + name + ": " + sw.ElapsedMilliseconds + " ms  →  " + pct.ToString("0.00", CultureInfo.InvariantCulture) + " % of one core");
        }

        // ------------------------------------------------------------ devices / engine

        static int DeviceChecks()
        {
            W("");
            W("[Devices]");
            int f = 0;
            var devs = Devices.ListRender();
            string def = Devices.GetDefaultRenderId(ERole.Console);
            foreach (var d in devs)
                W("  " + (string.Equals(d.Id, def, StringComparison.OrdinalIgnoreCase) ? "* " : "  ") + d.Name + "   formfactor=" + d.FormFactor + (d.LooksVirtual ? "  [virtual]" : ""));
            object pc = null;
            try
            {
                pc = new PolicyConfigClient();
                bool ok = pc is IPolicyConfig;
                W((ok ? "  ok   " : "  FAIL ") + "IPolicyConfig available (default-device switching)");
                if (!ok) f++;
            }
            catch (Exception ex) { W("  FAIL IPolicyConfig: " + ex.Message); f++; }
            finally { Devices.Release(pc); }

            AudioDevice src = null, outp = null;
            foreach (var d in devs)
                if (d.LooksVirtual && (src == null || d.Name.ToLowerInvariant().Contains("fxsound"))) src = d;
            string fxOut = FxSoundImport.ReadSetting("output_device_id");
            foreach (var d in devs)
                if (d != src && string.Equals(d.Id, fxOut, StringComparison.OrdinalIgnoreCase)) outp = d;
            if (outp == null)
                foreach (var d in devs)
                    if (d != src && (outp == null || Devices.ListenScore(d) > Devices.ListenScore(outp))) outp = d;
            W("  Source (capture): " + (src != null ? src.Name : "NONE"));
            W("  Output (render):  " + (outp != null ? outp.Name : "NONE"));

            W("");
            W("[FxSound user presets]");
            foreach (var kv in FxSoundImport.LoadUserPresets())
            {
                W("  '" + kv.Key + "': " + kv.Value.Bands.Count + " bands, Clarity " + F(kv.Value.Clarity) + ", Loudness " + F(kv.Value.Loudness));
                foreach (var b in kv.Value.Bands) W("      " + Band.TypeNames[(int)b.Type] + "  " + b.Describe());
            }

            if (src == null || outp == null) { W("  (engine test skipped: devices missing)"); return f + 1; }

            W("");
            W("[Engine: loopback capture → DSP → render (output muted), -90 dBFS probe tone into the source]");
            var eng = new AudioEngine();
            eng.MuteOutput = true;
            var st = new EqState();
            st.Bands.Add(new Band { Freq = 1000, Gain = 3, Q = 1 });
            st.Clarity = 3;
            eng.Snapshot = new DspSnapshot(st, true);
            eng.Dsp.Tap.Enabled = true;
            eng.Start(src.Id, outp.Id);
            var sw = Stopwatch.StartNew();
            while (!eng.Connected && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            W("  connected: " + eng.Connected + "   status: " + eng.Status);
            W("  capture: " + eng.CaptureFormat + "   render: " + eng.RenderFormat + "   est. latency ~" + eng.LatencyMs + " ms");
            if (!eng.Connected) { eng.Stop(); return f + 1; }

            int packets0 = eng.PacketsCaptured;
            bool wasIdleBefore = eng.Idle;
            var tone = new Thread(() => PlayProbe(src.Id, 3000));
            tone.Start();
            bool sawPlaying = false;
            int playingAt = -1;
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(100);
                if (!eng.Idle && !sawPlaying) { sawPlaying = true; playingAt = (i + 1) * 100; }
            }
            tone.Join();
            int packets = eng.PacketsCaptured - packets0;
            W("  idle before probe: " + wasIdleBefore + "   woke up for probe: " + sawPlaying + (playingAt > 0 ? " (~" + playingAt + " ms)" : ""));
            W("  packets captured in 3 s: " + packets + "   underruns: " + eng.Underruns);
            if (!sawPlaying || packets < 100) { W("  FAIL engine did not process the probe"); f++; }
            else W("  ok   capture/render path works");
            sw.Restart();
            while (!eng.Idle && sw.ElapsedMilliseconds < 9000) Thread.Sleep(100);
            W((eng.Idle ? "  ok   " : "  note ") + "returns to idle after silence: " + eng.Idle + " (" + sw.ElapsedMilliseconds + " ms)" +
              (eng.Idle ? "" : " - other audio may be playing on the source"));
            eng.Stop();
            return f;
        }

        /// Plays an inaudible (-90 dBFS) 1 kHz tone into a device, to exercise the loopback path.
        static void PlayProbe(string deviceId, int ms)
        {
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioClient ac = null; IAudioRenderClient rc = null;
            IntPtr fmt = IntPtr.Zero, mix = IntPtr.Zero;
            try
            {
                en = Devices.CreateEnumerator();
                if (en.GetDevice(deviceId, out dev) != 0) return;
                Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return;
                ac = (IAudioClient)o;
                ac.GetMixFormat(out mix);
                int rate = WaveFormat.Read(mix).SampleRate;
                fmt = WaveFormat.CreateFloat(rate, 2);
                if (ac.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 1000000, 0, fmt, IntPtr.Zero) < 0) return;
                int buf;
                ac.GetBufferSize(out buf);
                Guid iidR = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
                ac.GetService(ref iidR, out o);
                rc = (IAudioRenderClient)o;
                ac.Start();
                var data = new float[buf * 2];
                long n = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    int pad;
                    ac.GetCurrentPadding(out pad);
                    int avail = buf - pad;
                    if (avail > 0)
                    {
                        IntPtr p;
                        if (rc.GetBuffer(avail, out p) == 0)
                        {
                            for (int i = 0; i < avail; i++, n++)
                            {
                                float s = (float)(3e-5 * Math.Sin(2 * Math.PI * 1000 * n / rate));
                                data[2 * i] = s; data[2 * i + 1] = s;
                            }
                            Marshal.Copy(data, 0, p, avail * 2);
                            rc.ReleaseBuffer(avail, 0);
                        }
                    }
                    Thread.Sleep(10);
                }
                ac.Stop();
            }
            catch { }
            finally
            {
                Devices.Release(rc); Devices.Release(ac); Devices.Release(dev); Devices.Release(en);
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            }
        }
    }
}
