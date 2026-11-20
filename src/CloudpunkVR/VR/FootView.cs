using System;
using Cinemachine;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// First person on foot (FirstPersonOnFoot). The game has three on-foot cameras (Player): fixed behind Rania,
    /// orbiting once unlocked (CameraUnlock: Square, keyboard X), and first person, which is the unlocked camera zoomed
    /// all the way in (EnableCamera picks it under zoom 0.41; zoom runs 0.4 to 2). A new game starts behind her.
    /// We set unlocked + zoom 0.4 and let the game's EnableCamera switch, as a cut. Waits while another camera shows,
    /// while the game holds its camera lock (Level_Lift, scene 14, locks it until the next load; scenes can too) and
    /// wherever the game's own camera input waits. A camera volume's override shot counts as hers. Put back behind her
    /// 3 times within 2 s of a switch: left alone until its state changes, she gets out, or once after 20 s.
    ///
    /// The camera tutorials end by themselves: the first waits for the fixed camera to go off, the zoom one for the
    /// orbiting one. While the first shows with the fixed camera still on, Square goes to the game (InputBridge) and
    /// lands in first person. Setting off: the cameras are left alone and Square is the game's unlock, as on a pad.
    ///
    /// The game saves its camera: a save made in VR starts in first person in the flat game too (X puts it back).
    /// </summary>
    internal static partial class FootView
    {
        private const float FirstPersonZoom = 0.4f;   // the game's closest zoom
        private const float FirstPersonBelow = 0.41f; // EnableCamera: unlocked and under this zoom is first person
        private const float RevertWindow = 2f, RetryAfter = 20f, CutWindow = 2f, WaitNoteAfter = 0.5f;
        private const int MaxReverts = 3;

        // const strings, compared by reference
        private const string WaitLock = "the game holds its camera lock (a scene, or Level_Lift until the next load)",
            WaitOtherCamera = "a camera that is not Rania's shows (a scene's own shot)";

        /// <summary>Square goes to the game as its camera unlock (camera tutorial up, fixed camera on).</summary>
        public static bool TutorialTakesSquare { get; private set; }

#if DEV
        /// <summary>Dev command `footview hold`: no switch for the run.</summary>
        public static bool Hold;
#else
        private const bool Hold = false;
#endif

        private static bool onFoot, leftAlone, timedRetryDone, lastFailed;
        private static int switchNo, failures, reverts, leftAloneState, logs, errors, zoomBacks;
        private static float lastSwitchAt = -100f, leftAloneAt, fightUntil, zoomNotedAt = -100f;
        private static string waitingFor;
        private static float waitingSince;
        private static bool waitNoted;

        // dev command labels, added to the log lines they lead to
        private static string devTag, fightTag, tutorialTag;

        // the game's on-foot cameras, cached per Rania
        private static IntPtr camerasOf, fixedPtr, orbitPtr;
        private static GameObject fixedGo, orbitGo, firstGo;

        public static void Update()
        {
            TutorialTakesSquare = false;
            try { Run(); }
            catch (Exception e) { if (errors++ < 5) Plugin.L.LogWarning("On foot view: " + e); }
        }

        private static void Run()
        {
            var g = Global.Instance;
            var p = g != null && CameraDriver.Mode == ViewMode.OnFoot ? Player.Instance : null;
            if (p == null || !p.isActiveAndEnabled) { LeaveFoot(); return; }
            onFoot = true;
            float now = Time.unscaledTime;
            RestoreBlend(p, false);
            WatchTutorials(g, p);
            if (!VRConfig.FirstPersonOnFoot.Value) { leftAlone = false; waitingFor = null; return; } // game's cameras untouched

            bool unlocked = p.FreeLook;
            if (unlocked && p.intendedZoomFactor < FirstPersonBelow)
            {
                leftAlone = timedRetryDone = false;
                waitingFor = null;
                if (now >= fightUntil) fightTag = null;
                return;
            }
            bool locked = p.cameraLocked;
            TutorialTakesSquare = cameraUp && !unlocked && !locked;
            if (TutorialTakesSquare && p.intendedZoomFactor >= FirstPersonBelow)
                p.intendedZoomFactor = FirstPersonZoom; // so the unlock lands in first person, not orbiting
            if (Hold || Busy(g, p)) return;
            if (locked) { Wait(p, WaitLock, now); return; }
            if (!RaniasCameraShows(g, p)) { Wait(p, WaitOtherCamera, now); return; }
            waitingFor = null;

            int state = State(p);
            if (leftAlone)
            {
                bool changed = state != leftAloneState;
                if (!changed && (timedRetryDone || now - leftAloneAt < RetryAfter)) return;
                if (!changed) timedRetryDone = true;
                leftAlone = false;
                reverts = 0;
                Log("On foot: trying the first-person view again (" + (changed ? "the game's camera changed" : $"{RetryAfter:0} s on") + ")" + Tag(fightTag) + ".");
            }
            else if (!unlocked || lastFailed)
            {
                // back behind her: a script, or the player's X. A plain zoom out (still unlocked) doesn't count.
                reverts = now - lastSwitchAt < RevertWindow ? reverts + 1 : 0;
                if (reverts >= MaxReverts)
                {
                    leftAlone = true;
                    leftAloneAt = now;
                    leftAloneState = state;
                    if (logs++ < 30)
                        Plugin.L.LogWarning($"On foot: the game put its camera back {reverts} times within {RevertWindow:0} s of a switch{Tag(fightTag)}; " +
                                            $"leaving it {Describe(p)} until it changes or Rania gets out again" + (timedRetryDone ? "." : $" (one more try in {RetryAfter:0} s)."));
                    return;
                }
            }
            Switch(g, p, now, unlocked);
            if (p.FreeLook) TutorialTakesSquare = false; // unlocked now: a Square this frame would lock it again
        }

        /// <summary>
        /// Where the game's Player.Update leaves its camera alone: no minimap yet (EnableCamera and HasBigWindowOpen
        /// read it), loading, teleport, menus, big windows, credits, arcade games.
        /// </summary>
        private static bool Busy(Global g, Player p)
        {
            if (Minimap.Instance == null) return true;
            bool loading;
            try { loading = SceneLoader.IsLoading(); }
            catch (Exception) { loading = false; }
            return loading || p.isTeleporting || g.IsInMenu() || g.HasBigWindowOpen() || Global.CreditsRunning || MinigameInteractableItem.MinigameActive;
        }

        /// <summary>
        /// Cinemachine shows Rania's fixed or orbiting camera. With the fixed one, a camera volume's override shot counts
        /// too: EnableCamera turns it on with the fixed camera and off once unlocked.
        /// </summary>
        private static bool RaniasCameraShows(Global g, Player p)
        {
            var brain = g.cameraBrain;
            var live = brain != null ? brain.ActiveVirtualCamera : null;
            if (live == null) return false;
            Cameras(p);
            IntPtr ptr = live.Pointer;
            if (ptr == fixedPtr || ptr == orbitPtr) return true;
            if (p.FreeLook) return false;
            var volume = p.currentVolume;
            var shot = volume != null ? volume.overrideCamera : null;
            return shot != null && ptr == shot.Pointer;
        }

        /// <summary>Logged once per wait, and only if it lasts (Cinemachine takes a frame to follow a switch).</summary>
        private static void Wait(Player p, string why, float now)
        {
            if (!ReferenceEquals(why, waitingFor)) { waitingFor = why; waitingSince = now; waitNoted = false; }
            if (waitNoted || now - waitingSince < WaitNoteAfter) return;
            waitNoted = true;
            Log($"On foot: the game's camera is {Describe(p)}; the first-person view waits: {why}.");
        }

        private static int State(Player p) => (p.FreeLook ? 1 : 0) | (p.cameraLocked ? 2 : 0) | (p.intendedZoomFactor < FirstPersonBelow ? 4 : 0);

        private static void Switch(Global g, Player p, float now, bool zoomOnly)
        {
            int n = ++switchNo;
            string was = Describe(p);
            bool wasUnlocked = p.FreeLook;
            float wasZoom = p.intendedZoomFactor;
            Cameras(p);
            string failure = null, cut = null;
            try
            {
                cut = CutNextBlend(g);
                p.intendedZoomFactor = FirstPersonZoom;
                p.FreeLook = true;
                p.EnableCamera();
                if (!Active(firstGo)) failure = "its first-person camera stayed off";
            }
            catch (Exception e) { failure = "the game's EnableCamera failed (" + e.Message + ")"; }
            if (failure != null)
            {
                // put its state and cameras back, so the next try starts clean
                try
                {
                    p.FreeLook = wasUnlocked;
                    p.intendedZoomFactor = wasZoom;
                    p.EnableCamera();
                }
                catch (Exception e) { failure += "; putting its camera back failed too (" + e.Message + ")"; }
            }
            lastSwitchAt = now;
            lastFailed = failure != null;
            if (lastFailed) failures++;
            string tag = Tag(devTag ?? fightTag);
            devTag = null;
            if (zoomOnly && failure == null)
            {
                // the mouse wheel zooms out a notch at a time: don't log every one
                zoomBacks++;
                if (now - zoomNotedAt < 5f && tag.Length == 0) return;
                zoomNotedAt = now;
            }
            if (logs++ >= 30) return;
            string line = $"On foot: switch #{n}{tag}: the game's camera was {was}" + (zoomOnly ? $" (zoomed out, still unlocked: not counted as put back; {zoomBacks} so far)" : "") + "; ";
            if (failure != null) Plugin.L.LogWarning(line + "the switch to first person failed: " + failure + "; its camera is back as it was.");
            else Plugin.L.LogInfo(line + "switched to its first-person view (unlocked, zoom 0.40)" + (cut != null ? ", " + cut : "") + ".");
        }

        private static string Tag(string t) => t != null ? " [" + t + "]" : "";

        // ---- The switch as a cut: otherwise Cinemachine glides from behind Rania into her head.

        private static CinemachineBrain cutBrain;
        private static CinemachineBlendDefinition savedBlend;
        private static CinemachineBlenderSettings savedCustom;
        private static bool blendCut;
        private static string blendNoted;

        /// <summary>The game's blend set to a cut until the first-person camera is live (RestoreBlend).</summary>
        private static string CutNextBlend(Global g)
        {
            if (blendCut) return null;
            var brain = g.cameraBrain;
            if (brain == null) return "no camera brain to make it a cut";
            savedBlend = brain.m_DefaultBlend;
            savedCustom = brain.m_CustomBlends;
            var cut = brain.m_DefaultBlend;
            string game = $"{cut.m_Style} {cut.m_Time:0.0} s" + (savedCustom != null ? ", custom blends '" + savedCustom.name + "'" : "");
            cut.m_Style = CinemachineBlendDefinition.Style.Cut;
            cut.m_Time = 0f;
            blendCut = true; // RestoreBlend undoes it from here, whatever fails next
            cutBrain = brain;
            brain.m_DefaultBlend = cut;
            brain.m_CustomBlends = null;
            if (blendNoted == game) return "as a cut";
            blendNoted = game;
            return $"as a cut (the game's blend: {game})";
        }

        /// <summary>
        /// The game's blend back once first person is live and settled (or after 2 s, or forced), only if the brain
        /// still holds our cut: a blend the game set meanwhile stays.
        /// </summary>
        private static void RestoreBlend(Player p, bool force)
        {
            if (!blendCut) return;
            var brain = cutBrain;
            bool done = force || brain == null || Time.unscaledTime - lastSwitchAt > CutWindow;
            if (!done)
            {
                try
                {
                    var live = brain.ActiveVirtualCamera;
                    var fp = p != null ? p.firstPersonCamera : null;
                    done = live != null && fp != null && live.Pointer == fp.Pointer && !brain.IsBlending;
                }
                catch (Exception) { done = true; }
            }
            if (!done) return;
            blendCut = false;
            try
            {
                if (brain != null)
                {
                    var held = brain.m_DefaultBlend;
                    if (held.m_Style == CinemachineBlendDefinition.Style.Cut && held.m_Time == 0f && brain.m_CustomBlends == null)
                    {
                        brain.m_DefaultBlend = savedBlend;
                        brain.m_CustomBlends = savedCustom;
                    }
                    else if (logs++ < 30) Plugin.L.LogInfo("On foot: the game changed its camera blend while the switch's cut was set; left as the game set it.");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("On foot: the game's camera blend could not be put back: " + e.Message); }
            cutBrain = null;
            savedBlend = default;
            savedCustom = null;
        }

        /// <summary>VR stopped: give back the game's blend if our cut is still set.</summary>
        public static void SessionStopped()
        {
            if (!blendCut) return;
            try { RestoreBlend(null, true); }
            catch (Exception e) { if (errors++ < 5) Plugin.L.LogWarning("On foot view: " + e.Message); }
        }

        /// <summary>If the cut doesn't take, the headset stays black while Cinemachine glides in, 2 s at most (VRManager).</summary>
        public static bool Hiding
        {
            get
            {
                if (!blendCut || Time.unscaledTime - lastSwitchAt > CutWindow) return false;
                try { return cutBrain != null && cutBrain.IsBlending; }
                catch (Exception) { return false; }
            }
        }

        private static void LeaveFoot()
        {
            if (!onFoot && !blendCut) return;
            onFoot = leftAlone = timedRetryDone = lastFailed = false;
            reverts = 0;
            fightUntil = 0f;
            fightTag = null;
            waitingFor = null;
            cameraUp = zoomUp = false;
            RestoreBlend(null, true);
        }

        // ---- The game's camera tutorials.

        private static GameObject cameraPanel, zoomPanel;
        private static bool cameraUp, zoomUp;
        private static float cameraSince, zoomSince;

        private static void WatchTutorials(Global g, Player p)
        {
            if (cameraPanel == null && g.tutorialCamera != null) cameraPanel = g.tutorialCamera.gameObject;
            if (zoomPanel == null && g.tutorialCameraZoom != null) zoomPanel = g.tutorialCameraZoom.gameObject;
            if (Changed(cameraPanel, ref cameraUp, ref cameraSince, out float shown))
            {
                Note("\"unlock the camera\" step (Square)", cameraUp, shown, g.tutorialCameraTrigger, p);
                if (!cameraUp) tutorialTag = null;
            }
            if (Changed(zoomPanel, ref zoomUp, ref zoomSince, out shown))
                Note("zoom step", zoomUp, shown, g.tutorialCameraTriggerZoom, p);
        }

        private static bool Changed(GameObject panel, ref bool up, ref float since, out float shown)
        {
            bool now = panel != null && panel.activeSelf;
            shown = Time.unscaledTime - since;
            if (now == up) return false;
            up = now;
            if (now) since = Time.unscaledTime;
            return true;
        }

        private static void Note(string what, bool up, float shown, StoryTrigger trigger, Player p)
        {
            string head = "Camera tutorial" + Tag(tutorialTag);
            if (up) Plugin.L.LogInfo($"{head}: the game shows its {what}; its on-foot camera is {Describe(p)}.");
            else Plugin.L.LogInfo($"{head}: its {what} is over after {shown:0.0} s (trigger {Trigger(trigger)}); the camera is {Describe(p)}.");
        }

        /// <summary>InputBridge passed Square to the game for the camera tutorial.</summary>
        public static void NoteTutorialSquare()
        {
            var p = Player.Instance;
            Plugin.L.LogInfo($"Camera tutorial{Tag(tutorialTag)}: Square goes to the game as its camera unlock (Action3); the camera is {Describe(p)}, so it lands in first person.");
        }

        // ---- Description.

        private static void Cameras(Player p)
        {
            if (p.Pointer == camerasOf && fixedGo != null && orbitGo != null && firstGo != null) return;
            camerasOf = p.Pointer;
            var f = p.fixedCamera;
            var o = p.freeLookCamera;
            var fp = p.firstPersonCamera;
            fixedPtr = f != null ? f.Pointer : IntPtr.Zero;
            orbitPtr = o != null ? o.Pointer : IntPtr.Zero;
            fixedGo = f != null ? f.gameObject : null;
            orbitGo = o != null ? o.gameObject : null;
            firstGo = fp != null ? fp.gameObject : null;
        }

        private static bool Active(GameObject go) => go != null && go.activeSelf;

        public static string Describe(Player p)
        {
            if (p == null) return "unknown (no Rania)";
            bool unlocked = p.FreeLook;
            float zoom = p.intendedZoomFactor;
            string kind = !unlocked ? "the fixed one behind Rania" : zoom < FirstPersonBelow ? "first person" : "the free one orbiting Rania";
            return $"{kind} (unlocked {unlocked}, zoom {zoom:0.00}{(p.cameraLocked ? ", locked" : "")})";
        }

        private static string Trigger(StoryTrigger t)
        {
            if (t == null) return "not found yet";
            try { return t.IsUnlocked() ? "unlocked" : "locked"; }
            catch (Exception) { return "?"; }
        }

        private static void Log(string line)
        {
            if (logs++ < 30) Plugin.L.LogInfo(line);
        }
    }
}
