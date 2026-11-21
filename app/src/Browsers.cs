using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ScreenCycle
{
    internal class BrowserLaunch
    {
        public string Exe;
        public string Args;
        public string ProcessName;   // used to recognise its windows
        public bool SelfFullscreen;  // the browser itself handles fullscreen (kiosk)
        public bool UserProfile;     // opens in the person's own browser profile (may join a browser that is already running)
    }

    internal class BrowserProfileInfo
    {
        public string Id;     // Chromium: profile folder name; Firefox: full profile path
        public string Label;
    }

    internal static class Browsers
    {
        private static readonly Dictionary<BrowserKind, string> Cache = new Dictionary<BrowserKind, string>();

        public static string Find(BrowserKind kind)
        {
            lock (Cache)
            {
                string cached;
                if (Cache.TryGetValue(kind, out cached)) return cached;
                string found = FindUncached(kind);
                Cache[kind] = found;
                return found;
            }
        }

        public static void ClearCache() { lock (Cache) Cache.Clear(); }

        private static string FindUncached(BrowserKind kind)
        {
            string exe;
            List<string> candidates = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            switch (kind)
            {
                case BrowserKind.Edge:
                    exe = "msedge.exe";
                    candidates.Add(Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"));
                    candidates.Add(Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"));
                    break;
                case BrowserKind.Chrome:
                    exe = "chrome.exe";
                    candidates.Add(Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"));
                    candidates.Add(Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"));
                    candidates.Add(Path.Combine(local, @"Google\Chrome\Application\chrome.exe"));
                    break;
                case BrowserKind.Firefox:
                    exe = "firefox.exe";
                    candidates.Add(Path.Combine(pf, @"Mozilla Firefox\firefox.exe"));
                    candidates.Add(Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"));
                    break;
                case BrowserKind.Brave:
                    exe = "brave.exe";
                    candidates.Add(Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"));
                    candidates.Add(Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"));
                    break;
                case BrowserKind.Auto:
                    foreach (BrowserKind k in new BrowserKind[] { BrowserKind.Edge, BrowserKind.Chrome, BrowserKind.Firefox, BrowserKind.Brave })
                    {
                        string p = Find(k);
                        if (p != null) return p;
                    }
                    return null;
                default:
                    return null;
            }
            string fromReg = AppPath(exe);
            if (fromReg != null) candidates.Insert(0, fromReg);
            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        private static string AppPath(string exe)
        {
            foreach (RegistryKey root in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (RegistryKey k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                    {
                        if (k == null) continue;
                        string v = k.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v))
                        {
                            v = v.Trim('"');
                            if (File.Exists(v)) return v;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public static BrowserKind KindOfExe(string exePath)
        {
            string n = Path.GetFileName(exePath ?? "").ToLowerInvariant();
            if (n == "msedge.exe") return BrowserKind.Edge;
            if (n == "chrome.exe") return BrowserKind.Chrome;
            if (n == "firefox.exe") return BrowserKind.Firefox;
            if (n == "brave.exe") return BrowserKind.Brave;
            return BrowserKind.Default;
        }

        public static string Describe(BrowserKind kind)
        {
            switch (kind)
            {
                case BrowserKind.Auto:
                    string p = Find(BrowserKind.Auto);
                    return p == null ? "Automatic (no supported browser found!)" : "Automatic (" + Pretty(KindOfExe(p)) + ")";
                case BrowserKind.Default: return "System default browser (no kiosk)";
                default:
                    return Pretty(kind) + (Find(kind) == null ? "  (not installed)" : "");
            }
        }

        public static string Pretty(BrowserKind k)
        {
            switch (k)
            {
                case BrowserKind.Edge: return "Microsoft Edge";
                case BrowserKind.Chrome: return "Google Chrome";
                case BrowserKind.Firefox: return "Mozilla Firefox";
                case BrowserKind.Brave: return "Brave";
                default: return k.ToString();
            }
        }

        public static string ProfileDir(RotationItem item)
        {
            return Path.Combine(AppConfig.DataDir, "profiles", item.Id);
        }

        private static string Q(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        public static string NormalizeUrl(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) return url;
            if (url.IndexOf("://", StringComparison.Ordinal) < 0 && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(url)) return new Uri(url).AbsoluteUri;
                url = "https://" + url;
            }
            return url;
        }

        /// <summary>Builds the launch command for a website item. Returns null when no browser is available (caller falls back to shell).</summary>
        public static BrowserLaunch Build(RotationItem item, Rectangle bounds)
        {
            if (item.Browser == BrowserKind.Default) return null;
            string exe = Find(item.Browser);
            if (exe == null && item.Browser != BrowserKind.Auto) exe = Find(BrowserKind.Auto);
            if (exe == null) return null;

            BrowserKind kind = KindOfExe(exe);
            string url = NormalizeUrl(item.Url);
            if (!string.IsNullOrEmpty(item.BrowserProfile) && ProfileExists(kind, item.BrowserProfile))
                return BuildForUserProfile(item, exe, kind, url, bounds);
            string profile = ProfileDir(item);
            Directory.CreateDirectory(profile);
            bool full = item.Mode == WindowMode.Fullscreen;

            BrowserLaunch bl = new BrowserLaunch();
            bl.Exe = exe;
            bl.ProcessName = Path.GetFileNameWithoutExtension(exe);
            bl.SelfFullscreen = full;
            StringBuilder a = new StringBuilder();

            if (kind == BrowserKind.Firefox)
            {
                WriteFirefoxPrefs(profile);
                a.Append("-no-remote -profile ").Append(Q(profile)).Append(' ');
                if (full) a.Append("--kiosk ");
                a.Append("-new-window ").Append(Q(url));
            }
            else
            {
                // Chromium family (Edge, Chrome, Brave)
                a.Append("--user-data-dir=").Append(Q(profile)).Append(' ');
                a.Append("--no-first-run --no-default-browser-check --disable-session-crashed-bubble --hide-crash-restore-bubble ");
                a.Append("--disable-features=Translate,msEdgeSidebarV2,msUndersideButton,TranslateUI --disable-infobars ");
                a.Append("--autoplay-policy=no-user-gesture-required ");
                a.Append("--window-position=").Append(bounds.X).Append(',').Append(bounds.Y).Append(' ');
                if (kind == BrowserKind.Edge) a.Append("--edge-skip-compat-layer-relaunch --no-service-autorun ");
                if (full)
                {
                    a.Append("--kiosk ");
                    if (kind == BrowserKind.Edge) a.Append("--edge-kiosk-type=fullscreen ");
                    a.Append(Q(url));
                }
                else
                {
                    a.Append("--window-size=").Append(Math.Max(800, bounds.Width * 3 / 4)).Append(',').Append(Math.Max(600, bounds.Height * 3 / 4)).Append(' ');
                    a.Append("--app=").Append(Q(url));
                }
            }
            bl.Args = a.ToString();
            return bl;
        }

        /// <summary>
        /// Opens the site with one of the person's own browser profiles, so they are signed in (Google, YouTube...).
        /// The browser may already be running with that profile: then only a new window is added to it, and kiosk
        /// switches would be ignored. So it opens as a clean app window (Chromium) / new window (Firefox), and
        /// ScreenCycle makes it fullscreen afterwards with the browser's own F11.
        /// </summary>
        private static BrowserLaunch BuildForUserProfile(RotationItem item, string exe, BrowserKind kind, string url, Rectangle bounds)
        {
            BrowserLaunch bl = new BrowserLaunch();
            bl.Exe = exe;
            bl.ProcessName = Path.GetFileNameWithoutExtension(exe);
            bl.UserProfile = true;
            bl.SelfFullscreen = false;
            StringBuilder a = new StringBuilder();
            if (kind == BrowserKind.Firefox)
            {
                a.Append("-profile ").Append(Q(item.BrowserProfile)).Append(" -new-window ").Append(Q(url));
            }
            else
            {
                a.Append("--profile-directory=").Append(Q(item.BrowserProfile)).Append(' ');
                a.Append("--no-first-run --no-default-browser-check ");
                a.Append("--window-position=").Append(bounds.X + 40).Append(',').Append(bounds.Y + 40).Append(' ');
                a.Append("--window-size=").Append(Math.Max(800, bounds.Width * 3 / 4)).Append(',').Append(Math.Max(600, bounds.Height * 3 / 4)).Append(' ');
                a.Append("--new-window --app=").Append(Q(url));
            }
            bl.Args = a.ToString();
            return bl;
        }

        private static string ChromiumUserData(BrowserKind kind)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            switch (kind)
            {
                case BrowserKind.Chrome: return Path.Combine(local, @"Google\Chrome\User Data");
                case BrowserKind.Edge: return Path.Combine(local, @"Microsoft\Edge\User Data");
                case BrowserKind.Brave: return Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data");
                default: return null;
            }
        }

        /// <summary>The browser that a setting really means (Auto resolved to the installed one).</summary>
        public static BrowserKind Resolve(BrowserKind kind)
        {
            if (kind == BrowserKind.Default) return BrowserKind.Default;
            string exe = Find(kind);
            if (exe == null && kind != BrowserKind.Auto) exe = Find(BrowserKind.Auto);
            return exe == null ? BrowserKind.Default : KindOfExe(exe);
        }

        private static bool ProfileExists(BrowserKind kind, string id)
        {
            try
            {
                if (kind == BrowserKind.Firefox) return Directory.Exists(id);
                string ud = ChromiumUserData(kind);
                return ud != null && Directory.Exists(Path.Combine(ud, id));
            }
            catch { return false; }
        }

        /// <summary>The person's own profiles of this browser (empty if none / not supported).</summary>
        public static List<BrowserProfileInfo> ListProfiles(BrowserKind kind)
        {
            List<BrowserProfileInfo> list = new List<BrowserProfileInfo>();
            try
            {
                if (kind == BrowserKind.Firefox) ListFirefoxProfiles(list);
                else ListChromiumProfiles(kind, list);
            }
            catch (Exception ex) { Log.Warn("Could not list " + kind + " profiles: " + ex.Message); }
            return list;
        }

        private static void ListChromiumProfiles(BrowserKind kind, List<BrowserProfileInfo> list)
        {
            string ud = ChromiumUserData(kind);
            if (ud == null || !Directory.Exists(ud)) return;
            string state = Path.Combine(ud, "Local State");
            if (File.Exists(state))
            {
                // read a copy: the browser may have the file open
                string json;
                using (FileStream fs = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs)) json = r.ReadToEnd();
                System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = js.DeserializeObject(json) as Dictionary<string, object>;
                Dictionary<string, object> profile = Get(root, "profile");
                Dictionary<string, object> cache = Get(profile, "info_cache");
                if (cache != null)
                {
                    foreach (KeyValuePair<string, object> kv in cache)
                    {
                        if (!Directory.Exists(Path.Combine(ud, kv.Key))) continue;
                        Dictionary<string, object> p = kv.Value as Dictionary<string, object>;
                        string name = Str(p, "name"), user = Str(p, "user_name");
                        if (name.Length == 0) name = kv.Key;
                        list.Add(new BrowserProfileInfo { Id = kv.Key, Label = user.Length > 0 && user != name ? name + "  (" + user + ")" : name });
                    }
                }
            }
            if (list.Count == 0 && Directory.Exists(Path.Combine(ud, "Default")))
                list.Add(new BrowserProfileInfo { Id = "Default", Label = "Default" });
            list.Sort(delegate(BrowserProfileInfo x, BrowserProfileInfo y)
            {
                if (x.Id == "Default") return -1;
                if (y.Id == "Default") return 1;
                return string.Compare(x.Label, y.Label, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private static Dictionary<string, object> Get(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) && o != null ? o.ToString().Trim() : "";
        }

        private static void ListFirefoxProfiles(List<BrowserProfileInfo> list)
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox");
            string ini = Path.Combine(baseDir, "profiles.ini");
            if (!File.Exists(ini)) return;
            string section = null, name = "", path = "";
            bool relative = true;
            string defaultPath = null;
            List<string[]> found = new List<string[]>();
            Action flush = delegate
            {
                if (section != null && section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && path.Length > 0)
                {
                    string full = relative ? Path.Combine(baseDir, path.Replace('/', '\\')) : path;
                    if (Directory.Exists(full)) found.Add(new string[] { full, name.Length > 0 ? name : Path.GetFileName(full) });
                }
                else if (section != null && section.StartsWith("Install", StringComparison.OrdinalIgnoreCase) && path.Length > 0)
                    defaultPath = Path.Combine(baseDir, path.Replace('/', '\\'));
                name = ""; path = ""; relative = true;
            };
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { flush(); section = line.Substring(1, line.Length - 2); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (k == "Name") name = v;
                else if (k == "Path" || k == "Default" && section != null && section.StartsWith("Install")) path = v;
                else if (k == "IsRelative") relative = v != "0";
            }
            flush();
            foreach (string[] p in found)
            {
                bool isDefault = defaultPath != null && string.Equals(Path.GetFullPath(p[0]), Path.GetFullPath(defaultPath), StringComparison.OrdinalIgnoreCase);
                BrowserProfileInfo info = new BrowserProfileInfo { Id = p[0], Label = p[1] + (isDefault ? "  (the one Firefox normally opens)" : "") };
                if (isDefault) list.Insert(0, info); else list.Add(info);
            }
        }
        private static void WriteFirefoxPrefs(string profile)
        {
            try
            {
                string[] prefs = new string[]
                {
                    "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
                    "user_pref(\"browser.aboutwelcome.enabled\", false);",
                    "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);",
                    "user_pref(\"startup.homepage_welcome_url\", \"\");",
                    "user_pref(\"startup.homepage_welcome_url.additional\", \"\");",
                    "user_pref(\"startup.homepage_override_url\", \"\");",
                    "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
                    "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);",
                    "user_pref(\"datareporting.policy.firstRunURL\", \"\");",
                    "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);",
                    "user_pref(\"browser.sessionstore.resume_from_crash\", false);",
                    "user_pref(\"browser.sessionstore.max_resumed_crashes\", -1);",
                    "user_pref(\"browser.startup.page\", 0);",
                    "user_pref(\"browser.tabs.warnOnClose\", false);",
                    "user_pref(\"browser.warnOnQuit\", false);",
                    "user_pref(\"browser.disableResetPrompt\", true);",
                    "user_pref(\"browser.translations.automaticallyPopup\", false);",
                    "user_pref(\"browser.translations.panelShown\", true);",
                    "user_pref(\"media.autoplay.default\", 0);",
                    "user_pref(\"browser.newtabpage.activity-stream.feeds.section.highlights\", false);",
                    "user_pref(\"browser.uitour.enabled\", false);",
                    "user_pref(\"browser.messaging-system.whatsNewPanel.enabled\", false);",
                    "user_pref(\"app.update.silent\", true);",
                    "user_pref(\"browser.fullscreen.autohide\", true);",
                    "user_pref(\"full-screen-api.warning.timeout\", 0);",
                    "user_pref(\"browser.privatebrowsing.vpnpromourl\", \"\");",
                    "user_pref(\"extensions.pocket.enabled\", false);",
                    "user_pref(\"browser.vpn_promo.enabled\", false);",
                    "user_pref(\"sidebar.revamp\", false);",
                };
                File.WriteAllLines(Path.Combine(profile, "user.js"), prefs);
            }
            catch (Exception ex) { Log.Warn("Could not write Firefox prefs: " + ex.Message); }
        }
    }
}
