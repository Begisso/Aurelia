using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aurelia
{
    sealed class EngineException : Exception
    {
        public EngineException(string msg) : base(msg) { }
    }

    sealed class WaveFormat
    {
        public int Tag, Channels, SampleRate, BlockAlign, Bits, ValidBits, Mask;
        public Guid Sub;
        static readonly Guid SubFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        static readonly Guid SubPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        public bool IsFloat { get { return Tag == 3 || (Tag == 0xFFFE && Sub == SubFloat); } }
        public bool IsPcm { get { return Tag == 1 || (Tag == 0xFFFE && Sub == SubPcm); } }
        public bool Supported
        {
            get { return Channels >= 1 && Channels <= 32 && ((IsFloat && Bits == 32) || (IsPcm && (Bits == 16 || Bits == 24 || Bits == 32))); }
        }

        public static WaveFormat Read(IntPtr p)
        {
            var f = new WaveFormat();
            f.Tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = (ushort)Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            f.BlockAlign = (ushort)Marshal.ReadInt16(p, 12);
            f.Bits = (ushort)Marshal.ReadInt16(p, 14);
            int cb = (ushort)Marshal.ReadInt16(p, 16);
            f.ValidBits = f.Bits;
            if (f.Tag == 0xFFFE && cb >= 22)
            {
                f.ValidBits = (ushort)Marshal.ReadInt16(p, 18);
                f.Mask = Marshal.ReadInt32(p, 20);
                var g = new byte[16];
                Marshal.Copy(new IntPtr(p.ToInt64() + 24), g, 0, 16);
                f.Sub = new Guid(g);
            }
            return f;
        }

        /// Allocates a WAVEFORMATEXTENSIBLE (float32) with CoTaskMemAlloc.
        public static IntPtr CreateFloat(int rate, int channels)
        {
            IntPtr p = Marshal.AllocCoTaskMem(40);
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
            Marshal.WriteInt16(p, 2, (short)channels);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * 4 * channels);
            Marshal.WriteInt16(p, 12, (short)(4 * channels));
            Marshal.WriteInt16(p, 14, 32);
            Marshal.WriteInt16(p, 16, 22);
            Marshal.WriteInt16(p, 18, 32);
            Marshal.WriteInt32(p, 20, channels == 2 ? 3 : 4);
            Marshal.Copy(SubFloat.ToByteArray(), 0, new IntPtr(p.ToInt64() + 24), 16);
            return p;
        }

        public override string ToString()
        {
            return (SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz " +
                   Bits + "-bit " + (IsFloat ? "float " : "") + Channels + "ch";
        }
    }

    /// Captures everything played to the source (virtual) device via WASAPI loopback, processes it,
    /// and renders it to the output device. Clock drift between the two devices is absorbed by a
    /// FIFO with a gently steered cubic resampler. Goes idle (render stream stopped) during silence.
    sealed class AudioEngine
    {
        const int CLSCTX_ALL = 23;
        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        const int AUDCLNT_BUFFERFLAGS_SILENT = 2;
        static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        static Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

        Thread thread;
        readonly ManualResetEvent stopEvt = new ManualResetEvent(false);
        volatile bool stopFlag;
        string srcId, outId;

        public volatile DspSnapshot Snapshot;
        public readonly Processor Dsp = new Processor();
        public volatile bool MuteOutput;   // self-test: run everything but write silence

        // Status (read by UI)
        public volatile string Status = "Stopped";
        public volatile bool Connected;
        public volatile bool Idle = true;
        public volatile int SampleRate;
        public volatile int LatencyMs;
        public volatile string CaptureFormat = "";
        public volatile string RenderFormat = "";
        public volatile int Underruns;
        public volatile int SessionCount;
        public int PacketsCaptured;

        public string SourceId { get { return srcId; } }
        public string OutputId { get { return outId; } }
        public bool Running { get { return thread != null; } }

        public void Start(string source, string output)
        {
            if (thread != null && source == srcId && output == outId) return;
            Stop();
            srcId = source; outId = output;
            stopFlag = false;
            stopEvt.Reset();
            thread = new Thread(ThreadMain);
            thread.Name = "Aurelia audio";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
        }

        public void Stop()
        {
            if (thread == null) return;
            stopFlag = true;
            stopEvt.Set();
            if (!thread.Join(3000)) { try { thread.Abort(); } catch { } }
            thread = null;
            Connected = false;
            Idle = true;
            Status = "Stopped";
        }

        void ThreadMain()
        {
            int taskIndex = 0;
            IntPtr mm = IntPtr.Zero;
            try { mm = Win32.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex); } catch { }
            try
            {
                while (!stopFlag)
                {
                    try { RunSession(); }
                    catch (EngineException ex) { Status = ex.Message; }
                    catch (Exception ex) { Status = "Audio error: " + ex.Message; }
                    Connected = false;
                    Idle = true;
                    if (stopFlag) break;
                    stopEvt.WaitOne(1500); // retry (device unplugged, format changed, ...)
                }
            }
            finally
            {
                if (mm != IntPtr.Zero) { try { Win32.AvRevertMmThreadCharacteristics(mm); } catch { } }
            }
        }

        static void Check(int hr, string what)
        {
            if (hr < 0)
            {
                string m;
                switch ((uint)hr)
                {
                    case 0x88890004: m = "device was removed or changed"; break;
                    case 0x88890008: m = "format not supported"; break;
                    case 0x8889000A: m = "device is in use (exclusive mode)"; break;
                    case 0x80070490: m = "device not found"; break;
                    default: m = "0x" + hr.ToString("X8"); break;
                }
                throw new EngineException(what + ": " + m);
            }
        }

        // ---- capture side buffers / FIFO ----
        float[] capL = new float[0], capR = new float[0];
        float[] fifo = new float[65536 * 2];
        const int FifoMask = 65535;
        long wPos, rPos;
        double frac, corr, avgFill;
        bool primed;
        int primeFrames, targetFrames;
        float[] chWL = new float[0], chWR = new float[0];

        void ResetFifo() { wPos = rPos = 0; frac = 0; corr = 0; primed = false; avgFill = targetFrames; }

        void FifoWrite(float[] L, float[] R, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int k = (int)((wPos + i) & FifoMask) * 2;
                fifo[k] = L[i]; fifo[k + 1] = R[i];
            }
            wPos += n;
            if (wPos - rPos > 60000) rPos = wPos - targetFrames; // way overfull: render side stalled
        }

        void BuildDownmix(WaveFormat f)
        {
            int ch = f.Channels;
            chWL = new float[ch]; chWR = new float[ch];
            if (ch == 1) { chWL[0] = 1; chWR[0] = 1; return; }
            int mask = f.Mask;
            int bitsSet = 0;
            for (int b = 0; b < 32; b++) if ((mask & (1 << b)) != 0) bitsSet++;
            if (mask == 0 || bitsSet < ch)
            {
                chWL[0] = 1; chWR[1] = 1;
                for (int c = 2; c < ch; c++) { chWL[c] = 0.5f; chWR[c] = 0.5f; }
                return;
            }
            const float h = 0.7071f;
            int ci = 0;
            for (int b = 0; b < 32 && ci < ch; b++)
            {
                if ((mask & (1 << b)) == 0) continue;
                float wl = 0, wr = 0;
                switch (1 << b)
                {
                    case 0x1: wl = 1; break;              // FL
                    case 0x2: wr = 1; break;              // FR
                    case 0x4: wl = h; wr = h; break;      // FC
                    case 0x8: wl = 0.5f; wr = 0.5f; break;// LFE
                    case 0x10: wl = h; break;             // BL
                    case 0x20: wr = h; break;             // BR
                    case 0x40: wl = 0.85f; wr = 0.3f; break; // FLC
                    case 0x80: wl = 0.3f; wr = 0.85f; break; // FRC
                    case 0x100: wl = 0.5f; wr = 0.5f; break; // BC
                    case 0x200: wl = h; break;            // SL
                    case 0x400: wr = h; break;            // SR
                    default: wl = 0.35f; wr = 0.35f; break;
                }
                chWL[ci] = wl; chWR[ci] = wr;
                ci++;
            }
        }

        unsafe void Convert(IntPtr data, int frames, WaveFormat f)
        {
            int ch = f.Channels;
            byte* basePtr = (byte*)data;
            if (f.IsFloat && ch == 2)
            {
                float* p = (float*)basePtr;
                for (int i = 0; i < frames; i++) { capL[i] = p[2 * i]; capR[i] = p[2 * i + 1]; }
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                byte* fr = basePtr + (long)i * f.BlockAlign;
                float l = 0, r = 0;
                for (int c = 0; c < ch; c++)
                {
                    float s;
                    if (f.IsFloat) s = ((float*)fr)[c];
                    else if (f.Bits == 16) s = ((short*)fr)[c] / 32768f;
                    else if (f.Bits == 32) s = ((int*)fr)[c] / 2147483648f;
                    else
                    {
                        byte* q = fr + c * 3;
                        int v = (q[0] << 8) | (q[1] << 16) | (q[2] << 24);
                        s = v / 2147483648f;
                    }
                    l += s * chWL[c]; r += s * chWR[c];
                }
                capL[i] = l; capR[i] = r;
            }
        }

        /// Pull 'frames' output frames from the FIFO through the drift-correcting resampler.
        unsafe void FillRender(IntPtr pData, int frames, int outCh, double baseRatio)
        {
            float* o = (float*)pData;
            long avail = wPos - rPos;
            if (!primed)
            {
                if (avail >= primeFrames) { primed = true; frac = 0; avgFill = avail; }
                else { for (int i = 0; i < frames * outCh; i++) o[i] = 0; return; }
            }
            double ratio = baseRatio * (1 + corr);
            int done = 0;
            float[] fb = fifo;
            for (; done < frames; done++)
            {
                if (wPos - rPos < 4) { Underruns++; primed = false; break; }
                int k0 = (int)(rPos & FifoMask) * 2, k1 = (int)((rPos + 1) & FifoMask) * 2,
                    k2 = (int)((rPos + 2) & FifoMask) * 2, k3 = (int)((rPos + 3) & FifoMask) * 2;
                float t = (float)frac;
                float l = Hermite(fb[k0], fb[k1], fb[k2], fb[k3], t);
                float r = Hermite(fb[k0 + 1], fb[k1 + 1], fb[k2 + 1], fb[k3 + 1], t);
                float* d = o + done * outCh;
                if (outCh == 1) d[0] = (l + r) * 0.5f;
                else
                {
                    d[0] = l; d[1] = r;
                    for (int c = 2; c < outCh; c++) d[c] = 0;
                }
                frac += ratio;
                int adv = (int)frac;
                frac -= adv;
                rPos += adv;
            }
            for (int i = done * outCh; i < frames * outCh; i++) o[i] = 0;

            // Steer the resampling ratio so the FIFO level hovers around the target (absorbs clock drift).
            long fill = wPos - rPos;
            avgFill += 0.02 * (fill - avgFill);
            double err = (avgFill - targetFrames) / targetFrames;
            corr = Math.Max(-0.004, Math.Min(0.004, err * 0.003));
        }

        static float Hermite(float y0, float y1, float y2, float y3, float t)
        {
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * t + c2) * t + c1) * t + y1;
        }

        void RunSession()
        {
            IMMDeviceEnumerator en = null;
            IMMDevice srcDev = null, outDev = null;
            IAudioClient cap = null, ren = null;
            IAudioCaptureClient capC = null;
            IAudioRenderClient renC = null;
            IntPtr capFmt = IntPtr.Zero, renFmt = IntPtr.Zero;
            var renderEvent = new AutoResetEvent(false);
            var captureEvent = new AutoResetEvent(false);
            bool renderRunning = false;
            Status = "Connecting...";
            try
            {
                if (string.IsNullOrEmpty(srcId) || string.IsNullOrEmpty(outId)) throw new EngineException("No devices selected");
                en = Devices.CreateEnumerator();
                Check(en.GetDevice(srcId, out srcDev), "Capture device");
                Check(en.GetDevice(outId, out outDev), "Output device");
                object o;

                // ---- loopback capture on the source device ----
                Check(srcDev.Activate(ref IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out o), "Capture device");
                cap = (IAudioClient)o;
                Check(cap.GetMixFormat(out capFmt), "Capture format");
                var cf = WaveFormat.Read(capFmt);
                if (!cf.Supported) throw new EngineException("Unsupported capture format (" + cf + ")");
                bool capEvents = cap.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 2000000, 0, capFmt, IntPtr.Zero) >= 0;
                if (capEvents) Check(cap.SetEventHandle(captureEvent.SafeWaitHandle.DangerousGetHandle()), "Capture events");
                else
                {
                    Devices.Release(cap);
                    Check(srcDev.Activate(ref IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out o), "Capture device");
                    cap = (IAudioClient)o;
                    Check(cap.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, capFmt, IntPtr.Zero), "Loopback capture");
                }
                Check(cap.GetService(ref IID_IAudioCaptureClient, out o), "Capture service");
                capC = (IAudioCaptureClient)o;
                BuildDownmix(cf);

                // ---- render on the output device (let Windows convert rate/channels if needed) ----
                Check(outDev.Activate(ref IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out o), "Output device");
                ren = (IAudioClient)o;
                renFmt = WaveFormat.CreateFloat(cf.SampleRate, 2);
                int outCh = 2;
                double baseRatio = 1;
                int hr = ren.Initialize(0, AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                        200000, 0, renFmt, IntPtr.Zero);
                if (hr >= 0) RenderFormat = (cf.SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz float 2ch";
                else
                {
                    Devices.Release(ren);
                    Check(outDev.Activate(ref IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out o), "Output device");
                    ren = (IAudioClient)o;
                    Marshal.FreeCoTaskMem(renFmt); renFmt = IntPtr.Zero;
                    Check(ren.GetMixFormat(out renFmt), "Output format");
                    var mf = WaveFormat.Read(renFmt);
                    if (!mf.IsFloat || mf.Bits != 32) throw new EngineException("Unsupported output format (" + mf + ")");
                    Check(ren.Initialize(0, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 200000, 0, renFmt, IntPtr.Zero), "Output stream");
                    outCh = mf.Channels;
                    baseRatio = cf.SampleRate / (double)mf.SampleRate;
                    RenderFormat = mf.ToString();
                }
                Check(ren.SetEventHandle(renderEvent.SafeWaitHandle.DangerousGetHandle()), "Output events");
                int bufFrames;
                Check(ren.GetBufferSize(out bufFrames), "Output buffer");
                Check(ren.GetService(ref IID_IAudioRenderClient, out o), "Output service");
                renC = (IAudioRenderClient)o;

                int fs = cf.SampleRate;
                primeFrames = fs * 22 / 1000;
                targetFrames = fs * 14 / 1000;
                ResetFifo();
                Dsp.Configure(fs);
                DspSnapshot applied = null;
                SampleRate = fs;
                CaptureFormat = cf.ToString();
                int outRate = (int)(fs / baseRatio);
                LatencyMs = (int)(bufFrames * 1000.0 / outRate + targetFrames * 1000.0 / fs + 10);

                Check(cap.Start(), "Start capture");
                Connected = true;
                Idle = true;
                SessionCount++;
                Status = "Ready";
                int lastSound = Environment.TickCount - 100000;
                bool capEventSeen = false;
                var waitRun = new WaitHandle[] { renderEvent, captureEvent, stopEvt };
                var waitIdle = new WaitHandle[] { captureEvent, stopEvt };

                while (!stopFlag)
                {
                    if (renderRunning) WaitHandle.WaitAny(waitRun, 100);
                    else
                    {
                        int w = WaitHandle.WaitAny(waitIdle, capEventSeen ? 250 : 12);
                        if (w == 0) capEventSeen = true;
                    }
                    if (stopFlag) break;

                    var snap = Snapshot;
                    if (snap != applied) { Dsp.Apply(snap); applied = snap; }

                    // ---- drain capture ----
                    while (true)
                    {
                        int pk;
                        Check(capC.GetNextPacketSize(out pk), "Capture");
                        if (pk == 0) break;
                        IntPtr data; int frames, flags; long p1, p2;
                        Check(capC.GetBuffer(out data, out frames, out flags, out p1, out p2), "Capture");
                        if (frames <= 0) { capC.ReleaseBuffer(0); continue; }
                        if (capL.Length < frames) { capL = new float[frames * 2]; capR = new float[frames * 2]; }
                        bool silentFlag = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        if (silentFlag) { Array.Clear(capL, 0, frames); Array.Clear(capR, 0, frames); }
                        else Convert(data, frames, cf);
                        capC.ReleaseBuffer(frames);
                        PacketsCaptured++;

                        bool sound = false;
                        if (!silentFlag)
                        {
                            for (int i = 0; i < frames; i++)
                            {
                                float a = capL[i], b = capR[i];
                                if (a > 1e-5f || a < -1e-5f || b > 1e-5f || b < -1e-5f) { sound = true; break; }
                            }
                        }
                        if (sound) lastSound = Environment.TickCount;

                        if (!renderRunning)
                        {
                            if (!sound) continue; // stay idle, drop digital silence
                            // wake up: pre-fill the device buffer with silence and start the render stream
                            ResetFifo();
                            IntPtr pz;
                            if (renC.GetBuffer(bufFrames, out pz) >= 0) renC.ReleaseBuffer(bufFrames, AUDCLNT_BUFFERFLAGS_SILENT);
                            Check(ren.Start(), "Start output");
                            renderRunning = true;
                            Idle = false;
                            Status = "Playing";
                        }
                        Dsp.Process(capL, capR, frames);
                        FifoWrite(capL, capR, frames);
                    }

                    if (!renderRunning) continue;

                    // ---- go idle after sustained silence (lets the PC sleep, frees the device) ----
                    if (Environment.TickCount - lastSound > 6000 && wPos - rPos < 8)
                    {
                        ren.Stop();
                        ren.Reset();
                        renderRunning = false;
                        Idle = true;
                        Status = "Idle";
                        continue;
                    }

                    // ---- render ----
                    int pad;
                    Check(ren.GetCurrentPadding(out pad), "Output");
                    int availFrames = bufFrames - pad;
                    if (availFrames > 0)
                    {
                        IntPtr pData;
                        Check(renC.GetBuffer(availFrames, out pData), "Output");
                        FillRender(pData, availFrames, outCh, baseRatio);
                        renC.ReleaseBuffer(availFrames, MuteOutput ? AUDCLNT_BUFFERFLAGS_SILENT : 0);
                    }
                }
            }
            finally
            {
                try { if (cap != null) cap.Stop(); } catch { }
                try { if (ren != null && renderRunning) ren.Stop(); } catch { }
                Devices.Release(capC); Devices.Release(renC);
                Devices.Release(cap); Devices.Release(ren);
                Devices.Release(srcDev); Devices.Release(outDev);
                Devices.Release(en);
                if (capFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(capFmt);
                if (renFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(renFmt);
                renderEvent.Close();
                captureEvent.Close();
                Connected = false;
            }
        }
    }
}
