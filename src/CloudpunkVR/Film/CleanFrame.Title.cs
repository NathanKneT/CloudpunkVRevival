using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// F2 on the main menu, for a title shot: hides the menu (list, prompts, cursor, in VR the panel and laser) and
    /// keeps the scene and the sign. Only a key press does it, never the setting at start, so you never boot into an
    /// invisible menu. The menu's input is held meanwhile (MenuInput). Lets go by itself if the menu goes away, the
    /// game loads or fades to black.
    /// </summary>
    internal static partial class CleanFrame
    {
        private static bool titleHidden, titleHolding, onForTitle, menuWasUp;
        private static int menuNotes;

        // what it changed: the UI camera's mask, and screen canvases outside that camera
        private static Camera titleUi;
        private static IntPtr titleUiPtr;
        private static int titleUiMask;
        private static bool titleMaskHeld;
        private static readonly List<Canvas> titleOverlays = new List<Canvas>();
        private static float nextOverlaySweep;

        /// <summary>Main menu hidden: the VR panel and laser stay off.</summary>
        public static bool HidesMenu => On && titleHidden && titleHolding;

        /// <summary>The main menu is up: its camera is on (the "press any key" title before it has none).</summary>
        public static bool MainMenuUp()
        {
            try
            {
                var g = Global.Instance;
                return g != null && g.menuCamera != null && g.menuCamera.gameObject.activeInHierarchy;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The key: on the main menu, hide or show the menu; anywhere else, the clean frame.</summary>
        private static void Toggle(string why)
        {
            if (titleHidden) ShowTitle(why);
            else if (MainMenuUp()) HideTitle(why);
            else Switch(!On, why);
        }

        private static void HideTitle(string why)
        {
            onForTitle = !On;
            if (!On) Switch(true, why);
            titleHidden = true;
            titleHolding = false;
            nextOverlaySweep = 0f; // sweep now
            Plugin.L.LogInfo($"Clean frame: the main menu goes ({why}){(onForTitle ? "; the clean frame goes off with it when it comes back" : "; the clean frame was on already and stays on after")}.");
        }

        private static void ShowTitle(string why) => LetGoOfTitle($"Clean frame: the main menu back ({why})", why);

        /// <summary>Menu and its input back; the clean frame off too if hiding the menu turned it on.</summary>
        private static void LetGoOfTitle(string line, string why)
        {
            titleHidden = false;
            string back;
            try { back = ReleaseTitle(); }
            catch (Exception e) { back = "NOT all given back (" + e.Message + ")"; }
            Plugin.L.LogInfo($"{line}: {back}.");
            if (onForTitle) { onForTitle = false; Switch(false, why); }
        }

        /// <summary>Why to give the menu back now, or null to keep it hidden.</summary>
        private static string TitleAside()
        {
            if (!MainMenuUp()) return "the main menu went away";
            string leaving = MenuLeaving();
            if (leaving != null) return leaving;
            try
            {
                var black = Global.Instance.m_BlackScreenCanvas;
                if (black != null && black.gameObject.activeInHierarchy && black.alpha > 0.05f) return "a fade to black";
            }
            catch (Exception) { return "the game starting"; }
            return null;
        }

        /// <summary>
        /// The game is leaving the main menu, or null. Continue, New Game and the credits all start with the loader's
        /// fade to black (SceneLoader.BeginFadeIn is the only thing setting fadingIn). IsLoading is useless here: it
        /// reads true the whole time the main menu is up.
        /// </summary>
        public static string MenuLeaving()
        {
            try
            {
                var loader = SceneLoader.Instance;
                if (loader != null && loader.fadingIn) return "the game fades to black for a load (Continue, New Game) or the credits";
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>The main menu back on screen after a fade (the credits come back to it). False when unsure.</summary>
        public static bool MenuInView()
        {
            if (!MainMenuUp() || MenuLeaving() != null) return false;
            try
            {
                var loader = SceneLoader.Instance;
                var black = loader != null ? loader.canvasGroup : null;
                return black == null || !black.gameObject.activeInHierarchy || black.alpha < 0.05f;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The loader's flags, for the log.</summary>
        private static string LoaderState()
        {
            try
            {
                var loader = SceneLoader.Instance;
                if (loader == null) return "no loader";
                var black = loader.canvasGroup;
                return $"the loader fading in {loader.fadingIn}, out {loader.fadingOut}, its black {(black == null ? "none" : black.gameObject.activeInHierarchy ? black.alpha.ToString("0.00") : "inactive")}" +
                       $" (its loading flag {loader.isLoading}, up the whole time the main menu is)";
            }
            catch (Exception e) { return "the loader unreadable (" + e.Message + ")"; }
        }

        /// <summary>Each LateUpdate while hidden; false once it lets go (then the normal clean frame runs).</summary>
        private static bool HoldTitleFrame()
        {
            string why = TitleAside();
            if (why != null)
            {
                // menu on its way out: stop comparing its selection. Not for the game's black screen, which can
                // cover a menu that stays
                if (MenuLeaving() != null || !MainMenuUp()) MenuInput.NoteLeaving();
                LetGoOfTitle($"Clean frame gives the main menu back ({why})", why);
                return false;
            }
            try { HoldTitle(); }
            catch (Exception e)
            {
                // never leave a hidden menu with its input half held
                Error(e);
                LetGoOfTitle("Clean frame gives the main menu back (it could not be hidden: " + e.Message + ")", "an error");
                return false;
            }
            if (!titleHolding)
            {
                titleHolding = true;
                holding = false;
                aside = "the main menu";
                Plugin.L.LogInfo($"Clean frame hides the main menu: {TitleState()}.");
            }
            return true;
        }

        private static void HoldTitle()
        {
            // input first: never hide a menu that can still be pressed
            MenuInput.Want(MenuInput.CleanTitle, true);
            var g = Global.Instance;
            // the UI camera draws the menu, prompts and windows. Fades too, but a fade lets go first (TitleAside)
            var ui = g.uiCamera;
            if (ui != null)
            {
                if (ui.Pointer != titleUiPtr) { GiveTitleMaskBack(); titleUi = ui; titleUiPtr = ui.Pointer; }
                int m = ui.cullingMask;
                if (m != 0) { titleUiMask = m; ui.cullingMask = 0; } // a mask the game set since is the one restored
                titleMaskHeld = true;
            }
            // screen canvases outside the UI camera (flat overlays, other cameras). World-space ones are scene, kept
            if (Time.unscaledTime >= nextOverlaySweep) SweepOverlays(g);
            for (int i = 0; i < titleOverlays.Count; i++)
            {
                var c = titleOverlays[i];
                if (c != null && c.enabled) c.enabled = false;
            }
        }

        /// <summary>A screen canvas the UI camera doesn't draw: an overlay, or another camera's.</summary>
        private static bool OutsideUiCamera(Canvas c, Camera ui)
        {
            if (c.renderMode == RenderMode.ScreenSpaceOverlay) return true;
            if (c.renderMode != RenderMode.ScreenSpaceCamera) return false;
            var w = c.worldCamera;
            return w == null || ((ui == null || w.Pointer != ui.Pointer) && w.isActiveAndEnabled);
        }

        /// <summary>Switches off the game's screen canvases outside the UI camera (not ours, not the black screen).</summary>
        private static void SweepOverlays(Global g)
        {
            nextOverlaySweep = Time.unscaledTime + 0.5f;
            var black = g.m_BlackScreenCanvas != null ? TopCanvas(g.m_BlackScreenCanvas) : null;
            var ui = g.uiCamera;
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.isRootCanvas || !c.enabled || !OutsideUiCamera(c, ui)) continue;
                if (c.name.StartsWith("CloudpunkVR_") || (black != null && c.Pointer == black.Pointer)) continue;
                c.enabled = false;
                bool known = false;
                for (int k = 0; k < titleOverlays.Count && !known; k++) known = titleOverlays[k] != null && titleOverlays[k].Pointer == c.Pointer;
                if (!known) titleOverlays.Add(c);
            }
        }

        private static bool GiveTitleMaskBack()
        {
            bool gave = false;
            if (titleMaskHeld && titleUi != null && titleUi.cullingMask == 0) { titleUi.cullingMask = titleUiMask; gave = true; }
            titleMaskHeld = false;
            return gave;
        }

        /// <summary>Gives back what hiding the menu changed, piece by piece; returns what came back.</summary>
        private static string ReleaseTitle()
        {
            titleHolding = false;
            nextOverlaySweep = 0f; // next hide sweeps at once
            var back = new List<string>();
            try
            {
                string cam = titleUi != null ? titleUi.name : "the UI camera";
                if (GiveTitleMaskBack()) back.Add($"'{cam}' draws again (mask 0x{titleUiMask:X8})");
            }
            catch (Exception e) { back.Add("the UI camera's mask NOT given back (" + e.Message + ")"); }
            titleUi = null;
            titleUiPtr = IntPtr.Zero;
            int n = 0;
            var names = new StringBuilder();
            for (int i = 0; i < titleOverlays.Count; i++)
            {
                var c = titleOverlays[i];
                try
                {
                    if (c == null || c.enabled) continue;
                    c.enabled = true;
                    n++;
                    names.Append(names.Length > 0 ? ", " : "").Append(c.name);
                }
                catch (Exception) { }
            }
            titleOverlays.Clear();
            if (n > 0) back.Add($"{n} screen canvas(es) outside the UI camera on again ({names})");
            MenuInput.Want(MenuInput.CleanTitle, false);
            back.Add(MenuInput.AfterLetGo());
            return string.Join("; ", back);
        }

        /// <summary>What the hidden menu looks like now, for the log.</summary>
        private static string TitleState()
        {
            var g = Global.Instance;
            var sb = new StringBuilder();
            var ui = g != null ? g.uiCamera : null;
            sb.Append(ui != null ? $"'{ui.name}' draws nothing (mask 0x{ui.cullingMask:X8}, the game's 0x{titleUiMask:X8}: the list with VREVIVAL, the prompts, its card)" : "no UI camera");
            int overlays = 0;
            for (int i = 0; i < titleOverlays.Count; i++) if (titleOverlays[i] != null && !titleOverlays[i].enabled) overlays++;
            sb.Append($", {overlays} screen canvas(es) outside the UI camera off");
            sb.Append("; ").Append(MenuInput.Describe());
            if (VR.VRSession.Active) sb.Append("; the VR panel and the laser off");
            var mc = g != null && g.menuCamera != null ? g.menuCamera.GetComponent<Camera>() : null;
            sb.Append($"; kept: the menu camera '{(mc != null ? mc.name : "none")}' {(mc != null && mc.isActiveAndEnabled ? "drawing" : "NOT drawing")} (mask 0x{(mc != null ? mc.cullingMask : 0):X8}), " +
                      $"the sign with VREVIVAL {(UI.MenuSign.Showing ? "up" : "not up")}");
            return sb.ToString();
        }

        /// <summary>Logs the main menu coming and going.</summary>
        private static void WatchMenu()
        {
            bool up = MainMenuUp();
            if (up == menuWasUp) return;
            menuWasUp = up;
            if (menuNotes++ < 20) Plugin.L.LogInfo(up ? "Main menu: up." : "Main menu: gone.");
        }
    }
}
