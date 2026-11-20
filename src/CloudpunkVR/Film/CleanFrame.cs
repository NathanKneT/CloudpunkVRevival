using System;
using System.Collections.Generic;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Clean frame for filming (F2): hides the game's and the mod's UI. Hands, props and cockpit stay.
    /// Gives back only what it changed, when off or while a menu, the map, a window or a trade is open.
    /// Held every frame since the game turns canvases back on. On the main menu F2 hides the menu instead (CleanFrame.Title).
    /// </summary>
    internal static partial class CleanFrame
    {
        public static bool On { get; private set; }

        /// <summary>Keep the dialogue line (setting, or `clean dialogue on|off` for the run).</summary>
        public static bool KeepDialogue => keepOverride ?? VRConfig.CleanFrameKeepDialogue.Value;

        public static bool HidesOverlays => On;

        /// <summary>With it on, only the dialogue line and call box show, and only when kept.</summary>
        public static bool ShowsHudPiece(bool line) => !On || (line && KeepDialogue);

        public static bool HidesDialogue => On && !KeepDialogue;

        private static readonly Input.FilmKey key = new Input.FilmKey("Clean frame", "[Filming] CleanFrameKey", "the clean frame");
        private static bool started, holding;
        private static bool? keepOverride;
        private static string aside = "";
        private static int notes;
        private static readonly HashSet<string> errorsSeen = new HashSet<string>();

        // game state it changed
        private static readonly MaskHold mainMask = new MaskHold(), cockpitMask = new MaskHold();
        private static readonly CanvasSet map = new CanvasSet("the minimap with money and fuel"), sign = new CanvasSet("the district sign"),
                                          dialogScreen = new CanvasSet("the call screen"), callScreen = new CanvasSet("the call screen's ring");
        private static int textLayer = -2;
        private static Transform mapRoot, signRoot, dialogRoot, callRoot;
        private static float nextFind;

        // flat only: UpdateHudAlpha restores the HUD groups, ChangeVehicleState the dialogue boxes
        private static bool hudFaded, boxesFaded;
        private static readonly List<CanvasGroup> ignoring = new List<CanvasGroup>();

        // ---- Each frame.

        /// <summary>Flat and VR: the setting once, then the key.</summary>
        public static void Update()
        {
            try
            {
                if (!started)
                {
                    started = true;
                    if (VRConfig.CleanFrame.Value) Switch(true, "CleanFrame = true");
                }
                if (key.Pressed(VRConfig.CleanFrameKey.Value)) Toggle("key");
            }
            catch (Exception e) { Error(e); }
        }

        /// <summary>Logs each distinct error once.</summary>
        private static void Error(Exception e)
        {
            if (errorsSeen.Count < 30 && errorsSeen.Add(e.GetType().Name + ": " + e.Message)) Plugin.L.LogError("Clean frame (logged once): " + e);
        }

        /// <summary>After the game's Update, before canvases build and cameras draw.</summary>
        public static void LateUpdate()
        {
            try { WatchMenu(); }
            catch (Exception e) { Error(e); }
            // F2 on the main menu (CleanFrame.Title); once that lets go, the normal clean frame below
            bool title = false;
            if (On && titleHidden)
            {
                try { title = HoldTitleFrame(); }
                catch (Exception e)
                {
                    Error(e);
                    title = false;
                    try { if (titleHidden) LetGoOfTitle("Clean frame gives the main menu back (an error: " + e.Message + ")", "an error"); }
                    catch (Exception e2) { Error(e2); }
                }
            }
            if (On && !title)
            {
                try
                {
                    string why = StepAside();
                    if (why != null)
                    {
                        if (holding || why != aside) Note($"steps aside ({why}): {Release()}; the game's menus and windows show as without it");
                        holding = false;
                        aside = why;
                    }
                    else
                    {
                        if (!holding) Note($"holds ({Place()}, the game's line {(KeepDialogue ? "kept" : "hidden")})");
                        holding = true;
                        aside = "";
                        Hold();
                    }
                }
                catch (Exception e) { Error(e); }
            }
#if DEV
            if (watching && !VRSession.Active) Watch();
#endif
        }

        // ---- On and off.

        public static void Switch(bool on, string why)
        {
            if (on == On) return;
            if (on)
            {
                string reread = why == "key" ? ReadSettingsAgain() : null;
                On = true;
                holding = false;
                aside = "";
                Plugin.L.LogInfo($"Clean frame on ({why}{(reread != null ? "; [Filming] read again: " + reread : "")}): {Plan()}; {key.Describe()}.");
            }
            else
            {
                On = false;
                string title = titleHidden ? "the main menu back: " + ReleaseTitle() + "; " : "";
                titleHidden = false;
                onForTitle = false;
                string back = Release();
                holding = false;
                aside = "";
#if DEV
                StopWatch("the clean frame went off");
#endif
                Plugin.L.LogInfo($"Clean frame off ({why}): {title}{back}; the mod's own pieces show again.");
                if (VRSession.Active) Wrist.Note("Filming", "clean frame off", true);
            }
        }

#if DEV
        /// <summary>The camera's mask with the prompts layer put back, for dev tools that save and restore masks.</summary>
        public static int GameMask(Camera c) => c == null ? 0 : c.cullingMask | mainMask.Bit(c) | cockpitMask.Bit(c);

        /// <summary>The line kept or hidden for the run (`clean dialogue on|off`).</summary>
        public static void KeepDialogueForRun(bool keep)
        {
            keepOverride = keep;
            Plugin.L.LogInfo($"Clean frame: the game's line {(keep ? "kept" : "hidden")} for the run{(On ? $" ({(holding ? "held at once" : "when it holds")})" : "")}.");
        }
#endif

        /// <summary>What it takes out, for the log.</summary>
        private static string Plan() =>
            "out of the picture: the game's HUD (its minimap, money and fuel, jobs and notices), the floating prompts (interact, PARK VEHICLE, refuel), " +
            "the cockpit's minimap screen and district sign" + (KeepDialogue ? "" : ", the call screen") +
            (VRSession.Active ? "; of the mod's: the windscreen HUD and the HUD's pieces, the wrist, the radar, the vignette, the passage card, the look glow, the laser away from a menu"
                              : "; of the mod's: the free camera's line on the monitor") +
            (KeepDialogue ? "; the game's line kept (CleanFrameKeepDialogue)" : "; the game's line hidden too (CleanFrameKeepDialogue = false)");

        private static string Place() => VRSession.Active ? CameraDriver.Mode.ToString() : Global.IsInVehicle ? (Global.IsInVehicleCockpit ? "cockpit" : "outside view") : "on foot";

        /// <summary>Logs a transition (capped).</summary>
        private static void Note(string what)
        {
            if (notes++ < 60) Plugin.L.LogInfo("Clean frame " + what + ".");
        }

        private static string ReadSettingsAgain() => VRConfig.ReadSectionAgain("Filming", name => name switch
        {
            "CleanFrameKeepDialogue" => VRConfig.CleanFrameKeepDialogue,
            "CleanFrameKey" => VRConfig.CleanFrameKey,
            _ => null,
        });

        // ---- When it steps aside.

        /// <summary>Why to step aside now (a menu is open), or null to hold.</summary>
        private static string StepAside()
        {
            var g = Global.Instance;
            if (g == null) return "no game yet";
            if (VRSession.Active)
            {
                var mode = CameraDriver.Mode;
                if (mode == ViewMode.Startup || mode == ViewMode.Menu || TitleScreen.OnPanel) return "the main menu";
            }
            else if (g.menuCamera != null && g.menuCamera.gameObject.activeInHierarchy) return "the main menu";
            try
            {
                if (g.IsInMenu()) return "the game's menu";
                if (Global.FullMapOpen) return "the map";
                if (g.HasBigWindowOpen()) return "a window";
                if (g.IsInDialog(true)) return "a trade";
            }
            catch (Exception) { return "the game starting"; } // its static flags read Global before it is set up
            return null;
        }

        // ---- Holding the game's side.

        private static void Hold()
        {
            var g = Global.Instance;
            if (textLayer == -2)
            {
                textLayer = LayerMask.NameToLayer("Text");
                Plugin.L.LogInfo(textLayer >= 0 ? $"Clean frame: the floating prompts are on layer {textLayer} ('Text')."
                                                : "Clean frame: no 'Text' layer, the floating prompts stay.");
            }
            mainMask.Hold(g.mainCamera, textLayer);
            var vi = g.vehicleInterior;
            cockpitMask.Hold(vi != null ? vi.cameraCockpit : null, textLayer);

            if (Time.unscaledTime >= nextFind || Gone(mapRoot) || Gone(signRoot) || Gone(dialogRoot) || Gone(callRoot)) Find(g);
            map.Hold(mapRoot);
            sign.Hold(signRoot);
            if (KeepDialogue) { dialogScreen.Release(); callScreen.Release(); }
            else { dialogScreen.Hold(dialogRoot); callScreen.Hold(callRoot); }

            if (!VRSession.Active) HoldFlatHud(g);
        }

        /// <summary>Cockpit screens, refound every 2 s. Sign and call screen only if world-space (else it's the HUD).</summary>
        private static void Find(Global g)
        {
            nextFind = Time.unscaledTime + 2f;
            var hudRoot = TopCanvas(g.hudParent);
            var mm = Minimap.Instance;
            mapRoot = RootOf(mm != null ? mm.mapSmall : null, false, hudRoot);
            signRoot = RootOf(g.textSectorNameInterior, true, hudRoot);
            var d = g.dialogUI;
            var c = g.incomingCallUI;
            dialogRoot = RootOf(d != null ? d.interiorCanvasGroupGlobal : null, true, hudRoot);
            callRoot = RootOf(c != null ? c.interiorCanvasGroupGlobal : null, true, hudRoot);
            if (callRoot != null && dialogRoot != null && callRoot.Pointer == dialogRoot.Pointer) callRoot = null; // one canvas holds both
        }

        /// <summary>Topmost canvas, inactive parents included (GetComponentInParent skips them in this Unity).</summary>
        private static Canvas TopCanvas(Component c)
        {
            Canvas top = null;
            for (var t = c != null ? c.transform : null; t != null; t = t.parent)
            {
                var canvas = t.GetComponent<Canvas>();
                if (canvas != null) top = canvas;
            }
            return top;
        }

        // destroyed with a scene: find again now, not in 2 s
        private static bool Gone(Transform t) => !ReferenceEquals(t, null) && t == null;

        private static Transform RootOf(Component c, bool worldOnly, Canvas hudRoot)
        {
            var root = TopCanvas(c);
            if (root == null || (hudRoot != null && root.Pointer == hudRoot.Pointer)) return null;
            if (worldOnly && root.renderMode != RenderMode.WorldSpace) return null;
            return root.transform;
        }

        // ---- The flat game's HUD.

        /// <summary>Flat: HUD groups faded each frame, the dialogue box kept over them or faded too.</summary>
        private static void HoldFlatHud(Global g)
        {
            hudFaded = true; // set first so a half-way failure still restores
            Fade(g.hudParent);
            var p = g.hudParents;
            if (p != null) { Fade(p.quests); Fade(p.racingUI); Fade(p.unlockPopup); }
            var d = g.dialogUI;
            var c = g.incomingCallUI;
            var dialogBox = d != null ? d.exteriorCanvasGroupGlobal : null;
            var callBox = c != null ? c.exteriorCanvasGroupGlobal : null;
            if (KeepDialogue)
            {
                if (boxesFaded) GiveBoxesBack(g);
                // the game's own HUD toggle hides the line too: respect that
                if (g.optionDisableHUD) ReleaseIgnoring();
                else { Ignore(dialogBox); Ignore(callBox); }
            }
            else
            {
                ReleaseIgnoring();
                boxesFaded = true;
                Fade(dialogBox);
                Fade(callBox);
            }
        }

        private static void Fade(CanvasGroup group)
        {
            if (group != null && group.alpha > 0f) group.alpha = 0f;
        }

        /// <summary>Keeps the box visible over the faded HUD.</summary>
        private static void Ignore(CanvasGroup group)
        {
            if (group == null || ignoreFailed || group.ignoreParentGroups) return;
            // setter unused by the game (may be stripped): if it fails, the line fades with the HUD
            try { group.ignoreParentGroups = true; }
            catch (Exception e)
            {
                ignoreFailed = true;
                Plugin.L.LogWarning($"Clean frame: the line's box cannot be kept over the faded HUD in this build ({e.Message}); in the flat game the line goes with the HUD.");
                return;
            }
            ignoring.Add(group);
        }

        private static bool ignoreFailed;

        private static int ReleaseIgnoring()
        {
            int n = 0;
            for (int i = 0; i < ignoring.Count; i++)
                if (ignoring[i] != null && ignoring[i].ignoreParentGroups) { ignoring[i].ignoreParentGroups = false; n++; }
            ignoring.Clear();
            return n;
        }

        private static void GiveBoxesBack(Global g)
        {
            boxesFaded = false;
            if (g.dialogUI != null) g.dialogUI.ChangeVehicleState();
            if (g.incomingCallUI != null) g.incomingCallUI.ChangeVehicleState();
        }

        // ---- Giving back.

        /// <summary>Step by step so one failure doesn't block the rest.</summary>
        private static string Release()
        {
            var back = new List<string>();
            var failed = new List<string>();
            void Step(string what, Func<bool> give)
            {
                try { if (give()) back.Add(what); }
                catch (Exception e) { failed.Add(what + " (" + e.Message + ")"); }
            }
            Step("the floating prompts in the world camera", mainMask.Release);
            Step("the floating prompts in the cockpit camera", cockpitMask.Release);
            foreach (var set in new[] { map, sign, dialogScreen, callScreen })
                Step(set.Name, () => set.Release() > 0);
            var g = Global.Instance;
            Step("the HUD's groups (the game's UpdateHudAlpha)", () =>
            {
                if (!hudFaded) return false;
                hudFaded = false;
                if (g != null) g.UpdateHudAlpha();
                return true;
            });
            Step("the line's box over the HUD", () => ReleaseIgnoring() > 0);
            Step("the line's boxes (the game's ChangeVehicleState)", () =>
            {
                if (!boxesFaded) return false;
                if (g != null) GiveBoxesBack(g);
                boxesFaded = false;
                return true;
            });
            string s = back.Count > 0 ? "given back: " + string.Join(", ", back) : "nothing of the game's to give back";
            return failed.Count > 0 ? s + "; NOT given back: " + string.Join(", ", failed) : s;
        }

        // ---- What it holds.

        /// <summary>A layer removed from a camera's mask, given back only if still out.</summary>
        private sealed class MaskHold
        {
            private Camera cam;
            private IntPtr camPtr;
            private int bit;
            public bool Removed { get; private set; }

            public void Hold(Camera c, int layer)
            {
                if (c == null || layer < 0) { Release(); return; }
                if (c.Pointer != camPtr) { Release(); cam = c; camPtr = c.Pointer; }
                bit = 1 << layer;
                if ((c.cullingMask & bit) == 0) return; // already out, by us or someone else
                c.cullingMask &= ~bit;
                Removed = true;
            }

            public bool Release()
            {
                bool gave = Removed && cam != null && (cam.cullingMask & bit) == 0;
                if (gave) cam.cullingMask |= bit;
                Removed = false;
                cam = null;
                camPtr = IntPtr.Zero;
                return gave;
            }

            /// <summary>The layer it leaves out of this camera, 0 if none.</summary>
            public int Bit(Camera c) => Removed && c != null && c.Pointer == camPtr ? bit : 0;

            /// <summary>For the check: whether the camera draws the layer.</summary>
            public static bool Draws(Camera c, int layer) => c != null && layer >= 0 && (c.cullingMask & (1 << layer)) != 0;
        }

        /// <summary>Canvases under a root, off while held. Only the ones it switched off go back on.</summary>
        private sealed class CanvasSet
        {
            public readonly string Name;
            private IntPtr rootPtr;
            private Il2CppArrayBase<Canvas> found;
            private float nextFind;
            private readonly List<Canvas> off = new List<Canvas>();

            public CanvasSet(string name) { Name = name; }

            public int Held => off.Count;

            public void Hold(Transform root)
            {
                if (root == null) { Release(); return; }
                if (root.Pointer != rootPtr) { Release(); rootPtr = root.Pointer; found = null; }
                if (found == null || Time.unscaledTime >= nextFind)
                {
                    found = root.GetComponentsInChildren<Canvas>(true);
                    nextFind = Time.unscaledTime + 2f;
                }
                for (int i = 0; i < found.Length; i++)
                {
                    var c = found[i];
                    if (c == null || !c.enabled) continue;
                    c.enabled = false;
                    if (!Has(c)) off.Add(c);
                }
            }

            private bool Has(Canvas c)
            {
                for (int i = 0; i < off.Count; i++) if (off[i] != null && off[i].Pointer == c.Pointer) return true;
                return false;
            }

            public int Release()
            {
                int n = 0;
                for (int i = 0; i < off.Count; i++)
                {
                    var c = off[i];
                    if (c != null && !c.enabled) { c.enabled = true; n++; }
                }
                off.Clear();
                rootPtr = IntPtr.Zero;
                found = null;
                return n;
            }

            /// <summary>For the watch: any found canvas on. No allocation.</summary>
            public bool AnyOn()
            {
                if (found == null) return false;
                for (int i = 0; i < found.Length; i++)
                {
                    var c = found[i];
                    if (c != null && c.enabled && c.gameObject.activeInHierarchy) return true;
                }
                return false;
            }

            /// <summary>For the check: how many canvases under the root draw now.</summary>
            public static int Drawing(Transform root, out int total)
            {
                total = 0;
                if (root == null) return 0;
                var all = root.GetComponentsInChildren<Canvas>(true);
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    total++;
                    if (c.enabled && c.gameObject.activeInHierarchy) n++;
                }
                return n;
            }
        }
    }
}
