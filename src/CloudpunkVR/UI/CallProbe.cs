#if DEV
using System;
using System.Collections.Generic;
using System.Text;
using CloudpunkVR.Patches;
using CloudpunkVR.VR;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Dev command `call`: fake calls through the game's own boxes, and a per-frame check of which box shows.
    /// - `call [who] [lines]`: a Dialog built in code, via IncomingCall.FadeIn. No autosave, call timer restored.
    /// - `call watch [s]`: watch a call the game makes itself.
    /// - `call check [label]`: log where the box is (degrees, distance, room above wheel and dashboard).
    /// - `call next`: skip the line via debugForceSkip (calling OnContinue from outside cut the next line short).
    /// - `call windshield on|off`, `call hud on|off`.
    /// </summary>
    internal static class CallProbe
    {
        private static readonly string[] TestLines =
        {
            "Test call, line one. This box shows once, just above the dashboard and between its two screens: the portrait, who speaks and these words.",
            "Line two. Nothing of it on the dashboard's left screen and nothing over the wheel or the instruments; it stays put on the windscreen while the HOVA turns.",
            "Line three. A short one.",
            "Line four, the last. Then the box fades out and the call is over; the wrist keeps the lines.",
        };

        private const float Grace = 0.35f; // up this long with no box counts as unseen

        private sealed class Track
        {
            public readonly string Kind;
            public bool Seen, HaveLast, InCockpit;
            public string Place = "";
            public Rect First, LastDeg;
            public float Moved, DistMin = float.MaxValue, DistMax, PerPixel, DriftMm;
            public Vector3 LastCockpit;
            public Track(string kind) { Kind = kind; }
        }

        private static bool watching, sawRing, sawTalk;
        private static int watchNumber; // in every log line, to tell calls apart
        private static float startedAt, until, quietSince, upSince = -1f;
        private static int frames, blackFrames, menuFrames, hudFrames, gameFrames, twoFrames, wrongFrames, unseenFrames, ringFrames, talkFrames, lines;
        private static int cleanFrames, cleanShownFrames; // clean frame hid the line / a box showed anyway
        private static string lastLine = "", firstTwo, firstWrong, firstUnseen, lineLog = "";
        private static string ringSeen, talkSeen;
        private static Track dialogTrack = new Track("Dialogue"), callTrack = new Track("Call");

        private static int expectedLines;
        private static float savedCallTimer = -1f;

        // `call next`
        private static bool skipping;
        private static IntPtr skipLine;
        private static float skipUntil;

        public static string Dev(string[] a)
        {
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "check": return Check(a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "now");
                case "next": return Next();
                case "watch":
                    StartWatch(a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 60f, 0);
                    return $"watch {watchNumber}: the next call or line";
                case "windshield":
                    if (a.Length > 2) VRConfig.HudOnWindshield.Value = a[2] != "off";
                    return "HudOnWindshield " + (VRConfig.HudOnWindshield.Value ? "on" : "off") + " for the run";
                case "hud":
                    return Hud(a.Length < 3 || a[2] != "off");
                default:
                    string who = sub.Length > 0 && !char.IsDigit(sub[0]) ? a[1] : "Control";
                    int count = TestLines.Length - 1;
                    for (int i = 1; i < a.Length; i++)
                        if (int.TryParse(a[i], out int n)) count = Mathf.Clamp(n, 1, TestLines.Length);
                    return Start(who, count);
            }
        }

        // ---- The test call.

        // flat-screen test call
        private static int flatNumber;
        private static float flatTimer = -1f, flatStarted, flatQuietSince = -1f;

        /// <summary>Flat only: restores the call timer once the test call is over (quiet 1.5 s, or a minute).</summary>
        public static void PollFlat()
        {
            if (flatTimer < 0f) return;
            var g = Global.Instance;
            if (g == null) { flatTimer = -1f; return; }
            var d = g.dialogUI;
            var c = g.incomingCallUI;
            bool busy = (d != null && d.IsActiveAndNotFadingOut()) || (c != null && c.gameObject.activeInHierarchy);
            float age = Time.unscaledTime - flatStarted;
            if ((busy || age < 1f) && age < 60f) { flatQuietSince = -1f; return; }
            if (flatQuietSince < 0f) flatQuietSince = Time.unscaledTime;
            if (Time.unscaledTime - flatQuietSince < 1.5f && age < 60f) return;
            g.makeCallTimer = flatTimer;
            Plugin.L.LogInfo($"call (flat game) {flatNumber}: {(age < 60f ? "over" : "still up after a minute")} after {age:0.0} s, the game's timer to its next call put back to {flatTimer:0} s.");
            flatTimer = -1f;
        }

        private static string Start(string who, int count)
        {
            var g = Global.Instance;
            if (g == null || g.incomingCallUI == null || g.dialogUI == null) return "no game yet";
            if (g.dialogUI.IsActiveAndNotFadingOut()) return "a dialogue is already up: wait for its end";
            var def = Character(g, who, out string key, out string known);
            if (def == null) return $"no character '{who}' and no Camus among the game's characters ({known})";
            var dlg = new Dialog("CloudpunkVR_TestCall", "", "remote noBlock");
            for (int i = 0; i < count; i++)
            {
                string text = i == count - 1 ? TestLines[TestLines.Length - 1] : TestLines[i];
                dlg.AddLine("", key, "", i == 0 ? "dialogChar" : "", text, "", "", "", "", "", 0, 0, "");
            }
            if (dlg.activatedByCharacter == null) dlg.activatedByCharacter = def;
            // its end would autosave the real save and reset the call timer
            g.dialogUI.DisableAutosave();
            float timer = g.makeCallTimer;
            g.incomingCallUI.FadeIn(dlg);
            int lineCount = dlg.lines != null ? dlg.lines.Count : count;
            // flat: nothing to watch, PollFlat restores the timer
            if (!VRSession.Active)
            {
                flatNumber++;
                flatTimer = timer;
                flatStarted = Time.unscaledTime;
                flatQuietSince = -1f;
                return $"flat call {flatNumber}, from '{key}' ('{def.localizedName}'), {lineCount} line(s), not watched (flat game): each line moves on by itself";
            }
            StartWatch(60f, lineCount);
            savedCallTimer = timer;
            return $"a test call from '{key}' ('{def.localizedName}'), {expectedLines} line(s): the game's call box rings, then its dialogue box types them out; watch {watchNumber}, every frame";
        }

        /// <summary>By name (any case), else Camus.</summary>
        private static CharacterDefinition Character(Global g, string who, out string key, out string known)
        {
            key = null;
            known = "";
            var chars = g.characters;
            if (chars == null) return null;
            if (chars.TryGetValue(who, out var def) && def != null) { key = who; return def; }
            string camus = null;
            var names = new StringBuilder();
            var e = chars.Keys.GetEnumerator();
            while (e.MoveNext())
            {
                string k = e.Current;
                if (k == null || !chars.TryGetValue(k, out var d) || d == null) continue;
                if (names.Length < 300) names.Append(names.Length > 0 ? ", " : "").Append(k);
                if (key == null && string.Equals(k, who, StringComparison.OrdinalIgnoreCase)) { key = k; def = d; }
                if (camus == null && g.camusCharacter != null && d.Pointer == g.camusCharacter.Pointer) camus = k;
            }
            known = names.ToString();
            if (key != null) return def;
            if (camus == null) return null;
            key = camus;
            return g.camusCharacter;
        }

        private static string Next()
        {
            var d = Global.Instance != null ? Global.Instance.dialogUI : null;
            if (d == null || !d.IsActiveAndNotFadingOut()) return "no line up";
            var line = d.currentLine;
            skipLine = line != null ? line.Pointer : IntPtr.Zero;
            skipUntil = Time.unscaledTime + 2f;
            skipping = true;
            d.debugForceSkip = true;
            return $"skipping line {(line != null ? line.lineID + 1 : 0)}, as the game's continue button does";
        }

        /// <summary>Clears the skip once the line is gone (or after 2 s).</summary>
        private static void EndSkip()
        {
            if (!skipping) return;
            var d = Global.Instance != null ? Global.Instance.dialogUI : null;
            if (d == null) { skipping = false; return; }
            var line = d.currentLine;
            bool moved = !d.IsActiveAndNotFadingOut() || line == null || line.Pointer != skipLine;
            if (!moved && Time.unscaledTime < skipUntil) return;
            d.debugForceSkip = false;
            skipping = false;
            if (!moved) Plugin.L.LogWarning($"call: the line did not move on within 2 s of `call next` (the game's continue)");
        }

        /// <summary>Same as the game's HUD toggle.</summary>
        private static string Hud(bool on)
        {
            var g = Global.Instance;
            if (g == null) return "no game yet";
            g.optionDisableHUD = !on;
            g.UpdateHudAlpha();
            return "the game's HUD " + (on ? "shown" : "hidden") + " (its own toggle)";
        }

        // ---- The boxes in view.

        private struct Boxes
        {
            public bool HudDialog, GameDialog, HudCall, GameCall, DialogUp, CallUp;
            public int Count => (HudDialog ? 1 : 0) + (GameDialog ? 1 : 0) + (HudCall ? 1 : 0) + (GameCall ? 1 : 0);
            public bool Hud => HudDialog || HudCall;
            public bool Game => GameDialog || GameCall;
        }

        private static bool FadedIn(float phase, float text) => phase > 0.05f || text > 0.05f;
        private static bool Drawn(CanvasGroup g) => g != null && g.gameObject.activeInHierarchy && UiPicture.GroupAlpha(g.transform) >= 0.05f && Film.CleanFrame.CanvasDraws(g);

        private static Boxes Read()
        {
            var b = new Boxes();
            var g = Global.Instance;
            if (g == null) return b;
            bool cockpit = CameraDriver.Mode == ViewMode.Cockpit;
            var d = g.dialogUI;
            if (d != null && FadedIn(d.phaseValue, d.textAlpha))
            {
                b.DialogUp = d.IsActiveAndNotFadingOut();
                b.HudDialog = HudLayout.Showing("Dialogue") && Drawn(d.exteriorCanvasGroupGlobal);
                b.GameDialog = cockpit && Drawn(d.interiorCanvasGroupGlobal);
            }
            var c = g.incomingCallUI;
            if (c != null && c.gameObject.activeInHierarchy && FadedIn(c.phaseValue, c.textAlpha))
            {
                b.CallUp = !c.fadingOut;
                b.HudCall = HudLayout.Showing("Call") && Drawn(c.exteriorCanvasGroupGlobal);
                b.GameCall = cockpit && Drawn(c.interiorCanvasGroupGlobal);
            }
            return b;
        }

        /// <summary>False when the game's dashboard box should show instead.</summary>
        private static bool HudWanted => CameraDriver.Mode != ViewMode.Cockpit || CockpitBoxes.KeepOnHud();

        private static string Names(Boxes b)
        {
            var sb = new StringBuilder();
            void Add(bool on, string what) { if (on) sb.Append(sb.Length > 0 ? " and " : "").Append(what); }
            Add(b.HudCall, "the HUD's call box");
            Add(b.GameCall, "the game's call box on the dashboard");
            Add(b.HudDialog, "the HUD's dialogue box");
            Add(b.GameDialog, "the game's dialogue box on the dashboard");
            return sb.Length > 0 ? sb.ToString() : "no box";
        }

        // ---- The watch, every frame drawn.

        private static void StartWatch(float seconds, int expected)
        {
            if (watching) Finish(false);
            watchNumber++;
            watching = true;
            sawRing = sawTalk = false;
            startedAt = Time.unscaledTime;
            until = startedAt + Mathf.Max(1f, seconds);
            quietSince = upSince = -1f;
            frames = blackFrames = menuFrames = hudFrames = gameFrames = twoFrames = wrongFrames = unseenFrames = ringFrames = talkFrames = lines = 0;
            cleanFrames = cleanShownFrames = 0;
            firstTwo = firstWrong = firstUnseen = ringSeen = talkSeen = null;
            lineLog = "";
            expectedLines = expected;
            savedCallTimer = -1f;
            var d = Global.Instance != null ? Global.Instance.dialogUI : null;
            lastLine = d != null && d.textField != null ? d.textField.text ?? "" : "";
            dialogTrack = new Track("Dialogue");
            callTrack = new Track("Call");
        }

        /// <summary>From the render hook, after the HUD is placed.</summary>
        public static void Watch()
        {
            EndSkip();
            if (!watching) return;
            float t = Time.unscaledTime - startedAt;
            var g = Global.Instance;
            var b = Read();
            bool up = b.DialogUp || b.CallUp;
            if (!up) upSince = -1f;
            else if (upSince < 0f) upSince = Time.unscaledTime;
            if (TransitionGuard.Active) { blackFrames++; dialogTrack.HaveLast = callTrack.HaveLast = false; }
            else if (VRUI.MenuShown || (g != null && g.IsInMenu())) { menuFrames++; dialogTrack.HaveLast = callTrack.HaveLast = false; }
            else
            {
                frames++;
                bool ring = b.HudCall || b.GameCall, talk = b.HudDialog || b.GameDialog;
                if (ring) ringFrames++;
                if (talk) talkFrames++;
                if (b.Hud) hudFrames++;
                if (b.Game) gameFrames++;
                if (ring && !sawRing) { sawRing = true; ringSeen = $"+{t:0.0} s"; Plugin.L.LogInfo($"call watch {watchNumber}: ringing at +{t:0.0} s, view {CameraDriver.Mode}: {Names(b)}."); }
                if (talk && !sawTalk) { sawTalk = true; talkSeen = $"+{t:0.0} s"; Plugin.L.LogInfo($"call watch {watchNumber}: talking at +{t:0.0} s, view {CameraDriver.Mode}: {Names(b)}."); }
                if (b.Count > 1)
                {
                    twoFrames++;
                    if (firstTwo == null) { firstTwo = $"+{t:0.00} s ({Names(b)})"; Plugin.L.LogWarning($"call watch {watchNumber}: two boxes at " + firstTwo); }
                }
                bool wrong = HudWanted ? b.Game : b.Hud;
                if (wrong)
                {
                    wrongFrames++;
                    if (firstWrong == null) { firstWrong = $"+{t:0.00} s ({Names(b)}, view {CameraDriver.Mode}, HudOnWindshield {VRConfig.HudOnWindshield.Value})"; Plugin.L.LogWarning($"call watch {watchNumber}: the wrong box at " + firstWrong); }
                }
                // up but no box. Fine if the HUD is off outside the cockpit, or the clean frame hides lines.
                bool hudHidden = g != null && g.optionDisableHUD && CameraDriver.Mode != ViewMode.Cockpit;
                bool cleanHidden = Film.CleanFrame.HidesDialogue;
                if (cleanHidden && up) { cleanFrames++; if (b.Count > 0) cleanShownFrames++; }
                if (up && b.Count == 0 && !hudHidden && !cleanHidden && Time.unscaledTime - upSince > Grace)
                {
                    unseenFrames++;
                    if (firstUnseen == null)
                    {
                        firstUnseen = $"+{t:0.00} s ({(b.CallUp ? "a call" : "a line")} up, view {CameraDriver.Mode}, the HUD's pieces: {HudLayout.ShownPieces()})";
                        Plugin.L.LogWarning($"call watch {watchNumber}: no box in view at " + firstUnseen);
                    }
                }
                if (b.HudDialog) Follow(dialogTrack); else dialogTrack.HaveLast = false;
                if (b.HudCall) Follow(callTrack); else callTrack.HaveLast = false;
            }
            NewLine(t);

            bool showing = b.Count > 0 || up || TransitionGuard.Active;
            var d = g != null ? g.dialogUI : null;
            var c = g != null ? g.incomingCallUI : null;
            bool busy = (d != null && d.IsActiveAndNotFadingOut()) || (c != null && c.gameObject.activeInHierarchy);
            // over after 1.5 s quiet, but only once it has started
            if (showing || busy || !(sawRing || sawTalk || cleanFrames > 0)) quietSince = -1f;
            else if (quietSince < 0f) quietSince = Time.unscaledTime;
            bool over = quietSince >= 0f && Time.unscaledTime - quietSince > 1.5f;
            if (over || Time.unscaledTime > until) Finish(over);
        }

        private static void NewLine(float t)
        {
            var d = Global.Instance != null ? Global.Instance.dialogUI : null;
            if (d == null || d.textField == null || !FadedIn(d.phaseValue, d.textAlpha)) return;
            string text = d.textField.text ?? "";
            if (text.Length == 0 || text == lastLine || text.StartsWith("This is text", StringComparison.Ordinal)) return;
            lastLine = text;
            lines++;
            lineLog += (lineLog.Length > 0 ? ", " : "") + $"+{t:0.0} s";
            Plugin.L.LogInfo($"call watch {watchNumber}: line {lines} at +{t:0.0} s: '{(text.Length > 48 ? text.Substring(0, 48) + "..." : text)}'.");
        }

        /// <summary>First place, movement, distance, and frame-to-frame drift against the cockpit.</summary>
        private static void Follow(Track k)
        {
            if (!HudLayout.Where(k.Kind, out var deg, out var quad, out float perPixel, out _)) { k.HaveLast = false; return; }
            var abs = Absolute(deg, quad);
            if (!k.Seen) { k.Seen = true; k.First = abs; k.Place = Describe(abs); k.PerPixel = perPixel; }
            k.Moved = Mathf.Max(k.Moved, Mathf.Max(Mathf.Max(Mathf.Abs(abs.xMin - k.First.xMin), Mathf.Abs(abs.xMax - k.First.xMax)),
                                                   Mathf.Max(Mathf.Abs(abs.yMin - k.First.yMin), Mathf.Abs(abs.yMax - k.First.yMax))));
            float dist = Vector3.Distance(quad.position, CameraDriver.HeadWorldPosition);
            k.DistMin = Mathf.Min(k.DistMin, dist);
            k.DistMax = Mathf.Max(k.DistMax, dist);
            if (CameraDriver.Mode != ViewMode.Cockpit) { k.HaveLast = false; return; }
            CameraDriver.WorldToCockpit(quad.position, quad.rotation, out var cp, out _);
            if (k.HaveLast && deg == k.LastDeg) { k.DriftMm = Mathf.Max(k.DriftMm, (cp - k.LastCockpit).magnitude * 1000f); k.InCockpit = true; }
            k.LastCockpit = cp;
            k.LastDeg = deg;
            k.HaveLast = true;
        }

        private static void Finish(bool over)
        {
            watching = false;
            var g = Global.Instance;
            // the dialogue's FadeOut set the call timer, put it back
            float timerAtEnd = g != null ? g.makeCallTimer : -1f;
            string dialogue = Dialogue(g);
            if (savedCallTimer >= 0f && g != null) g.makeCallTimer = savedCallTimer;
            bool seen = sawRing || sawTalk;
            bool allLines = expectedLines == 0 || lines >= expectedLines;
            // clean frame hiding lines: no box is the pass
            bool hiddenAll = !seen && cleanFrames > 0;
            bool ok = (seen || hiddenAll) && twoFrames == 0 && wrongFrames == 0 && unseenFrames == 0 && cleanShownFrames == 0 && allLines;
            var sb = new StringBuilder($"call watch {watchNumber}: {(over ? "over" : "stopped")} after {Time.unscaledTime - startedAt:0.0} s, view {CameraDriver.Mode}, HudOnWindshield {(VRConfig.HudOnWindshield.Value ? "on" : "off")}{(g != null && g.optionDisableHUD ? ", the game's HUD hidden" : "")}: ");
            sb.Append($"ringing from {ringSeen ?? "never"} ({ringFrames} frames), talking from {talkSeen ?? "never"} ({talkFrames} frames), {lines}{(expectedLines > 0 ? " of " + expectedLines : "")} line(s){(lines > 0 ? " at " + lineLog : "")}; ");
            sb.Append($"{frames} frames drawn ({blackFrames} more black, {menuFrames} with a menu): the HUD's box in {hudFrames}, the game's on the dashboard in {gameFrames}, " +
                      $"two boxes in {twoFrames}{(firstTwo != null ? " (first " + firstTwo + ")" : "")}, the wrong box in {wrongFrames}{(firstWrong != null ? " (first " + firstWrong + ")" : "")}, " +
                      $"a line or a call up but no box in {unseenFrames}{(firstUnseen != null ? " (first " + firstUnseen + ")" : "")}");
            foreach (var k in new[] { callTrack, dialogTrack })
                if (k.Seen)
                    sb.Append($"; the HUD's {(k.Kind == "Call" ? "call" : "dialogue")} box at {k.Place}, moved up to {k.Moved:0.0} deg in view, {k.DistMin:0.00}..{k.DistMax:0.00} m from the eyes, {k.PerPixel:0.000} deg per pixel" +
                              (k.InCockpit ? $", against the cockpit at most {k.DriftMm:0.0} mm from one frame to the next" : ""));
            if (cleanFrames > 0) sb.Append($"; the clean frame hid the line in {cleanFrames} frames, a box in view anyway in {cleanShownFrames}");
            if (!seen && !hiddenAll) sb.Append("; no call box nor dialogue box seen");
            if (!allLines) sb.Append($"; fewer lines seen than the call had");
            sb.Append("; the game's dialogue ").Append(dialogue);
            if (savedCallTimer >= 0f) sb.Append($"; the game's timer to its next call {(Mathf.Abs(timerAtEnd - savedCallTimer) > 0.01f ? $"set to {timerAtEnd:0} s by its end, " : "")}put back to {savedCallTimer:0} s");
            savedCallTimer = -1f;
            sb.Append(ok ? " -> call watch ok" : " -> call watch FAILED");
            if (ok) Plugin.L.LogInfo(sb.ToString());
            else Plugin.L.LogWarning(sb.ToString());
        }

        /// <summary>
        /// Whether the game closed its dialogue and call. The dialogue box fades out (DialogHUD: textAlpha, phaseValue,
        /// then mainFadeoutDone) but only deactivates once the music is back up, so IsInDialog(false) stays true a while
        /// after it's gone. Faded out counts as closed.
        /// </summary>
        private static string Dialogue(Global g)
        {
            if (g == null) return "unknown (no game)";
            var d = g.dialogUI;
            var c = g.incomingCallUI;
            if (c != null && c.gameObject.activeInHierarchy) return $"STILL OPEN (the call box up, fading out {c.fadingOut})";
            if (d == null || !d.gameObject.activeInHierarchy) return "closed";
            if (d.fadingOut && d.mainFadeoutDone) return "closed (faded out, the game still bringing its music back up)";
            return $"STILL OPEN (the dialogue box up: fading out {d.fadingOut}, box {d.phaseValue:0.00}, text {d.textAlpha:0.00})";
        }

        // ---- What the game does with the lines, while a call is watched.

        /// <summary>From the game's FadeIn or OnContinue.</summary>
        public static void NoteTyping(int lineIndex)
        {
            if (!watching) return;
            var d = Global.Instance != null ? Global.Instance.dialogUI : null;
            var dlg = d != null ? d.dialog : null;
            int count = dlg != null && dlg.lines != null ? dlg.lines.Count : 0;
            Plugin.L.LogInfo($"call watch {watchNumber}: the game starts line {lineIndex + 1} of {count} at +{Time.unscaledTime - startedAt:0.00} s.");
        }

        public static void NoteContinue(DialogHUD d)
        {
            if (!watching || d == null) return;
            var line = d.currentLine;
            var dlg = d.dialog;
            int count = dlg != null && dlg.lines != null ? dlg.lines.Count : 0;
            var played = new StringBuilder();
            for (int i = 0; i < count; i++) { var l = dlg.lines[i]; played.Append(l != null && l.played ? '1' : '0'); }
            Plugin.L.LogInfo($"call watch {watchNumber}: the game continues after line {(line != null ? line.lineID + 1 : 0)} of {count} at +{Time.unscaledTime - startedAt:0.00} s (played {played}{(skipping ? ", `call next`" : "")}).");
        }

        // ---- Where the box is.

        /// <summary>HUD eye (seat eyes plus HeightOffset), anchor space.</summary>
        private static Vector3 EyeInAnchor => Quaternion.Inverse(CameraDriver.AnchorRotation) * (VRUI.HudEye - CameraDriver.AnchorPosition);

        /// <summary>Degrees from straight ahead of the seat on the windscreen, else around the cone centre.</summary>
        private static Rect Absolute(Rect deg, Transform quad)
        {
            if (!HudLayout.OnWindshield) return deg;
            Vector3 v = Quaternion.Inverse(CameraDriver.AnchorRotation) * (quad.position - VRUI.HudEye);
            return new Rect(Azimuth(v) - deg.width * 0.5f, Elevation(v) - deg.height * 0.5f, deg.width, deg.height);
        }

        private static float Azimuth(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        private static float Elevation(Vector3 v) => Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;

        private static string Describe(Rect r) =>
            $"x {r.xMin:0.0}..{r.xMax:0.0}, y {r.yMin:0.0}..{r.yMax:0.0} deg " + (HudLayout.OnWindshield ? "from straight ahead of the seat" : "around the cone's centre, 3 below the gaze");

        private static string Check(string label)
        {
            var g = Global.Instance;
            var b = Read();
            bool ok;
            var sb = new StringBuilder($"call check {label}: view {CameraDriver.Mode}, HudOnWindshield {(VRConfig.HudOnWindshield.Value ? "on" : "off")}, " +
                                       $"the game's HUD {(g != null && g.optionDisableHUD ? "hidden" : "shown")}, the HUD {(HudLayout.OnWindshield ? "on the windscreen" : "around the gaze")}");
            if (g == null) return sb.Append(": no game -> call check FAILED").ToString();
            var d = g.dialogUI;
            var c = g.incomingCallUI;
            var why = new List<string>();
            sb.Append("; call: ").Append(Box("Call", b.CallUp, b.HudCall, b.GameCall, c != null ? c.interiorCanvasGroupGlobal : null, c != null ? c.nameObj : null, why));
            sb.Append("; dialogue: ").Append(Box("Dialogue", b.DialogUp, b.HudDialog, b.GameDialog, d != null ? d.interiorCanvasGroupGlobal : null, d != null ? d.textField : null, why));
            bool wrong = HudWanted ? b.Game : b.Hud;
            if (Film.CleanFrame.HidesDialogue)
            {
                sb.Append($"; boxes in view: {b.Count} ({Names(b)}), none wanted (the clean frame hides the line)");
                if (b.Count != 0) why.Add(b.Count == 1 ? "a box in view while the clean frame hides the line" : "two boxes in view while the clean frame hides the line");
            }
            else
            {
                sb.Append($"; boxes in view: {b.Count} ({Names(b)}), {(HudWanted ? "the HUD's" : "the game's on the dashboard")} wanted");
                if (b.Count != 1) why.Add(b.Count == 0 ? "no box in view" : "two boxes in view");
                if (wrong) why.Add("the wrong box");
            }
            ok = why.Count == 0;
            sb.Append(ok ? " -> call check ok" : " -> call check FAILED: " + string.Join(", ", why));
            string line = sb.ToString();
            if (ok) Plugin.L.LogInfo(line);
            else Plugin.L.LogWarning(line);
            return "logged";
        }

        private static string Box(string kind, bool up, bool hud, bool game, CanvasGroup cockpit, TMP_Text words, List<string> why)
        {
            var sb = new StringBuilder(up ? "" : "none up now; ");
            if (hud && HudLayout.Where(kind, out var deg, out var quad, out float perPixel, out _))
            {
                var abs = Absolute(deg, quad);
                Vector3 v = Quaternion.Inverse(CameraDriver.HeadWorldRotation) * (quad.position - CameraDriver.HeadWorldPosition);
                float yaw = Azimuth(v), pitch = Elevation(v);
                sb.Append($"the HUD's box in view at {Describe(abs)}; from the gaze {yaw - deg.width * 0.5f:0.0}..{yaw + deg.width * 0.5f:0.0} right, " +
                          $"{pitch - deg.height * 0.5f:0.0}..{pitch + deg.height * 0.5f:0.0} up; {v.magnitude:0.00} m from the eyes; {perPixel:0.000} deg per pixel");
                float px = FontPixels(words);
                if (px > 0f) sb.Append($", its {(kind == "Call" ? "name" : "words")} {px:0} px = {px * perPixel:0.00} deg tall");
                if (HudLayout.OnWindshield && CameraDriver.Mode == ViewMode.Cockpit) sb.Append("; ").Append(Room(abs, why));
            }
            else sb.Append("the HUD's box not in view");
            if (cockpit == null) return sb.Append("; no box on the dashboard").ToString();
            string state = CockpitBoxes.Hidden(cockpit) ? "hidden by the mod" : game ? "in view" : CameraDriver.Mode == ViewMode.Cockpit ? "not shown" : "out of view (not the cockpit view)";
            sb.Append($"; the game's '{cockpit.name}' on the dashboard {state} (alpha {cockpit.alpha:0.00})");
            if (CameraDriver.Mode == ViewMode.Cockpit) sb.Append(", its frame ").Append(InCockpit(cockpit.GetComponent<RectTransform>()));
            return sb.ToString();
        }

        /// <summary>Degrees from straight ahead of the seat, and nearest distance.</summary>
        private static string InCockpit(RectTransform rt)
        {
            if (rt == null) return "unknown";
            var r = rt.rect;
            Vector3 eye = EyeInAnchor;
            float x0 = 180f, x1 = -180f, y0 = 90f, y1 = -90f, near = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                Vector3 v = CameraDriver.CockpitToAnchorSpace(rt.TransformPoint(new Vector3((k & 1) != 0 ? r.xMax : r.xMin, (k & 2) != 0 ? r.yMax : r.yMin, 0f))) - eye;
                x0 = Mathf.Min(x0, Azimuth(v)); x1 = Mathf.Max(x1, Azimuth(v)); y0 = Mathf.Min(y0, Elevation(v)); y1 = Mathf.Max(y1, Elevation(v));
                near = Mathf.Min(near, v.magnitude);
            }
            return $"at x {x0:0.0}..{x1:0.0}, y {y0:0.0}..{y1:0.0} deg from straight ahead of the seat, {near:0.00} m from the eyes at its nearest";
        }

        /// <summary>Font height in UI pixels.</summary>
        private static float FontPixels(TMP_Text text)
        {
            var cam = CameraDriver.UiCamera;
            if (text == null || cam == null) return 0f;
            var rt = text.rectTransform;
            float a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(Vector3.zero)).y;
            float b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(0f, text.m_fontSize, 0f))).y;
            return Mathf.Abs(b - a);
        }

        /// <summary>Room around the box: wheel top below it, nothing of the cockpit behind it (CabinShape rays).</summary>
        private static string Room(Rect abs, List<string> why)
        {
            var sb = new StringBuilder("room: ");
            Vector3 eye = EyeInAnchor;
            float wheel = WheelTop(eye, out string how);
            if (float.IsNaN(wheel)) sb.Append("the wheel not found");
            else
            {
                sb.Append($"the wheel's top {wheel:0.0} deg ({how}), {abs.yMin - wheel:0.0} under the box");
                if (wheel >= abs.yMin) why.Add("the wheel's top behind the box");
            }
            if (!CabinShape.Ready) return sb.Append("; no cabin shape yet (built with the coffee's place): the dashboard not measured").ToString();
            float least = float.MaxValue, lowTop = float.MaxValue, highTop = float.MinValue;
            string covered = null;
            const int Rays = 9;
            for (int i = 0; i < Rays; i++)
            {
                float az = Mathf.Lerp(abs.xMin, abs.xMax, i / (Rays - 1f));
                for (float el = abs.yMax; el > abs.yMin - 30f; el -= 0.25f)
                {
                    float hit = Ray(eye, az, el, out _);
                    if (float.IsNaN(hit)) continue;
                    if (el >= abs.yMin) covered ??= $"at {az:0.0} right, {el:0.0} up, {hit:0.00} m away";
                    else { least = Mathf.Min(least, abs.yMin - el); lowTop = Mathf.Min(lowTop, el); highTop = Mathf.Max(highTop, el); }
                    break;
                }
            }
            if (covered != null) { why.Add("the cockpit behind the box"); sb.Append("; the cockpit behind the box ").Append(covered); }
            else sb.Append("; nothing of the cockpit behind the box");
            if (least < float.MaxValue) sb.Append($"; the cockpit's top under it {lowTop:0.0}..{highTop:0.0} deg, {least:0.0} under the box at the least");
            Ray(eye, abs.center.x, abs.center.y, out float glass);
            if (!float.IsNaN(glass)) sb.Append($"; the windscreen {glass:0.00} m along its middle");
            if (Mathf.Abs(eye.y) > 0.001f) sb.Append($"; seen from {eye.y * 100f:0} cm above the seat's eyes (HeightOffset)");
            return sb.ToString();
        }

        /// <summary>
        /// Distance to the first solid cabin cell along (az, el). NaN through glass (distance in glass) or past 1.8 m.
        /// </summary>
        private static float Ray(Vector3 eye, float az, float el, out float glass)
        {
            glass = float.NaN;
            float ca = Mathf.Cos(el * Mathf.Deg2Rad);
            float dx = ca * Mathf.Sin(az * Mathf.Deg2Rad), dy = Mathf.Sin(el * Mathf.Deg2Rad), dz = ca * Mathf.Cos(az * Mathf.Deg2Rad);
            for (float t = 0.12f; t < 1.8f; t += 0.005f)
            {
                float y = eye.y + dy * t;
                if (y < CabinShape.Low || y > CabinShape.High) break;
                int col = CabinShape.Column(eye.x + dx * t, eye.z + dz * t);
                if (col < 0) break;
                if (!CabinShape.Solid(col, y, out _, out _, out int tag)) continue;
                if (tag != CabinShape.Set) { glass = t; return float.NaN; }
                return t;
            }
            return float.NaN;
        }

        /// <summary>Degrees up to the wheel's top: rim if known, else bounds.</summary>
        private static float WheelTop(Vector3 eye, out string how)
        {
            how = "";
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            var wheel = vi != null ? vi.steeringWheel : null;
            if (wheel == null) return float.NaN;
            if (Input.WheelModel.Ready)
            {
                Input.WheelModel.Shape(wheel, out var hub, out var axis, out _, out _);
                // hub, then up in the wheel plane to the outer rim
                Vector3 hubA = CameraDriver.CockpitToAnchorSpace(hub), axisA = CameraDriver.CockpitToAnchorSpace(hub + axis) - hubA;
                Vector3 inPlane = Vector3.ProjectOnPlane(Vector3.up, axisA);
                if (inPlane.sqrMagnitude > 1e-6f)
                {
                    how = "its rim";
                    return Elevation(hubA + inPlane.normalized * (Input.WheelModel.RimRadius + Input.WheelModel.TubeRadius) - eye);
                }
            }
            var renderers = wheel.GetComponentsInChildren<Renderer>(false);
            Bounds box = default;
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled) continue;
                if (!any) { box = r.bounds; any = true; }
                else box.Encapsulate(r.bounds);
            }
            if (!any) return float.NaN;
            float best = -90f;
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) != 0 ? box.max.x : box.min.x, (k & 2) != 0 ? box.max.y : box.min.y, (k & 4) != 0 ? box.max.z : box.min.z);
                best = Mathf.Max(best, Elevation(CameraDriver.CockpitToAnchorSpace(corner) - eye));
            }
            how = "its box, at most";
            return best;
        }
    }

    // logs line starts and continues while a call is watched
    [HarmonyPatch(typeof(DialogHUD), nameof(DialogHUD._TeletypeRoutine))]
    internal static class CallProbeTypingPatch
    {
        private static void Prefix(int lineIndex)
        {
            try { CallProbe.NoteTyping(lineIndex); }
            catch (Exception) { /* log only */ }
        }
    }

    [HarmonyPatch(typeof(DialogHUD), nameof(DialogHUD.OnContinue))]
    internal static class CallProbeContinuePatch
    {
        private static void Prefix(DialogHUD __instance)
        {
            try { CallProbe.NoteContinue(__instance); }
            catch (Exception) { /* log only */ }
        }
    }
}
#endif
