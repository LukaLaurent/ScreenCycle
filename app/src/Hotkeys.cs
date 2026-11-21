using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenCycle
{
    internal enum HotkeyAction { PauseResume = 1, Next, Previous, StartStop, Settings, Exit }

    /// <summary>Registers system-wide shortcuts on a hidden message window.</summary>
    internal sealed class HotkeyManager : NativeWindow, IDisposable
    {
        private readonly List<int> registered = new List<int>();
        public event Action<HotkeyAction> Pressed;

        public HotkeyManager()
        {
            CreateHandle(new CreateParams());
        }

        /// <summary>Registers all shortcuts; returns a description of the ones Windows refused (already used by another app).</summary>
        public List<string> Apply(HotkeySet set)
        {
            UnregisterAll();
            List<string> failed = new List<string>();
            TryRegister(HotkeyAction.PauseResume, set.PauseResume, "Pause / resume", failed);
            TryRegister(HotkeyAction.Next, set.Next, "Next screen", failed);
            TryRegister(HotkeyAction.Previous, set.Previous, "Previous screen", failed);
            TryRegister(HotkeyAction.StartStop, set.StartStop, "Start / stop", failed);
            TryRegister(HotkeyAction.Settings, set.Settings, "Open settings", failed);
            TryRegister(HotkeyAction.Exit, set.Exit, "Exit", failed);
            return failed;
        }

        private void TryRegister(HotkeyAction action, HotkeySpec spec, string label, List<string> failed)
        {
            if (spec == null || spec.IsEmpty) return;
            uint mods = Native.MOD_NOREPEAT;
            if (spec.Ctrl) mods |= Native.MOD_CONTROL;
            if (spec.Alt) mods |= Native.MOD_ALT;
            if (spec.Shift) mods |= Native.MOD_SHIFT;
            if (spec.Win) mods |= Native.MOD_WIN;
            int id = (int)action;
            if (Native.RegisterHotKey(Handle, id, mods, (uint)spec.Key)) registered.Add(id);
            else
            {
                failed.Add(label + " (" + spec + ")");
                Log.Warn("Shortcut " + spec + " for " + label + " is already in use by another program");
            }
        }

        public void Suspend() { UnregisterAll(); }

        private void UnregisterAll()
        {
            foreach (int id in registered) Native.UnregisterHotKey(Handle, id);
            registered.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                SyntheticInput.Mark(); // the key press itself must not count as "someone uses the screen"
                Action<HotkeyAction> h = Pressed;
                if (h != null)
                {
                    try { h((HotkeyAction)m.WParam.ToInt32()); }
                    catch (Exception ex) { Log.Error("Hotkey handler: " + ex); }
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ScreenCycle";

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) k.SetValue(ValueName, Program.LaunchCommand + " --autostart");
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Error("Could not update autostart: " + ex.Message); }
        }

        public static bool IsSet()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string v = k == null ? null : k.GetValue(ValueName) as string;
                    return v != null && v.IndexOf(Program.LaunchIdentity, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }
    }
}
