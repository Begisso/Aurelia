using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Aurelia
{
    /// Portable settings: one INI file next to the exe (falls back to %APPDATA%\Aurelia if that folder is read-only).
    sealed class Settings
    {
        public string FilePath;
        public bool IsFirstRun;

        public bool Power = true;
        public string SourceId = "";        // device apps play into (virtual device)
        public string OutputId = "";        // preferred device you listen on
        public string OriginalDefault = ""; // default device before Aurelia took over (crash recovery)
        public bool ChangedCommDefault;
        public string PresetName = "";
        public bool CloseToTray = true;
        public int AnalyzerMode = 3;        // 0 off, 1 pre, 2 post, 3 pre+post
        public int DisplayRange = 12;       // dB
        public int WinX = int.MinValue, WinY = int.MinValue, WinW = 0, WinH = 0;
        public bool TrayHintShown;

        public EqState Current = new EqState();
        public readonly SortedDictionary<string, EqState> Presets = new SortedDictionary<string, EqState>(StringComparer.OrdinalIgnoreCase);

        public static Settings Load()
        {
            var s = new Settings();
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string local = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + ".ini");
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aurelia", "Aurelia.ini");
            if (File.Exists(local)) s.FilePath = local;
            else if (File.Exists(roaming)) s.FilePath = roaming;
            else
            {
                s.FilePath = CanWrite(Path.GetDirectoryName(local)) ? local : roaming;
                s.IsFirstRun = true;
                return s;
            }
            try { s.Parse(File.ReadAllLines(s.FilePath, Encoding.UTF8)); }
            catch { }
            return s;
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".aurelia_probe_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        void Parse(string[] lines)
        {
            var sections = new List<KeyValuePair<string, Dictionary<string, string>>>();
            Dictionary<string, string> cur = null;
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections.Add(new KeyValuePair<string, Dictionary<string, string>>(line.Substring(1, line.Length - 2), cur));
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || cur == null) continue;
                cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            foreach (var sec in sections)
            {
                if (sec.Key == "Aurelia") ReadMain(sec.Value);
                else if (sec.Key == "Current") Current = EqState.ReadFrom(sec.Value);
                else if (sec.Key.StartsWith("Preset:"))
                {
                    string name = CleanName(sec.Key.Substring(7));
                    if (name.Length > 0) Presets[name] = EqState.ReadFrom(sec.Value);
                }
            }
        }

        void ReadMain(Dictionary<string, string> d)
        {
            Power = Str(d, "Power", "1") != "0";
            SourceId = Str(d, "SourceDevice", "");
            OutputId = Str(d, "OutputDevice", "");
            OriginalDefault = Str(d, "OriginalDefault", "");
            ChangedCommDefault = Str(d, "ChangedCommDefault", "0") == "1";
            PresetName = Str(d, "Preset", "");
            CloseToTray = Str(d, "CloseToTray", "1") != "0";
            TrayHintShown = Str(d, "TrayHintShown", "0") == "1";
            AnalyzerMode = (int)Num.Clamp(Num.P(Str(d, "Analyzer", "3"), 3), 0, 3);
            DisplayRange = (int)Num.P(Str(d, "Range", "12"), 12);
            if (DisplayRange != 3 && DisplayRange != 6 && DisplayRange != 12 && DisplayRange != 24 && DisplayRange != 30) DisplayRange = 12;
            WinX = (int)Num.P(Str(d, "WindowX", ""), int.MinValue);
            WinY = (int)Num.P(Str(d, "WindowY", ""), int.MinValue);
            WinW = (int)Num.P(Str(d, "WindowW", "0"), 0);
            WinH = (int)Num.P(Str(d, "WindowH", "0"), 0);
        }

        static string Str(Dictionary<string, string> d, string k, string def) { string v; return d.TryGetValue(k, out v) ? v : def; }

        public static string CleanName(string n)
        {
            if (n == null) return "";
            var sb = new StringBuilder();
            foreach (char c in n) if (c != '[' && c != ']' && c != '\r' && c != '\n' && c != '=') sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; Aurelia settings - portable. Delete this file to start fresh.");
            sb.AppendLine("[Aurelia]");
            Kv(sb, "Power", Power ? "1" : "0");
            Kv(sb, "SourceDevice", SourceId);
            Kv(sb, "OutputDevice", OutputId);
            Kv(sb, "OriginalDefault", OriginalDefault);
            Kv(sb, "ChangedCommDefault", ChangedCommDefault ? "1" : "0");
            Kv(sb, "Preset", PresetName);
            Kv(sb, "CloseToTray", CloseToTray ? "1" : "0");
            Kv(sb, "TrayHintShown", TrayHintShown ? "1" : "0");
            Kv(sb, "Analyzer", AnalyzerMode.ToString(CultureInfo.InvariantCulture));
            Kv(sb, "Range", DisplayRange.ToString(CultureInfo.InvariantCulture));
            if (WinX != int.MinValue)
            {
                Kv(sb, "WindowX", WinX.ToString(CultureInfo.InvariantCulture));
                Kv(sb, "WindowY", WinY.ToString(CultureInfo.InvariantCulture));
                Kv(sb, "WindowW", WinW.ToString(CultureInfo.InvariantCulture));
                Kv(sb, "WindowH", WinH.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
            WriteState(sb, "Current", Current);
            foreach (var p in Presets) WriteState(sb, "Preset:" + p.Key, p.Value);

            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
                IsFirstRun = false;
            }
            catch { }
        }

        static void WriteState(StringBuilder sb, string section, EqState st)
        {
            sb.AppendLine("[" + section + "]");
            var d = new Dictionary<string, string>();
            st.WriteTo(d);
            // stable order
            Kv(sb, "Clarity", d["Clarity"]);
            Kv(sb, "Loudness", d["Loudness"]);
            Kv(sb, "OutputGain", d["OutputGain"]);
            Kv(sb, "Bands", d["Bands"]);
            for (int i = 0; i < st.Bands.Count; i++) Kv(sb, "Band" + (i + 1), d["Band" + (i + 1)]);
            sb.AppendLine();
        }

        static void Kv(StringBuilder sb, string k, string v)
        {
            sb.Append(k).Append('=').Append((v ?? "").Replace("\r", "").Replace("\n", "")).AppendLine();
        }
    }
}
