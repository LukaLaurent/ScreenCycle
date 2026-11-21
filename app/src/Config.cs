using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace ScreenCycle
{
    public enum ItemKind { Website, Application, Window }
    public enum WindowMode { Fullscreen, Maximized, AsIs }
    public enum BrowserKind { Auto, Edge, Chrome, Firefox, Brave, Default }
    public enum TransitionKind { Crossfade, FadeThroughBlack, None }

    public class RotationItem
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public bool Enabled = true;
        public ItemKind Kind = ItemKind.Website;
        public int DurationSeconds = 30;
        public WindowMode Mode = WindowMode.Fullscreen;
        public bool RefreshOnShow = false;
        /// <summary>For apps that draw their own title bar: 0 = hide it automatically, -1 = keep it, &gt;0 = pixels to cut off the top.</summary>
        public int TitleBarCrop = 0;

        // Website
        public string Url = "";
        public BrowserKind Browser = BrowserKind.Auto;
        /// <summary>Empty = a separate profile kept by ScreenCycle for this screen. Otherwise one of the browser's own profiles
        /// (Chromium: profile folder name such as "Default"; Firefox: profile folder path), so the person stays signed in.</summary>
        public string BrowserProfile = "";

        // Application
        public string Path = "";
        public string Arguments = "";

        // Application (optional filter) + Window
        public string TitleContains = "";
        public string ProcessName = "";

        public RotationItem Clone()
        {
            RotationItem c = (RotationItem)MemberwiseClone();
            return c;
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
                switch (Kind)
                {
                    case ItemKind.Website: return string.IsNullOrWhiteSpace(Url) ? "Website" : Url;
                    case ItemKind.Application: return string.IsNullOrWhiteSpace(Path) ? "Application" : System.IO.Path.GetFileNameWithoutExtension(Path);
                    default:
                        if (!string.IsNullOrWhiteSpace(TitleContains)) return TitleContains;
                        if (!string.IsNullOrWhiteSpace(ProcessName)) return ProcessName;
                        return "Window";
                }
            }
        }

        public string TargetDescription
        {
            get
            {
                switch (Kind)
                {
                    case ItemKind.Website: return Url;
                    case ItemKind.Application: return string.IsNullOrEmpty(Arguments) ? Path : Path + " " + Arguments;
                    default:
                        List<string> p = new List<string>();
                        if (!string.IsNullOrWhiteSpace(TitleContains)) p.Add("title “" + TitleContains + "”");
                        if (!string.IsNullOrWhiteSpace(ProcessName)) p.Add("process " + ProcessName);
                        return string.Join(", ", p.ToArray());
                }
            }
        }
    }

    public class HotkeySpec
    {
        public Keys Key = Keys.None;
        public bool Ctrl, Alt, Shift, Win;

        public HotkeySpec() { }
        public HotkeySpec(Keys key, bool ctrl, bool alt, bool shift)
        {
            Key = key; Ctrl = ctrl; Alt = alt; Shift = shift;
        }

        [XmlIgnore]
        public bool IsEmpty { get { return Key == Keys.None; } }

        public HotkeySpec Clone() { return (HotkeySpec)MemberwiseClone(); }

        public override string ToString()
        {
            if (IsEmpty) return "None";
            StringBuilder sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl + ");
            if (Alt) sb.Append("Alt + ");
            if (Shift) sb.Append("Shift + ");
            if (Win) sb.Append("Win + ");
            sb.Append(KeyName(Key));
            return sb.ToString();
        }

        public static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.Next: return "Page Down";
                case Keys.Prior: return "Page Up";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "+";
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Pause: return "Pause";
            }
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            return k.ToString();
        }
    }

    public class HotkeySet
    {
        public HotkeySpec PauseResume = new HotkeySpec(Keys.P, true, true, false);
        public HotkeySpec Next = new HotkeySpec(Keys.Right, true, true, false);
        public HotkeySpec Previous = new HotkeySpec(Keys.Left, true, true, false);
        public HotkeySpec StartStop = new HotkeySpec();
        public HotkeySpec Settings = new HotkeySpec(Keys.S, true, true, false);
        public HotkeySpec Exit = new HotkeySpec(Keys.Q, true, true, false);

        public HotkeySet Clone()
        {
            HotkeySet c = new HotkeySet();
            c.PauseResume = PauseResume.Clone();
            c.Next = Next.Clone();
            c.Previous = Previous.Clone();
            c.StartStop = StartStop.Clone();
            c.Settings = Settings.Clone();
            c.Exit = Exit.Clone();
            return c;
        }
    }

    [XmlRoot("ScreenCycle")]
    public class AppConfig
    {
        public int Version = 1;
        public List<RotationItem> Items = new List<RotationItem>();

        public string MonitorDevice = "";          // empty = primary
        public TransitionKind Transition = TransitionKind.Crossfade;
        public int TransitionMs = 600;
        public int DefaultDurationSeconds = 30;

        public bool StartRotationOnLaunch = true;
        public bool StartWithWindows = false;
        public int StartupDelaySeconds = 10;

        public bool KeepScreenAwake = true;
        public bool PauseOnUserActivity = false;
        public int ResumeAfterIdleSeconds = 60;
        public bool ShowProgressBar = false;
        public bool ParkMouseCursor = false;
        public bool AutoHideTaskbar = false;
        public bool CloseLaunchedAppsOnExit = false;
        public bool ShowNotifications = false;
        public bool RelaunchClosedItems = true;

        public HotkeySet Hotkeys = new HotkeySet();

        public AppConfig Clone()
        {
            AppConfig c = (AppConfig)MemberwiseClone();
            c.Items = new List<RotationItem>();
            foreach (RotationItem i in Items) c.Items.Add(i.Clone());
            c.Hotkeys = Hotkeys.Clone();
            return c;
        }

        // ----- persistence -----

        public static string DataDir
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenCycle");
            }
        }

        private static string PortablePath
        {
            get { return System.IO.Path.Combine(Program.AppDir, "ScreenCycle.config.xml"); }
        }

        /// <summary>A config file next to the exe wins (portable mode); otherwise %LocalAppData%\ScreenCycle.</summary>
        public static string ConfigPath
        {
            get
            {
                if (File.Exists(PortablePath)) return PortablePath;
                return System.IO.Path.Combine(DataDir, "config.xml");
            }
        }

        public static AppConfig Load()
        {
            string path = ConfigPath;
            try
            {
                if (File.Exists(path))
                {
                    XmlSerializer xs = new XmlSerializer(typeof(AppConfig));
                    using (FileStream fs = File.OpenRead(path))
                    {
                        AppConfig c = (AppConfig)xs.Deserialize(fs);
                        c.Normalize();
                        return c;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read config " + path + ": " + ex.Message);
                try
                {
                    string bak = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(path, bak, true);
                    Log.Info("Broken config backed up to " + bak);
                }
                catch { }
            }
            AppConfig d = new AppConfig();
            d.Normalize();
            return d;
        }

        public void Save()
        {
            string path = ConfigPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            XmlSerializer xs = new XmlSerializer(typeof(AppConfig));
            XmlWriterSettings ws = new XmlWriterSettings();
            ws.Indent = true;
            ws.Encoding = new UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(tmp, ws))
            {
                xs.Serialize(w, this);
            }
            // atomic-ish replace so a crash mid-write never leaves a half file
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { File.Delete(path); }
            }
            File.Move(tmp, path);
        }

        public void SaveTo(string path)
        {
            XmlSerializer xs = new XmlSerializer(typeof(AppConfig));
            XmlWriterSettings ws = new XmlWriterSettings();
            ws.Indent = true;
            ws.Encoding = new UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(path, ws)) xs.Serialize(w, this);
        }

        public static AppConfig LoadFrom(string path)
        {
            XmlSerializer xs = new XmlSerializer(typeof(AppConfig));
            using (FileStream fs = File.OpenRead(path))
            {
                AppConfig c = (AppConfig)xs.Deserialize(fs);
                c.Normalize();
                return c;
            }
        }

        public void Normalize()
        {
            if (Items == null) Items = new List<RotationItem>();
            if (Hotkeys == null) Hotkeys = new HotkeySet();
            HotkeySet def = new HotkeySet();
            if (Hotkeys.PauseResume == null) Hotkeys.PauseResume = def.PauseResume;
            if (Hotkeys.Next == null) Hotkeys.Next = def.Next;
            if (Hotkeys.Previous == null) Hotkeys.Previous = def.Previous;
            if (Hotkeys.StartStop == null) Hotkeys.StartStop = def.StartStop;
            if (Hotkeys.Settings == null) Hotkeys.Settings = def.Settings;
            if (Hotkeys.Exit == null) Hotkeys.Exit = def.Exit;
            HashSet<string> ids = new HashSet<string>();
            foreach (RotationItem i in Items)
            {
                if (string.IsNullOrEmpty(i.Id) || ids.Contains(i.Id)) i.Id = Guid.NewGuid().ToString("N");
                ids.Add(i.Id);
                if (i.DurationSeconds < 1) i.DurationSeconds = 1;
                if (i.Name == null) i.Name = "";
                if (i.Url == null) i.Url = "";
                if (i.Path == null) i.Path = "";
                if (i.Arguments == null) i.Arguments = "";
                if (i.TitleContains == null) i.TitleContains = "";
                if (i.ProcessName == null) i.ProcessName = "";
            }
            if (TransitionMs < 0) TransitionMs = 0;
            if (TransitionMs > 5000) TransitionMs = 5000;
            if (ResumeAfterIdleSeconds < 1) ResumeAfterIdleSeconds = 1;
            if (StartupDelaySeconds < 0) StartupDelaySeconds = 0;
            if (MonitorDevice == null) MonitorDevice = "";
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string LogPath
        {
            get { return System.IO.Path.Combine(AppConfig.DataDir, "ScreenCycle.log"); }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppConfig.DataDir);
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
