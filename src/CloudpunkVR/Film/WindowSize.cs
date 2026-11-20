using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// The game window's size as Unity draws it (what saved frames get), the screen, and the client area with its DPI,
    /// so display scaling shows up. Logged at start and on change, with the -screen-* command line args.
    /// </summary>
    internal static partial class WindowSize
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCommandLineW();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string title);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out Rect32 rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect32 { public int Left, Top, Right, Bottom; }

        /// <summary>From the command line, 0 when not given.</summary>
        public static int AskedWidth { get; private set; }
        public static int AskedHeight { get; private set; }
        /// <summary>-screen-fullscreen 1 or 0; null when not given.</summary>
        public static bool? AskedFullScreen { get; private set; }

        private static bool argsRead;
        private static IntPtr window;
        private static float nextCheck;
        private static int lastW, lastH, notes;
        private static bool lastFull;

        /// <summary>Once a second, flat and VR (Diagnostics).</summary>
        public static void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            try
            {
                if (!argsRead) ReadArgs();
                int w = Screen.width, h = Screen.height;
                bool full = Screen.fullScreen;
                if (w != lastW || h != lastH || full != lastFull)
                {
                    lastW = w; lastH = h; lastFull = full;
                    if (notes++ < 20) Plugin.L.LogInfo("Window: " + Describe());
                }
                Keep(w, h, full);
            }
            catch (Exception e) { if (notes++ < 20) Plugin.L.LogWarning("Window: not read: " + e.Message); }
        }

        /// <summary>DEV builds only (WindowSize.Dev): keep the size the command line asked.</summary>
        static partial void Keep(int w, int h, bool full);

        private static void ReadArgs()
        {
            argsRead = true;
            // the process command line: the hosted runtime's args don't carry it
            string line = " " + Marshal.PtrToStringUni(GetCommandLineW()) + " ";
            AskedWidth = Number(line, "-screen-width");
            AskedHeight = Number(line, "-screen-height");
            int full = Number(line, "-screen-fullscreen", -1);
            AskedFullScreen = full < 0 ? null : full != 0;
            ReadKeep(line);
        }

        static partial void ReadKeep(string commandLine);

        private static int Number(string line, string arg, int none = 0)
        {
            var m = Regex.Match(line, @"\s" + Regex.Escape(arg) + @"\s+(\d+)\s", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : none;
        }

        /// <summary>This process's Unity window, found once.</summary>
        private static IntPtr Window()
        {
            if (window != IntPtr.Zero) return window;
            uint me = (uint)Environment.ProcessId;
            for (IntPtr w = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "UnityWndClass", null); w != IntPtr.Zero; w = FindWindowExW(IntPtr.Zero, w, "UnityWndClass", null))
            {
                GetWindowThreadProcessId(w, out uint pid);
                if (pid == me) return window = w;
            }
            return IntPtr.Zero;
        }

        /// <summary>Client area in pixels, DPI and the game's DPI awareness.</summary>
        private static string Client(out int cw, out int ch)
        {
            cw = ch = 0;
            var w = Window();
            if (w == IntPtr.Zero || !GetClientRect(w, out var r)) return "the window not found";
            cw = r.Right - r.Left;
            ch = r.Bottom - r.Top;
            string dpi = "";
            try
            {
                uint d = GetDpiForWindow(w);
                int aware = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(w));
                string how = aware switch { 0 => "not DPI aware: Windows scales it", 1 => "system DPI aware", 2 => "per-monitor DPI aware", _ => "DPI awareness unknown" };
                dpi = $" at {d} dpi ({d * 100 / 96}% scale), {how}";
            }
            catch (Exception) { dpi = " (DPI not readable on this Windows)"; }
            return $"the window's client area {cw}x{ch} px{dpi}";
        }

        /// <summary>The asked size as text, or null.</summary>
        private static string Asked()
        {
            if (AskedWidth <= 0 && AskedHeight <= 0 && AskedFullScreen == null) return null;
            return $"{(AskedWidth > 0 ? AskedWidth.ToString(CultureInfo.InvariantCulture) : "?")}x{(AskedHeight > 0 ? AskedHeight.ToString(CultureInfo.InvariantCulture) : "?")}" +
                   (AskedFullScreen == null ? "" : AskedFullScreen.Value ? " full screen" : " windowed");
        }

        /// <summary>Unity's size matches what the command line asked (true if it asked nothing).</summary>
        public static bool AsAsked()
        {
            if (AskedWidth > 0 && Screen.width != AskedWidth) return false;
            if (AskedHeight > 0 && Screen.height != AskedHeight) return false;
            return AskedFullScreen == null || Screen.fullScreen == AskedFullScreen.Value;
        }

        /// <summary>The full log line.</summary>
        public static string Describe()
        {
            if (!argsRead) ReadArgs();
            int w = Screen.width, h = Screen.height;
            bool full = Screen.fullScreen;
            string mode = "windowed";
            if (full)
            {
                mode = "full screen";
                try { mode += " (" + Screen.fullScreenMode + ")"; } catch (Exception) { }
            }
            var cur = Screen.currentResolution;
            string client = Client(out int cw, out int ch);
            string scaled = cw <= 0 || (cw == w && ch == h) ? "" : full ? ", Unity's picture scaled to it" : $", NOT Unity's {w}x{h}: Windows scales the picture (the frames stay {w}x{h})";
            string asked = Asked();
            return $"{w}x{h} {mode}, the screen {cur.width}x{cur.height} at {cur.refreshRate} Hz; {client}{scaled}; " +
                   (asked == null ? "the command line asked no size" : $"the command line asked {asked}: {(AsAsked() ? "as asked" : "NOT as asked")}") + KeepNote() + ".";
        }

        /// <summary>Short form for the take lines.</summary>
        public static string Short()
        {
            string client = Client(out _, out _);
            return $"Unity's {Screen.width}x{Screen.height}{(Screen.fullScreen ? " full screen" : " window")}, {client}";
        }

        private static string KeepNote()
        {
            string note = "";
            KeepDescribe(ref note);
            return note;
        }

        static partial void KeepDescribe(ref string note);
    }
}
