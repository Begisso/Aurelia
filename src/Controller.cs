using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aurelia
{
    /// Owns settings, the audio engine and device routing. All public members are used from the UI thread.
    sealed class Controller : IMMNotificationClient
    {
        public readonly Settings Cfg;
        public readonly AudioEngine Engine = new AudioEngine();
        public EqState State;
        public bool Power;

        public string SourceId, OutputId;           // resolved routing
        public string SourceName = "", OutputName = "";
        public List<AudioDevice> Devices_ = new List<AudioDevice>();
        public string Problem = "";                 // routing problem shown in the banner
        public bool DryRun;                         // preview mode: no default-device changes, muted output, nothing saved

        public event Action Changed;                // routing / status changed
        public event Action PresetsChanged;
        public event Action StateReplaced;          // whole EQ state swapped (preset load / reset)

        Control ui;
        Timer debounce, saveTimer, pollTimer;
        IMMDeviceEnumerator notifyEnum;
        bool shutDown, tookDefault;
        string lastEngineStatus = "";

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Aurelia";

        public Controller(Settings cfg)
        {
            Cfg = cfg;
            State = cfg.Current;
            Power = cfg.Power;
        }

        public void Initialize(Control uiControl)
        {
            ui = uiControl;
            if (Cfg.IsFirstRun) { SetAutoStart(true); SaveNow(); } // start with Windows by default

            debounce = new Timer(); debounce.Interval = 400;
            debounce.Tick += delegate { debounce.Stop(); ApplyRouting(); };
            saveTimer = new Timer(); saveTimer.Interval = 1200;
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow(); };
            pollTimer = new Timer(); pollTimer.Interval = 1000;
            pollTimer.Tick += delegate { Poll(); };
            pollTimer.Start();

            PushDsp();
            try
            {
                notifyEnum = Devices.CreateEnumerator();
                notifyEnum.RegisterEndpointNotificationCallback(this);
            }
            catch { notifyEnum = null; }
            ApplyRouting();
            if (AutoStartEnabled) SetAutoStart(true); // refresh path in case the exe was moved
        }

        // ---------------- DSP / persistence ----------------

        public void PushDsp()
        {
            Engine.Snapshot = new DspSnapshot(State.Clone(), Power);
        }

        public void StateEdited()
        {
            PushDsp();
            MarkDirty();
        }

        public void MarkDirty()
        {
            if (saveTimer == null) return;
            saveTimer.Stop();
            saveTimer.Start();
        }

        public void SaveNow()
        {
            if (DryRun) return;
            Cfg.Current = State;
            Cfg.Power = Power;
            Cfg.Save();
        }

        public void SetPower(bool on)
        {
            Power = on;
            PushDsp();
            MarkDirty();
            Fire();
        }

        public bool PresetModified
        {
            get
            {
                EqState p;
                if (string.IsNullOrEmpty(Cfg.PresetName) || !Cfg.Presets.TryGetValue(Cfg.PresetName, out p)) return false;
                return !p.SameAs(State);
            }
        }

        public void LoadPreset(string name)
        {
            EqState p;
            if (!Cfg.Presets.TryGetValue(name, out p)) return;
            State = p.Clone();
            Cfg.PresetName = name;
            StateEdited();
            FireReplaced();
            FirePresets();
        }

        public void SavePreset(string name)
        {
            name = Settings.CleanName(name);
            if (name.Length == 0) return;
            Cfg.Presets[name] = State.Clone();
            Cfg.PresetName = name;
            SaveNow();
            FirePresets();
        }

        public void DeletePreset(string name)
        {
            if (!Cfg.Presets.Remove(name)) return;
            if (string.Equals(Cfg.PresetName, name, StringComparison.OrdinalIgnoreCase)) Cfg.PresetName = "";
            SaveNow();
            FirePresets();
        }

        public void RenamePreset(string oldName, string newName)
        {
            newName = Settings.CleanName(newName);
            EqState p;
            if (newName.Length == 0 || !Cfg.Presets.TryGetValue(oldName, out p)) return;
            Cfg.Presets.Remove(oldName);
            Cfg.Presets[newName] = p;
            if (string.Equals(Cfg.PresetName, oldName, StringComparison.OrdinalIgnoreCase)) Cfg.PresetName = newName;
            SaveNow();
            FirePresets();
        }

        public void ResetEq()
        {
            State = new EqState();
            Cfg.PresetName = "";
            StateEdited();
            FireReplaced();
            FirePresets();
        }

        // ---------------- autostart ----------------

        public static string ExePath { get { return Application.ExecutablePath; } }

        public bool AutoStartEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue(RunName) != null;
                }
                catch { return false; }
            }
        }

        public void SetAutoStart(bool on)
        {
            if (DryRun) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + ExePath + "\" --tray");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        // ---------------- routing ----------------

        static bool Same(string a, string b) { return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        AudioDevice Find(string id)
        {
            foreach (var d in Devices_) if (Same(d.Id, id)) return d;
            return null;
        }

        public void ApplyRouting()
        {
            if (shutDown) return;
            try { Devices_ = Devices.ListRender(); }
            catch { Devices_ = new List<AudioDevice>(); }

            // 1. source = the device Windows apps play into (normally a virtual device)
            AudioDevice src = Find(Cfg.SourceId);
            if (src == null)
            {
                AudioDevice best = null;
                foreach (var d in Devices_)
                    if (d.LooksVirtual && (best == null || Rank(d) > Rank(best))) best = d;
                src = best;
            }
            if (src == null)
            {
                Engine.Stop();
                SourceId = OutputId = null;
                SourceName = OutputName = "";
                Problem = "No virtual audio device found. Pick a spare output under  ⚙ › Capture from  (e.g. a monitor's HDMI audio you don't listen on), or install a virtual cable.";
                Fire();
                return;
            }

            // 2. output = where you actually listen
            AudioDevice outp = null;
            var pref = Find(Cfg.OutputId);
            if (pref != null && !Same(pref.Id, src.Id)) outp = pref;
            if (outp == null)
            {
                var def = Find(Devices.GetDefaultRenderId(ERole.Console));
                if (def != null && !Same(def.Id, src.Id) && !def.LooksVirtual) outp = def;
            }
            if (outp == null)
            {
                var orig = Find(Cfg.OriginalDefault);
                if (orig != null && !Same(orig.Id, src.Id)) outp = orig;
            }
            if (outp == null)
            {
                foreach (var d in Devices_)
                    if (!Same(d.Id, src.Id) && (outp == null || Devices.ListenScore(d) > Devices.ListenScore(outp))) outp = d;
            }
            if (outp == null)
            {
                Engine.Stop();
                SourceId = src.Id; SourceName = src.Name;
                OutputId = null; OutputName = "";
                Problem = "No output device available besides the capture device.";
                Fire();
                return;
            }
            if (string.IsNullOrEmpty(Cfg.OutputId)) { Cfg.OutputId = outp.Id; MarkDirty(); }

            SourceId = src.Id; SourceName = src.Name;
            OutputId = outp.Id; OutputName = outp.Name;
            Problem = "";

            // 3. make the source the Windows default so every app is captured
            Engine.MuteOutput = DryRun;
            string curDef = Devices.GetDefaultRenderId(ERole.Console);
            if (!DryRun && !Same(curDef, src.Id))
            {
                if (!string.IsNullOrEmpty(curDef)) Cfg.OriginalDefault = curDef;
                string comm = Devices.GetDefaultRenderId(ERole.Communications);
                Devices.SetDefault(src.Id, ERole.Console);
                Devices.SetDefault(src.Id, ERole.Multimedia);
                if (Same(comm, curDef)) { Devices.SetDefault(src.Id, ERole.Communications); Cfg.ChangedCommDefault = true; }
                MarkDirty();
            }
            tookDefault = !DryRun;

            Engine.Start(src.Id, outp.Id);
            Fire();
        }

        /// Preference among virtual capture devices (higher wins).
        public static int Rank(AudioDevice d)
        {
            string n = d.Name.ToLowerInvariant();
            if (n.Contains("dfx") || n.Contains("audio enhancer")) return 5;
            if (n.Contains("cable input") || n.Contains("vb-audio")) return 4;
            if (n.Contains("voicemeeter")) return 3;
            if (n.Contains("steam streaming speakers")) return 2;
            return 1;
        }

        public void SelectOutput(string id)
        {
            if (Same(id, SourceId)) return;
            Cfg.OutputId = id;
            MarkDirty();
            ApplyRouting();
        }

        public void SelectSource(string id)
        {
            string oldSource = SourceId;
            Cfg.SourceId = id;
            if (Same(Cfg.OutputId, id)) Cfg.OutputId = Same(oldSource, id) ? "" : (oldSource ?? "");
            MarkDirty();
            ApplyRouting();
        }

        /// Give the Windows default device back (on exit). Safe to call more than once.
        public void Shutdown()
        {
            if (shutDown) return;
            shutDown = true;
            try { if (notifyEnum != null) notifyEnum.UnregisterEndpointNotificationCallback(this); } catch { }
            if (pollTimer != null) pollTimer.Stop();
            Engine.Stop();
            RestoreDefault();
            try { SaveNow(); } catch { }
        }

        public void RestoreDefault()
        {
            if (!tookDefault || string.IsNullOrEmpty(SourceId)) return;
            string target = !string.IsNullOrEmpty(OutputId) ? OutputId : Cfg.OriginalDefault;
            if (string.IsNullOrEmpty(target)) return;
            if (Same(Devices.GetDefaultRenderId(ERole.Console), SourceId))
            {
                Devices.SetDefault(target, ERole.Console);
                Devices.SetDefault(target, ERole.Multimedia);
            }
            if (Same(Devices.GetDefaultRenderId(ERole.Communications), SourceId))
                Devices.SetDefault(target, ERole.Communications);
            tookDefault = false;
        }

        // ---------------- polling / notifications ----------------

        void Poll()
        {
            string st = Engine.Status ?? "";
            if (st != lastEngineStatus)
            {
                lastEngineStatus = st;
                Fire();
            }
        }

        void ScheduleRouting()
        {
            if (shutDown || ui == null || ui.IsDisposed) return;
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    if (shutDown) return;
                    debounce.Stop();
                    debounce.Start();
                });
            }
            catch { }
        }

        void Fire() { var h = Changed; if (h != null) h(); }
        void FirePresets() { var h = PresetsChanged; if (h != null) h(); }
        void FireReplaced() { var h = StateReplaced; if (h != null) h(); }

        // IMMNotificationClient (called on a COM worker thread)
        public void OnDeviceStateChanged(string deviceId, int newState) { ScheduleRouting(); }
        public void OnDeviceAdded(string deviceId) { ScheduleRouting(); }
        public void OnDeviceRemoved(string deviceId) { ScheduleRouting(); }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
        {
            if (flow != EDataFlow.Render || role != ERole.Console || string.IsNullOrEmpty(defaultDeviceId)) return;
            if (shutDown || ui == null || ui.IsDisposed) return;
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    if (shutDown || Same(defaultDeviceId, SourceId)) return;
                    // Someone picked a real device as default in Windows: treat it as the new listening
                    // device and route through it (the source becomes default again).
                    var d = Find(defaultDeviceId);
                    if (d == null || !d.LooksVirtual) Cfg.OutputId = defaultDeviceId;
                    MarkDirty();
                    debounce.Stop();
                    debounce.Start();
                });
            }
            catch { }
        }
    }
}
