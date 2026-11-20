using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// A filming hotkey read through Windows, so it also works with OBS focused (Unity only sees its own window).
    /// Ignored in other windows and with Alt/Ctrl held.
    /// </summary>
    internal sealed class FilmKey
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        private static int ownProcess;

        private readonly string owner, setting, subject;
        private string keyText;
        private KeyCode key = KeyCode.None;
        private int virtualKey, ignoredLogs;
        private bool keyDown, keyBroken;

        /// <param name="owner">who the log lines are from ("Spectator view")</param>
        /// <param name="setting">the setting that names the key ("[Spectator] Key")</param>
        /// <param name="subject">what has no key when the name is wrong ("the view")</param>
        public FilmKey(string owner, string setting, string subject)
        {
            this.owner = owner;
            this.setting = setting;
            this.subject = subject;
        }

        public KeyCode Key => key;

        public string Describe() => key == KeyCode.None ? "no key"
            : $"key {key}, {(virtualKey != 0 && !keyBroken ? "in the game's window or OBS's" : "in the game's window only")}";

        /// <summary>True once per press of the key named by <paramref name="text"/>.</summary>
        public bool Pressed(string text)
        {
            if (!ReferenceEquals(text, keyText))
            {
                keyText = text;
                key = ParseKey(text);
                virtualKey = VirtualKey(key);
                keyDown = true; // a key held as it is set does not count
            }
            if (key == KeyCode.None) return false;
            bool down;
            if (virtualKey != 0 && !keyBroken)
            {
                try { down = (GetAsyncKeyState(virtualKey) & 0x8000) != 0; }
                catch (Exception e) { keyBroken = true; Plugin.L.LogWarning($"{owner}: the key is read with the window focused only: {e.Message}"); return false; }
            }
            else down = UnityEngine.Input.GetKey(key);
            bool pressed = false;
            if (down && !keyDown)
            {
                if (Modified(out string held)) { if (ignoredLogs++ < 5) Plugin.L.LogInfo($"{owner}: {key} pressed with {held} held, another tool's shortcut: ignored."); }
                else if (KeyWindow(out string window)) pressed = true;
                else if (ignoredLogs++ < 5) Plugin.L.LogInfo($"{owner}: {key} pressed in {window}, not the game's or OBS's: ignored.");
            }
            keyDown = down;
            return pressed;
        }

        private bool Modified(out string held)
        {
            bool alt, ctrl;
            if (virtualKey != 0 && !keyBroken)
            {
                alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;  // VK_MENU
                ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
            }
            else
            {
                alt = UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);
                ctrl = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            }
            held = alt && ctrl ? "Ctrl+Alt" : alt ? "Alt" : "Ctrl";
            return alt || ctrl;
        }

        /// <summary>Unity key name. A single digit means the top-row digit key.</summary>
        private KeyCode ParseKey(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return KeyCode.None;
            if (t.Length == 1 && t[0] >= '0' && t[0] <= '9') return (KeyCode)((int)KeyCode.Alpha0 + (t[0] - '0'));
            if (char.IsLetter(t[0]) && Enum.TryParse(t, true, out KeyCode k) && Enum.IsDefined(typeof(KeyCode), k)) return k; // no numbers, no lists
            Plugin.L.LogWarning($"{owner}: no key called '{t}' ({setting}); {subject} has no key.");
            return KeyCode.None;
        }

        /// <summary>The game's window or OBS is in front.</summary>
        private bool KeyWindow(out string window)
        {
            window = "another window";
            if (virtualKey == 0 || keyBroken) { window = "the game's window"; return true; }
            try
            {
                if (ownProcess == 0) ownProcess = Process.GetCurrentProcess().Id;
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == (uint)ownProcess) { window = "the game's window"; return true; }
                if (pid == 0) return false;
                using var process = Process.GetProcessById((int)pid);
                string name = process.ProcessName;
                window = $"'{name}'";
                return name.Equals("obs64", StringComparison.OrdinalIgnoreCase) || name.Equals("obs32", StringComparison.OrdinalIgnoreCase)
                       || name.Equals("obs", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                window = "a window that could not be told (" + e.Message + ")";
                return false;
            }
        }

        /// <summary>Windows virtual key code, 0 if unmapped.</summary>
        private static int VirtualKey(KeyCode k)
        {
            int c = (int)k;
            if (k >= KeyCode.F1 && k <= KeyCode.F15) return 0x70 + (c - (int)KeyCode.F1);
            if (k >= KeyCode.A && k <= KeyCode.Z) return 'A' + (c - (int)KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return '0' + (c - (int)KeyCode.Alpha0);
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return 0x60 + (c - (int)KeyCode.Keypad0);
            switch (k)
            {
                case KeyCode.Pause: return 0x13;
                case KeyCode.ScrollLock: return 0x91;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                default: return 0;
            }
        }
    }
}
