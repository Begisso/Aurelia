using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Aurelia")]
[assembly: AssemblyProduct("Aurelia")]
[assembly: AssemblyDescription("Aurelia - portable system-wide 20-band parametric EQ")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Aurelia
{
    static class Program
    {
        const string MutexName = @"Local\Aurelia.SingleInstance.5b1d7e42";
        const string ShowEventName = @"Local\Aurelia.Show.5b1d7e42";

        [STAThread]
        static int Main(string[] args)
        {
            bool tray = false, selftest = false;
            string exportIcon = null, shotDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--tray" || a == "/tray" || a == "-tray" || a == "--minimized") tray = true;
                else if (a == "--selftest") selftest = true;
                else if (a == "--export-icon" && i + 1 < args.Length) exportIcon = args[++i];
                else if (a == "--screenshot" && i + 1 < args.Length) shotDir = args[++i];
            }

            if (exportIcon != null)
            {
                Theme.Init(1f);
                AppIcon.SaveIco(exportIcon);
                return 0;
            }

            try { Win32.SetProcessDPIAware(); } catch { }
            float scale = 1f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; } catch { }
            Theme.Init(scale);

            if (selftest) return SelfTest.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (shotDir != null) return Screenshot(shotDir);

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    if (!tray)
                    {
                        try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    }
                    return 0;
                }

                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                var cfg = Settings.Load();
                var ctl = new Controller(cfg);

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log(cfg, e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log(cfg, e.ExceptionObject as Exception);
                    try { ctl.Engine.Stop(); ctl.RestoreDefault(); } catch { }
                };

                var form = new MainForm(ctl);
                form.Start(tray);

                var listener = new Thread(() =>
                {
                    while (true)
                    {
                        showEvent.WaitOne();
                        try { form.BeginInvoke((MethodInvoker)form.ShowFromTray); }
                        catch { return; }
                    }
                });
                listener.IsBackground = true;
                listener.Start();

                Application.Run(new ApplicationContext());
                ctl.Shutdown();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// Dry-run render of the UI to PNGs (off-screen, muted, no routing changes, nothing saved).
        static int Screenshot(string dir)
        {
            // icon contact sheet on dark and light backgrounds
            int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
            using (var sheet = new Bitmap(700, 560))
            using (var g = Graphics.FromImage(sheet))
            {
                g.FillRectangle(new SolidBrush(Color.FromArgb(32, 32, 36)), 0, 0, 700, 280);
                g.FillRectangle(Brushes.White, 0, 280, 700, 280);
                for (int row = 0; row < 2; row++)
                {
                    int x = 10;
                    foreach (int s in sizes)
                    {
                        using (var ic = AppIcon.Render(s, row == 0 || s != 256)) g.DrawImageUnscaled(ic, x, row * 280 + 10);
                        x += Math.Max(s, 24) + 10;
                    }
                }
                sheet.Save(Path.Combine(dir, "aurelia-icon.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            var cfg = Settings.Load();
            var ctl = new Controller(cfg);
            ctl.DryRun = true;
            var form = new MainForm(ctl);
            form.ShowInTaskbar = false;
            form.NoActivate = true;
            form.HideTrayIcon();
            form.Location = new Point(-7000, 40);
            form.Start(false);
            form.Location = new Point(-7000, 40);
            File.WriteAllText(Path.Combine(dir, "aurelia-caption.txt"), "caption colour applied: " + Theme.DarkTitleBar(form.Handle));

            // synthetic programme material (pink noise + bass + tone) through a private processor, for the analyzer/meter
            var demo = new Processor();
            demo.Configure(48000);
            demo.Tap.Enabled = true;
            form.Eq.AltDsp = demo;
            bool run = true;
            var feeder = new Thread(() =>
            {
                var rnd = new Random(3);
                var L = new float[480]; var R = new float[480];
                double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
                long n = 0;
                DspSnapshot applied = null;
                while (run)
                {
                    var snap = ctl.Engine.Snapshot;
                    if (snap != applied) { demo.Apply(snap); applied = snap; }
                    for (int i = 0; i < 480; i++, n++)
                    {
                        double white = rnd.NextDouble() * 2 - 1;
                        b0 = 0.99886 * b0 + white * 0.0555179; b1 = 0.99332 * b1 + white * 0.0750759;
                        b2 = 0.96900 * b2 + white * 0.1538520; b3 = 0.86650 * b3 + white * 0.3104856;
                        b4 = 0.55000 * b4 + white * 0.5329522; b5 = -0.7616 * b5 - white * 0.0168980;
                        double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11;
                        b6 = white * 0.115926;
                        double tm = n / 48000.0;
                        double s = pink * 0.3 + 0.22 * Math.Sin(2 * Math.PI * 55 * tm) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 2 * tm)) + 0.04 * Math.Sin(2 * Math.PI * 880 * tm);
                        L[i] = (float)s; R[i] = (float)(s * 0.9 + pink * 0.05);
                    }
                    demo.Process(L, R, 480);
                    Thread.Sleep(10);
                }
            });
            feeder.IsBackground = true;
            feeder.Start();

            int step = 0;
            var t = new System.Windows.Forms.Timer();
            t.Interval = 2500;
            t.Tick += delegate
            {
                step++;
                if (step == 1) { Capture(form, Path.Combine(dir, "aurelia-1.png")); form.Demo(); }
                else { Capture(form, Path.Combine(dir, "aurelia-2.png")); t.Stop(); run = false; form.ExitApp(); }
            };
            t.Start();
            Application.Run(new ApplicationContext());
            return 0;
        }

        static void Capture(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        static void Log(Settings cfg, Exception ex)
        {
            if (ex == null) return;
            try
            {
                string path = Path.ChangeExtension(cfg.FilePath, ".log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
