#if DEV
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Dev command `clean`: state, `on|off`, `key`, `dialogue on|off`,
    /// `check [label]` (every piece, ok or FAILED), `watch <s> [label]` (same check every frame for s seconds).
    /// On the main menu `on` and `key` hide the menu like F2, `check` checks the menu.
    /// </summary>
    internal static partial class CleanFrame
    {
        public static string Command(string[] a)
        {
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "on": // on the main menu: hides the menu too, like F2
                    if (!titleHidden && MainMenuUp()) HideTitle("developer command");
                    else Switch(true, "developer command");
                    break;
                case "off":
                    if (titleHidden) ShowTitle("developer command");
                    Switch(false, "developer command");
                    break;
                case "key": Toggle("key"); break;
                case "dialogue": KeepDialogueForRun(a.Length < 3 || a[2] != "off"); break;
                case "check": return Check(a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "now");
                case "watch":
                    if (a.Length > 2 && a[2] == "end") // end the watch now
                    {
                        if (!watching) return "no watch running";
                        FinishWatch(null);
                        return $"watch {watchNumber} ended";
                    }
                    StartWatch(a.Length > 2 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 30f, a.Length > 3 ? string.Join(" ", a, 3, a.Length - 3) : "");
                    return $"watch {watchNumber}: every frame for {watchSeconds:0} s";
            }
            return Describe();
        }

        public static string Describe()
        {
            string state = !On ? "off" : titleHidden ? (titleHolding ? "on, hiding the main menu" : "on, hiding the main menu from the next frame")
                         : holding ? $"on, holding ({Place()})" : $"on, stepping aside ({(aside.Length > 0 ? aside : "not yet in play")})";
            return $"{state}; the game's line {(KeepDialogue ? "kept" : "hidden")}{(keepOverride.HasValue ? " for the run" : "")}; " +
                   $"floating prompts out of {(mainMask.Removed ? 1 : 0) + (cockpitMask.Removed ? 1 : 0)} camera(s); canvases it switched off: {map.Name} {map.Held}, " +
                   $"{sign.Name} {sign.Held}, {dialogScreen.Name} {dialogScreen.Held + callScreen.Held}" +
                   (VRSession.Active ? "" : $"; the HUD's groups {(hudFaded ? "faded" : "as the game has them")}, the line's box {(ignoring.Count > 0 ? "over them" : boxesFaded ? "faded" : "as the game has it")}") +
                   $"; {key.Describe()}";
        }

        // ---- The check.

        private enum Part { Other, Hand, Line, Menu }

        private static readonly List<(Renderer r, Part part, string name)> overlayRenderers = new List<(Renderer, Part, string)>();
        private static readonly List<(Canvas c, string name)> overlayCanvases = new List<(Canvas, string)>();
        private static float nextOverlayFind;

        /// <summary>Mod renderers under the overlay root, and canvases on its layer.</summary>
        private static void FindOverlay()
        {
            nextOverlayFind = Time.unscaledTime + 2f;
            overlayRenderers.Clear();
            overlayCanvases.Clear();
            var root = VRUI.Root;
            if (root != null)
            {
                var all = root.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i];
                    if (r == null) continue;
                    string name = r.gameObject.name;
                    overlayRenderers.Add((r, Classify(r.transform, name), name));
                }
            }
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c != null && c.gameObject.layer == VRUI.Layer && c.gameObject.scene.IsValid()) overlayCanvases.Add((c, c.gameObject.name));
            }
        }

        private static Part Classify(Transform t, string name)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == "CloudpunkVR_LeftHand" || p.name == "CloudpunkVR_RightHand") return Part.Hand;
            if (name == "CloudpunkVR_HudPieceDialogue" || name == "CloudpunkVR_HudPieceCall") return Part.Line;
            if (name == "CloudpunkVR_UIPanel" || name == "CloudpunkVR_MenuBackdrop" || name == "CloudpunkVR_Laser" || name == "CloudpunkVR_LaserDot") return Part.Menu;
            return Part.Other;
        }

        /// <summary>Overlay things drawn that should be hidden (null: none). Hands always stay.</summary>
        private static string OverlayDrawn(out int count, StringBuilder kept)
        {
            count = 0;
            string first = null;
            int layer = VRUI.Layer;
            for (int i = 0; i < overlayRenderers.Count; i++)
            {
                var (r, part, name) = overlayRenderers[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.gameObject.layer != layer) continue;
                bool allowed = part == Part.Hand || !On || (part == Part.Line && KeepDialogue) || (part == Part.Menu && !holding && !HidesMenu);
                if (allowed) { if (kept != null && part != Part.Hand) kept.Append(kept.Length > 0 ? ", " : "").Append(name); continue; }
                count++;
                if (first == null) first = name;
                else if (count <= 4) first += ", " + name;
            }
            for (int i = 0; i < overlayCanvases.Count; i++)
            {
                var (c, name) = overlayCanvases[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !On) continue;
                count++;
                if (first == null) first = name + " (canvas)";
                else if (count <= 4) first += ", " + name + " (canvas)";
            }
            return first;
        }

        /// <summary>Hand parts drawn now, by any camera.</summary>
        private static int HandParts()
        {
            int n = 0;
            for (int i = 0; i < overlayRenderers.Count; i++)
            {
                var (r, part, _) = overlayRenderers[i];
                if (part == Part.Hand && r != null && r.enabled && r.gameObject.activeInHierarchy) n++;
            }
            return n;
        }

        /// <summary>Held game UI that still draws (null: nothing).</summary>
        private static string GameLeft()
        {
            var g = Global.Instance;
            if (g == null) return null;
            var sb = new StringBuilder();
            if (textLayer >= 0)
            {
                if (MaskHold.Draws(g.mainCamera, textLayer)) sb.Append("the floating prompts in the world camera; ");
                var vi = g.vehicleInterior;
                if (vi != null && MaskHold.Draws(vi.cameraCockpit, textLayer)) sb.Append("the floating prompts in the cockpit camera; ");
            }
            Left(sb, map.Name, mapRoot);
            Left(sb, sign.Name, signRoot);
            if (!KeepDialogue) { Left(sb, dialogScreen.Name, dialogRoot); Left(sb, callScreen.Name, callRoot); }
            if (!VRSession.Active)
            {
                if (g.hudParent != null && g.hudParent.alpha > 0f) sb.Append("the HUD's group; ");
                var p = g.hudParents;
                if (p != null && ((p.quests != null && p.quests.alpha > 0f) || (p.racingUI != null && p.racingUI.alpha > 0f) || (p.unlockPopup != null && p.unlockPopup.alpha > 0f)))
                    sb.Append("the jobs, race or unlock group; ");
                if (!KeepDialogue)
                {
                    var d = g.dialogUI;
                    if (d != null && d.exteriorCanvasGroupGlobal != null && d.exteriorCanvasGroupGlobal.alpha > 0f) sb.Append("the line's box; ");
                }
            }
            return sb.Length > 0 ? sb.ToString(0, sb.Length - 2) : null;
        }

        /// <summary>A held set never found (null: none), so it fails instead of passing.</summary>
        private static string Missing()
        {
            if (mapRoot == null) return map.Name + " not found";
            bool cockpit = VRSession.Active ? CameraDriver.Mode == ViewMode.Cockpit : Global.IsInVehicle && Global.IsInVehicleCockpit;
            if (!cockpit) return null;
            if (signRoot == null) return sign.Name + " not found";
            if (!KeepDialogue && dialogRoot == null) return dialogScreen.Name + " not found";
            return null;
        }

        // canvases of a held set that still draw
        private static void Left(StringBuilder sb, string name, Transform root)
        {
            if (root == null) return;
            int drawing = CanvasSet.Drawing(root, out int total);
            if (drawing > 0) sb.Append($"{name} ({drawing} of {total} canvases); ");
        }

        /// <summary>Logs every piece with its state, ending in ok or FAILED.</summary>
        private static string Check(string label)
        {
            if (VRSession.Active) FindOverlay();
            if (MainMenuUp()) return TitleCheck(label);
            var g = Global.Instance;
            var fails = new List<string>();
            var sb = new StringBuilder($"clean check {label}: the clean frame {(On ? "on" : "off")}, the game's line {(KeepDialogue ? "kept" : "hidden")}, ");
            sb.Append(!On ? "nothing held" : holding ? $"holding ({Place()})" : $"stepping aside ({(aside.Length > 0 ? aside : "not yet in play")})");
            if (VRSession.Active) sb.Append($", view {CameraDriver.Mode}");

            // The mod's own.
            if (VRSession.Active)
            {
                var kept = new StringBuilder();
                string drawn = OverlayDrawn(out int n, kept);
                sb.Append($"; the mod's: HUD pieces in view {HudLayout.ShownPieces()}, the wrist {(Wrist.Shown ? "shown" : "off")}, the vignette {(Vignette.Intensity > 0.002f ? $"{Vignette.Intensity:0.00}" : "off")}, " +
                          $"the passage card {(PassageCard.Showing ? "SHOWN" : "off")}, the look glow {(LookHighlight.HaloShown ? "on" : "off")}; " +
                          $"Rania's hands {HandParts()} part(s) drawn; the overlay camera draws {(kept.Length > 0 ? kept + ", and " : "")}{n} thing(s) it should not{(drawn != null ? ": " + drawn : "")}");
                if (On && n > 0) fails.Add("the mod's pieces drawn: " + drawn);
                if (On && PassageCard.Showing) fails.Add("the passage card shown");
                if (On && LookHighlight.HaloShown) fails.Add("the look glow shown");
            }
            else sb.Append($"; the free camera's line on the monitor {(FreeCamNote.Visible ? "SHOWN" : "off")}");
            if (On && FreeCamNote.Visible) fails.Add("the free camera's line on the monitor");

            // The game's.
            if (g != null)
            {
                if (textLayer == -2) textLayer = LayerMask.NameToLayer("Text");
                var vi = g.vehicleInterior;
                if (Time.unscaledTime >= nextFind || mapRoot == null) Find(g);
                sb.Append($"; the game's: the floating prompts (layer {textLayer}) drawn by the world camera {Yes(MaskHold.Draws(g.mainCamera, textLayer))}, by the cockpit camera {Yes(vi != null && MaskHold.Draws(vi.cameraCockpit, textLayer))}");
                sb.Append($"; {Canvases(map.Name, mapRoot)}, {Canvases(sign.Name, signRoot)}, {Canvases(dialogScreen.Name, dialogRoot)}");
                if (callRoot != null) sb.Append($", {Canvases(callScreen.Name, callRoot)}");
                if (!VRSession.Active) sb.Append("; ").Append(FlatHud(g));
                int seen = LineSeen(g, out string line);
                sb.Append("; ").Append(line);
                if (On && holding && seen > 0 && !KeepDialogue) fails.Add("the line in view though the clean frame hides it");
                if (On && holding && seen == 0 && KeepDialogue) fails.Add("the kept line not in view");
                if (On && holding)
                {
                    string left = GameLeft();
                    if (left != null) fails.Add("the game's still drawn: " + left);
                    string missing = Missing();
                    if (missing != null) fails.Add(missing);
                    if (!VRSession.Active)
                    {
                        string pictured = Pictured(g);
                        sb.Append("; the UI camera draws ").Append(pictured ?? "nothing but the kept line and the fades");
                        if (pictured != null) fails.Add("the UI camera draws " + pictured);
                    }
                }
                if (!On) Restored(g, fails);
            }
            else sb.Append("; no game yet");

            bool ok = fails.Count == 0;
            sb.Append(ok ? (On ? " -> clean check ok" : " -> clean check ok (all back)") : " -> clean check FAILED: " + string.Join("; ", fails));
            if (ok) Plugin.L.LogInfo(sb.ToString());
            else Plugin.L.LogWarning(sb.ToString());
            return "logged";
        }

        private static string Yes(bool b) => b ? "yes" : "no";

        private static string Canvases(string name, Transform root)
        {
            if (root == null) return name + " not found";
            int drawing = CanvasSet.Drawing(root, out int total);
            return $"{name} {drawing} of {total} canvas(es) drawing";
        }

        /// <summary>Flat HUD groups and dialogue box, as they are now.</summary>
        private static string FlatHud(Global g)
        {
            var p = g.hudParents;
            var d = g.dialogUI;
            var box = d != null ? d.exteriorCanvasGroupGlobal : null;
            return $"the HUD's group alpha {(g.hudParent != null ? g.hudParent.alpha : -1f):0.00}, jobs {(p != null && p.quests != null ? p.quests.alpha : -1f):0.00}, " +
                   $"the line's box alpha {(box != null ? box.alpha : -1f):0.00}{(box != null && box.ignoreParentGroups ? " over the HUD's" : "")}, the game's HUD {(g.optionDisableHUD ? "hidden by its toggle" : "shown")}";
        }

        /// <summary>Places the dialogue line shows, -1 if none is up or the HUD toggle hides it.</summary>
        private static int LineSeen(Global g, out string text)
        {
            var d = g.dialogUI;
            var c = g.incomingCallUI;
            bool talking = d != null && d.IsActiveAndNotFadingOut();
            bool ringing = c != null && c.gameObject.activeInHierarchy && !c.fadingOut;
            if (!talking && !ringing) { text = "no line up"; return -1; }
            bool cockpit = VRSession.Active ? CameraDriver.Mode == ViewMode.Cockpit : Global.IsInVehicle && Global.IsInVehicleCockpit;
            var outside = talking ? d.exteriorCanvasGroupGlobal : c.exteriorCanvasGroupGlobal;
            var inside = talking ? d.interiorCanvasGroupGlobal : c.interiorCanvasGroupGlobal;
            bool hud = VRSession.Active ? HudLayout.Showing("Dialogue") || HudLayout.Showing("Call")
                                        : outside != null && UiPicture.GroupAlpha(outside.transform) > 0.05f && CanvasDraws(outside);
            bool dash = cockpit && inside != null && UiPicture.GroupAlpha(inside.transform) > 0.05f && CanvasDraws(inside);
            text = $"{(talking ? "a line up" : "a call ringing")}, the HUD's box {(hud ? "in view" : "not in view")}{(cockpit ? $", the dashboard's {(dash ? "in view" : "not in view")}" : "")}";
            if (g.optionDisableHUD && !cockpit) { text += " (the game's HUD toggle hides it on foot)"; return -1; }
            return (hud ? 1 : 0) + (dash ? 1 : 0);
        }

        /// <summary>The component's canvas and its root canvas are both on.</summary>
        public static bool CanvasDraws(Component c)
        {
            var canvas = c != null ? c.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return true;
            var root = canvas.rootCanvas;
            return canvas.enabled && (root == null || root.enabled);
        }

        /// <summary>Flat: what the UI camera draws besides the kept line and the fades (null: nothing).</summary>
        private static string Pictured(Global g)
        {
            var cam = g.uiCamera;
            if (cam == null) return null;
            var skip = new HashSet<IntPtr>();
            if (g.m_BlackScreenCanvas != null) UiPicture.Collect(g.m_BlackScreenCanvas.transform, skip);
            if (KeepDialogue)
            {
                if (g.dialogUI != null && g.dialogUI.exteriorCanvasGroupGlobal != null) UiPicture.Collect(g.dialogUI.exteriorCanvasGroupGlobal.transform, skip);
                if (g.incomingCallUI != null && g.incomingCallUI.exteriorCanvasGroupGlobal != null) UiPicture.Collect(g.incomingCallUI.exteriorCanvasGroupGlobal.transform, skip);
            }
            UiPicture.StartScan();
            var sb = new StringBuilder();
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null || !c.isRootCanvas || !c.enabled || c.renderMode != RenderMode.ScreenSpaceCamera || c.worldCamera == null || c.worldCamera.Pointer != cam.Pointer) continue;
                if ((cam.cullingMask & (1 << c.gameObject.layer)) == 0) continue;
                var r = UiPicture.Content(c.transform, cam, skip, false);
                if (r.width <= 0f) continue;
                sb.Append(sb.Length > 0 ? ", " : "").Append($"'{c.name}' at {UiPicture.Describe(r)}");
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Main menu hidden: nothing of it drawn (UI camera mask 0, no game screen canvas, no VR panel or laser), input
        /// held, scene and sign kept. Shown: all of it back.
        /// </summary>
        private static string TitleCheck(string label)
        {
            var g = Global.Instance;
            var fails = new List<string>();
            bool hidden = HidesMenu;
            var sb = new StringBuilder($"clean check {label}: on the main menu, the clean frame {(On ? "on" : "off")}, the menu {(hidden ? "hidden" : titleHidden ? "hidden from the next frame" : "shown")}");

            var ui = g.uiCamera;
            string pictured = ui != null && ui.cullingMask != 0 ? Pictured(g) : null;
            sb.Append($"; the UI camera '{(ui != null ? ui.name : "none")}' mask 0x{(ui != null ? ui.cullingMask : 0):X8}, draws {pictured ?? "nothing"}");
            int overlays = OverlaysDrawing(g, out string overlayNames);
            sb.Append($"; {overlays} screen canvas(es) of the game's outside the UI camera drawing{(overlayNames.Length > 0 ? " (" + overlayNames + ")" : "")}");
            sb.Append($"; the free camera's line {(FreeCamNote.Visible ? "SHOWN" : "off")}; {MenuInput.Describe()}");
            if (VRSession.Active)
            {
                var kept = new StringBuilder();
                OverlayDrawn(out int n, kept);
                sb.Append($"; VR: the panel {(VRUI.Interactive ? "up" : "off")}{(VRUI.MenuShown ? ", the menu on it" : "")}, the overlay camera draws {(kept.Length > 0 ? kept + ", and " : "")}{n} thing(s) it should not");
                if (hidden && (VRUI.Interactive || VRUI.MenuShown)) fails.Add("the VR panel up");
                if (hidden && n > 0) fails.Add("the overlay camera draws the mod's pieces");
                if (!hidden && !titleHidden && !VRUI.Interactive) fails.Add("the VR panel not up for the menu");
            }
            var mc = g.menuCamera != null ? g.menuCamera.GetComponent<Camera>() : null;
            bool scene = mc != null && mc.isActiveAndEnabled;
            sb.Append($"; kept: the menu camera {(scene ? "drawing" : "NOT drawing")}, the sign with VREVIVAL {(UI.MenuSign.Showing ? "up" : "not up")}");
            if (!scene) fails.Add("the menu camera not drawing");
            if (VRConfig.MenuSign.Value && !UI.MenuSign.Showing) fails.Add("the sign's VREVIVAL line not up");
            // not IsLoading: it's true the whole time on the main menu (MenuLeaving)
            sb.Append("; ").Append(LoaderState());
            string leaving = MenuLeaving();
            if (leaving != null) fails.Add(leaving + ": something in the menu was pressed");

            if (hidden)
            {
                if (ui != null && ui.cullingMask != 0) fails.Add("the UI camera draws");
                if (overlays > 0) fails.Add("screen canvases outside the UI camera drawn: " + overlayNames);
                if (FreeCamNote.Visible) fails.Add("the free camera's line on the monitor");
                if (!MenuInput.HeldBy(MenuInput.CleanTitle) || !MenuInput.AllHeld()) fails.Add("the menu's input not held");
                string moved = MenuInput.SelectionMoved();
                if (moved != null) fails.Add(moved + " while the menu was hidden");
            }
            else if (!titleHidden)
            {
                if (ui != null && ui.cullingMask == 0) fails.Add("the UI camera still draws nothing");
                if (!VRSession.Active && pictured == null) fails.Add("the menu not drawn");
                if (titleOverlays.Count > 0) fails.Add("screen canvases still held off");
                // the menu input all back, unless the free camera holds it now
                if (MenuInput.HeldBy(MenuInput.CleanTitle)) fails.Add("the menu's input still held by the clean frame");
                else if (!MenuInput.HeldBy(MenuInput.FreeCamera))
                {
                    string notBack = MenuInput.NotBack();
                    if (notBack != null) fails.Add("the menu's input not as it was: " + notBack);
                }
            }

            bool ok = fails.Count == 0;
            sb.Append(ok ? (hidden ? " -> clean check ok" : " -> clean check ok (all back)") : " -> clean check FAILED: " + string.Join("; ", fails));
            if (ok) Plugin.L.LogInfo(sb.ToString());
            else Plugin.L.LogWarning(sb.ToString());
            return "logged";
        }

        /// <summary>Game screen canvases drawing outside the UI camera (ours and the black screen excepted).</summary>
        private static int OverlaysDrawing(Global g, out string names)
        {
            var black = g.m_BlackScreenCanvas != null ? TopCanvas(g.m_BlackScreenCanvas) : null;
            var ui = g.uiCamera;
            var sb = new StringBuilder();
            int n = 0;
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.isRootCanvas || !c.enabled || !c.gameObject.activeInHierarchy || !OutsideUiCamera(c, ui)) continue;
                if (c.name.StartsWith("CloudpunkVR_") || (black != null && c.Pointer == black.Pointer)) continue;
                n++;
                if (n <= 4) sb.Append(sb.Length > 0 ? ", " : "").Append(c.name);
            }
            names = sb.ToString();
            return n;
        }

        /// <summary>For the watch: what of the hidden menu is back (null: nothing).</summary>
        private static string TitleLeftFast()
        {
            if (titleUi != null && titleUi.cullingMask != 0) return "the UI camera draws";
            for (int i = 0; i < titleOverlays.Count; i++)
                if (titleOverlays[i] != null && titleOverlays[i].enabled) return "a screen canvas: " + titleOverlays[i].name;
            if (!MenuInput.AllHeld()) return "the menu's input";
            if (VRSession.Active && VRUI.Interactive) return "the VR panel";
            return MenuLeaving() ?? MenuInput.SelectionMoved();
        }

        /// <summary>Off: nothing of the game's left as we set it.</summary>
        private static void Restored(Global g, List<string> fails)
        {
            if (mainMask.Removed || cockpitMask.Removed || map.Held + sign.Held + dialogScreen.Held + callScreen.Held > 0 || hudFaded || boxesFaded || ignoring.Count > 0)
                fails.Add("something still held: " + Describe());
            var vi = g.vehicleInterior;
            if (textLayer >= 0 && g.mainCamera != null && !MaskHold.Draws(g.mainCamera, textLayer)) fails.Add("the world camera leaves the floating prompts out");
            if (textLayer >= 0 && vi != null && vi.cameraCockpit != null && !MaskHold.Draws(vi.cameraCockpit, textLayer)) fails.Add("the cockpit camera leaves the floating prompts out");
            if (VRSession.Active) return;
            // flat alphas should match what the game's routines set
            bool cockpit = Global.IsInVehicle && Global.IsInVehicleCockpit;
            float hud = !g.optionDisableHUD || cockpit ? 1f : 0f;
            if (g.hudParent != null && Mathf.Abs(g.hudParent.alpha - hud) > 0.01f) fails.Add($"the HUD's group at {g.hudParent.alpha:0.00}, the game's toggle wants {hud:0}");
            var box = g.dialogUI != null ? g.dialogUI.exteriorCanvasGroupGlobal : null;
            if (box != null && box.ignoreParentGroups) fails.Add("the line's box still over the HUD's group");
        }

        // ---- The watch: every frame.

        private static bool watching;
        private static int watchNumber, frames, blackFrames, asideFrames, overlayFrames, gameFrames, cardFrames, glowFrames, lineFrames;
        private static float watchStart, watchUntil, watchSeconds;
        private static string watchLabel = "", firstOverlay, firstGame;

        private static void StartWatch(float seconds, string label)
        {
            if (watching) FinishWatch("a new watch");
            watching = true;
            watchNumber++;
            watchSeconds = Mathf.Max(1f, seconds);
            watchStart = Time.unscaledTime;
            watchUntil = watchStart + watchSeconds;
            watchLabel = label;
            frames = blackFrames = asideFrames = overlayFrames = gameFrames = cardFrames = glowFrames = lineFrames = 0;
            firstOverlay = firstGame = null;
            if (VRSession.Active) FindOverlay();
            Plugin.L.LogInfo($"clean watch {watchNumber}{(label.Length > 0 ? " (" + label + ")" : "")}: every frame for {watchSeconds:0} s; {Describe()}.");
        }

        private static void StopWatch(string why)
        {
            if (watching) FinishWatch(why);
        }

        /// <summary>Each frame while watching (VR: render hook, flat: LateUpdate).</summary>
        public static void Watch()
        {
            if (!watching) return;
            try
            {
                if (VRSession.Active && Time.unscaledTime >= nextOverlayFind) FindOverlay();
                float t = Time.unscaledTime - watchStart;
                frames++;
                if (VRSession.Active && TransitionGuard.Active) blackFrames++;
                if (!holding && !HidesMenu) asideFrames++;
                if (VRSession.Active)
                {
                    string drawn = OverlayDrawn(out int n, null);
                    if (n > 0) { overlayFrames++; if (firstOverlay == null) firstOverlay = $"+{t:0.00} s: {drawn}"; }
                    if (PassageCard.Showing) cardFrames++;
                    if (LookHighlight.HaloShown) glowFrames++;
                    if (HudLayout.Showing("Dialogue") || HudLayout.Showing("Call")) lineFrames++;
                }
                else if (FreeCamNote.Visible) { overlayFrames++; if (firstOverlay == null) firstOverlay = $"+{t:0.00} s: the free camera's line on the monitor"; }
                if (holding || HidesMenu)
                {
                    string left = HidesMenu ? TitleLeftFast() : GameLeftFast();
                    if (left != null) { gameFrames++; if (firstGame == null) firstGame = $"+{t:0.00} s: {left}"; }
                }
                if (Time.unscaledTime >= watchUntil) FinishWatch(null);
            }
            catch (Exception e)
            {
                watching = false;
                Plugin.L.LogWarning($"clean watch {watchNumber}: stopped by an error: {e.Message} -> clean watch FAILED");
            }
        }

        /// <summary>GameLeft without allocations, for every frame.</summary>
        private static string GameLeftFast()
        {
            var g = Global.Instance;
            if (g == null) return null;
            if (textLayer >= 0)
            {
                if (MaskHold.Draws(g.mainCamera, textLayer)) return "the floating prompts in the world camera";
                var vi = g.vehicleInterior;
                if (vi != null && MaskHold.Draws(vi.cameraCockpit, textLayer)) return "the floating prompts in the cockpit camera";
            }
            string missing = Missing();
            if (missing != null) return missing;
            if (map.AnyOn()) return map.Name;
            if (sign.AnyOn()) return sign.Name;
            if (!KeepDialogue && (dialogScreen.AnyOn() || callScreen.AnyOn())) return dialogScreen.Name;
            if (!VRSession.Active && g.hudParent != null && g.hudParent.alpha > 0f) return "the HUD's group";
            return null;
        }

        private static void FinishWatch(string stopped)
        {
            watching = false;
            bool ok = stopped == null && frames > 0 && overlayFrames == 0 && gameFrames == 0 && cardFrames == 0 && glowFrames == 0;
            string line = $"clean watch {watchNumber}{(watchLabel.Length > 0 ? " (" + watchLabel + ")" : "")}: {(stopped == null ? "over" : "stopped (" + stopped + ")")} after {Time.unscaledTime - watchStart:0.0} s, " +
                          $"{frames} frames ({blackFrames} black, {asideFrames} with the game's menu, map or a window): the mod's pieces drawn in {overlayFrames}{(firstOverlay != null ? " (first " + firstOverlay + ")" : "")}, " +
                          $"the game's drawn in {gameFrames}{(firstGame != null ? " (first " + firstGame + ")" : "")}, the passage card in {cardFrames}, the look glow in {glowFrames}" +
                          (VRSession.Active ? $"; the kept line's box in view in {lineFrames}" : "") +
                          (ok ? " -> clean watch ok" : " -> clean watch FAILED");
            if (ok) Plugin.L.LogInfo(line);
            else Plugin.L.LogWarning(line);
        }
    }
}
#endif
