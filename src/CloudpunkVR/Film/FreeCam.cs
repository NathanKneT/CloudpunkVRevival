using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Bearroll;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Cinemachine;
using RedAlliance.Util;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Flat-only free camera for filming. Moves the game's main camera, so culling, LODs, fog and post follow it.
    /// Cinemachine, cockpit draw, HUD, prompts and occlusion culling (pops on fast moves) are off until exit.
    /// On the main menu it takes MenuCamera instead (its brain off, the menu's input held, see MenuInput), and a take
    /// can relight the sign at a set time (CameraPath.RelightAt).
    /// </summary>
    public partial class FreeCam : MonoBehaviour
    {
        public FreeCam(IntPtr ptr) : base(ptr) { }

        internal const float SlowestSlow = 0.05f;
        private static readonly float[] SlowSteps = { 0.05f, 0.1f, 0.25f, 0.5f, 0.75f, 1f };
        private const string Hints = "Mouse and WASD / ZQSD fly, Space or E / C up and down, Shift / Ctrl faster or slower, wheel zoom. Tab gives the keys, Esc too, to the game.\n" +
                                     "F7 keyframe, Backspace removes it, F8 play, F5 follow, PgDn / PgUp slow motion, Home / End length, 1-9 load, Ctrl + 1-9 save, F6 exit.";
        private const string BeforeLoad = "before-load";

        private static FreeCam instance;
        public static bool On { get; private set; }

        private static bool cameraKeys = true;      // keys fly the camera, else they go to the game
        private static ShotPose pose;               // world space, or the followed HOVA's
        private static Vector3 flyVelocity;
        private static float gameFov = 60f;
        private static CameraPath path;
        private static Take take;
        private static int takes;
        private static bool keysBeforeTake;
        private static int errors;
        private static string pendingLoad;          // load over unsaved keys: a second press confirms
        private static float pendingLoadUntil;
        private static bool menuMode;               // the main menu's camera, not the game's
        private static bool menuLeaving;            // the game is leaving the main menu
        private static float menuBackSince = -1f;   // real time the menu showed again while leaving
        private static bool stills;                 // keyframe stills running (FreeCam.Dev)

        // follow: smoothed anchor on the HOVA (or Rania on foot)
        private static bool following;
        private static Transform target;
        private static string targetName;
        private static Vector3 anchorPos, anchorVel;
        private static float anchorYaw, anchorYawVel;

        // the ...Off flags are only set if we switched it off
        private static Camera cam;
        private static Transform camT;
        private static IntPtr camPtr;
        private static CinemachineBrain brain;
        private static bool brainOff, interiorOff, gdocOff, uiHidden, promptsOff;
        private static int brainFights, poseFights;
        private static MainCameraVehicleRenderer interiorDraw;
        private static Camera uiCam;
        private static int uiMask;
        private static int promptsBit;
        private static GDOC gdoc;
        private static bool gdocWasOn;
        private static readonly Dictionary<IntPtr, (GPUInstancer.GPUInstancerManager m, bool was)> instancers = new Dictionary<IntPtr, (GPUInstancer.GPUInstancerManager, bool)>();
        private static float nextInstancerPass, nextHookCheck, nextActionsCheck;
        private static PlayerActions actions;
        private static bool actionsBlocked, cursorTaken, cursorVisible, cullHookFailed;
        private static CursorLockMode cursorLock;
        private static Vector3 written;
        private static Quaternion writtenRot = Quaternion.identity;

        // slow motion: the game's time scale and physics step, to restore
        private static bool slowApplied, timePending;
        private static float gameScale = 1f, appliedScale = 1f, baseFixedDelta = 0.02f;

        // mouse axes: InControl's "mouse x" (raw counts) or Unity's "Mouse X" (a tenth)
        private static string mouseX, mouseY;
        private static float mouseScale;
        private static bool mouseChecked;

#if DEV
        // developer command overrides
        internal static float[] GrabTimes;
        internal static bool Probe;
#else
        private static readonly float[] GrabTimes;
        private const bool Probe = false;
#endif

        private static string ShotsDir => Path.Combine(BepInEx.Paths.ConfigPath, "CloudpunkVR-shots");

        private void Awake() => instance = this;

        private void Update()
        {
            try
            {
                var g = Global.Instance;
                PutTimeBack(g);
                bool keys = VRConfig.FreeCamEnabled.Value;
                // F6 always exits, even with keys disabled
                if ((keys || On) && Down(KeyCode.F6))
                {
                    if (On) TurnOff("F6");
                    else TurnOn();
                }
                if (!On) { FreeCamNote.Update(false); return; }

                bool gone = menuMode ? !CleanFrame.MainMenuUp() : g == null || g.mainCamera == null || g.mainCamera.Pointer != camPtr;
                if (gone || cam == null || !cam.isActiveAndEnabled)
                {
                    TurnOff(menuMode ? "the main menu went away" : "the game's camera changed");
                    return;
                }
                Ui(g, out bool loading, out bool stopsTakes, out bool gameUi);
                if (stopsTakes && take != null) EndTake(menuMode ? "the game is leaving the main menu" : "the game's menu, map, a window or a loading screen came up");
                if (stopsTakes && stills) StopStills(menuMode ? "the game is leaving the main menu" : "the game's menu, map, a window or a loading screen came up");
                if (Time.unscaledTime > nextHookCheck) { nextHookCheck = Time.unscaledTime + 2f; HookCull(); }

                Hold(gameUi, keys, loading);
                ApplySlow(gameUi, loading);
                if (keys && !gameUi && !stills) Keys();
                Follow(Time.deltaTime);

                ShotPose shown = pose;
                if (take != null)
                {
                    if (take.Done) EndTake(null);
                    else
                    {
                        float x = take.Step();
                        if (take.Path.Keys.Count > 0) shown = pose = take.Path.Evaluate(x);
                        DriveRelight(take);
                    }
                }
                else if (stills) StillsFrame(ref shown);
                else if (keys && cameraKeys && !gameUi) { Fly(Time.unscaledDeltaTime); shown = pose; }
                Write(shown);
                FreeCamNote.Update(take != null || stills);
            }
            catch (Exception e)
            {
                if (errors++ < 20) Plugin.L.LogError("Free camera: " + e);
            }
        }

        private static bool Down(KeyCode k) => UnityEngine.Input.GetKeyDown(k);
        private static bool Held(KeyCode k) => UnityEngine.Input.GetKey(k);

        /// <summary>Menus, map, windows and loading stop a take and give back HUD and keys.</summary>
        private static void Ui(Global g, out bool loading, out bool stopsTakes, out bool gameUi)
        {
            if (menuMode)
            {
                // only leaving the menu stops a take. Not IsLoading: it stays up the whole time the main menu is.
                // The fade ends before the menu goes, so once seen, leaving sticks until the camera goes with it.
                if (!menuLeaving && CleanFrame.MenuLeaving() != null)
                {
                    menuLeaving = true;
                    menuBackSince = -1f;
                    MenuInput.NoteLeaving();
                    Plugin.L.LogInfo("freecam: the game is leaving the main menu: no take there any more, the menu's input is the game's.");
                }
                else if (menuLeaving)
                {
                    // back without leaving (the credits): a second of menu on screen re-enables takes
                    if (!CleanFrame.MenuInView()) menuBackSince = -1f;
                    else if (menuBackSince < 0f) menuBackSince = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - menuBackSince > 1f)
                    {
                        menuLeaving = false;
                        menuBackSince = -1f;
                        Plugin.L.LogInfo("freecam: the main menu is back on screen (the game did not leave it): takes work there again.");
                    }
                }
                loading = menuLeaving;
                stopsTakes = loading;
                gameUi = false;
                return;
            }
            loading = SceneLoader.IsLoading();
            stopsTakes = loading || g.IsInMenu() || Global.FullMapOpen || g.HasBigWindowOpen();
            gameUi = stopsTakes || (take == null && g.IsInDialog(false));
        }

        // ---- On and off.

        private static bool TurnOn()
        {
            var g = Global.Instance;
            bool menu = CleanFrame.MainMenuUp();
            Camera c = null;
            if (menu) c = g.menuCamera.GetComponent<Camera>();
            else if (g != null && !g.IsInMenu()) c = g.mainCamera;
            if (c == null || !c.isActiveAndEnabled)
            {
                Say(g != null && !menu && g.IsInMenu() ? "Close the game's menu first." : "No camera to take yet: load a game, or wait for the main menu.");
                Plugin.L.LogInfo("freecam: not on, no game camera yet (loading, the pause menu open, or the main menu not up yet).");
                return false;
            }

            // read before changing anything
            menuMode = menu;
            menuLeaving = false;
            menuBackSince = -1f;
            cam = c;
            camT = cam.transform;
            camPtr = cam.Pointer;
            brain = cam.GetComponent<CinemachineBrain>();
            // main menu: only the camera, no cockpit, HUD, prompts or GDOC to hold
            interiorDraw = menu ? null : cam.GetComponent<MainCameraVehicleRenderer>();
            uiCam = menu ? null : g.uiCamera;
            uiMask = uiCam != null ? uiCam.cullingMask : 0;
            int layer = LayerMask.NameToLayer("Text");
            promptsBit = !menu && layer >= 0 ? 1 << layer : 0;
            gdoc = menu ? null : GDOC.instance;
            gdocWasOn = gdoc != null && gdoc.enabled;
            bool brainWasOn = brain != null && brain.enabled, interiorWasOn = interiorDraw != null && interiorDraw.enabled;
            gameFov = cam.fieldOfView;
            pose = ShotPose.From(camT.position, camT.rotation, gameFov);
            pose.Roll = 0f; // level horizon
            flyVelocity = Vector3.zero;
            following = false;
            cameraKeys = true;
            brainOff = interiorOff = gdocOff = uiHidden = promptsOff = false;
            brainFights = poseFights = 0;
            nextInstancerPass = 0f;
            EnsurePath();

            // Hold re-applies these every frame, TurnOff undoes whatever got done
            On = true;
            try
            {
                Ui(g, out bool loading, out _, out bool gameUi);
                Hold(gameUi, VRConfig.FreeCamEnabled.Value, loading);
            }
            catch (Exception e) { Plugin.L.LogWarning("freecam: not all taken over at once (kept trying every frame): " + e.Message); }
            HookCull();
            nextHookCheck = Time.unscaledTime + 2f;

            if (menu)
            {
                Plugin.L.LogInfo($"freecam: on, the main menu's camera '{cam.name}' taken at {pose}: Cinemachine {(brain != null ? (brainWasOn ? "off (was on: its glide to the sign stops)" : "already off") : "not found")}; " +
                                 $"the menu left as it is (F2 hides it), {MenuInput.Describe()}; {SignFromHere()}; " +
                                 $"keys {(VRConfig.FreeCamEnabled.Value ? "fly the camera (Tab gives them to the menu)" : "off ([FreeCam] Enabled = false), developer commands only")}; path: {DescribePath()}.");
                Say("Free camera on the main menu (F2 hides the menu). " + Hints);
                return true;
            }
            Plugin.L.LogInfo($"freecam: on, the game's camera '{cam.name}' taken at {pose}: Cinemachine {(brain != null ? (brainWasOn ? "off (was on)" : "already off") : "not found")}, " +
                             $"cockpit interior draw {(interiorDraw != null ? (interiorWasOn ? "off (was on)" : "already off") : "not found")}, HUD hidden (UI camera '{(uiCam != null ? uiCam.name : "none")}' draws nothing), " +
                             $"{Prompts()}; {Occlusion()}; keys {(VRConfig.FreeCamEnabled.Value ? "fly the camera (Tab gives them to the game)" : "off ([FreeCam] Enabled = false), developer commands only")}; " +
                             $"path: {DescribePath()}.");
            Say("Free camera on. " + Hints);
            return true;
        }

        /// <summary>Each piece separately, so one failure doesn't block the rest.</summary>
        private static void TurnOff(string why)
        {
            On = false;
            var failed = new List<string>();
            void Step(string what, Action give)
            {
                try { give(); }
                catch (Exception e) { failed.Add(what + " (" + e.Message + ")"); }
            }
            Step("the take", () => { if (take != null) EndTake("the free camera went off"); });
            Step("the stills", () => StopStills("the free camera went off"));
            string menuInput = null;
            Step("the game's keys", () =>
            {
                if (!menuMode) { BlockInput(false); return; }
                MenuInput.Want(MenuInput.FreeCamera, false);
                menuInput = MenuInput.AfterLetGo();
            });
            Step("time", RestoreTime);
            Step("Cinemachine", () => { if (brainOff && brain != null) brain.enabled = true; });
            Step("Cinemachine's camera", () =>
            {
                // vcam restarts from its target, not from where it was left
                if (!brainOff || brain == null) return;
                var live = brain.ActiveVirtualCamera;
                var vcam = live != null ? live.TryCast<CinemachineVirtualCameraBase>() : null;
                if (vcam != null) vcam.PreviousStateIsValid = false;
            });
            Step("the cockpit draw", () => { if (interiorOff && interiorDraw != null) interiorDraw.enabled = true; });
            Step("the HUD", () => { if (uiHidden && uiCam != null) uiCam.cullingMask = uiMask; });
            Step("the floating prompts", () => { if (promptsOff && cam != null) cam.cullingMask |= promptsBit; });
            Step("the field of view", () => { if (cam != null) cam.fieldOfView = gameFov; });
            Step("occlusion culling", () => { if (gdocOff) GDOC.Enable(); });
            Step("instanced props", RestoreInstancers);
            Step("the pre-cull hook", UnhookCull);
            brainOff = interiorOff = gdocOff = uiHidden = promptsOff = false;

            var c = CultureInfo.InvariantCulture;
            if (menuMode)
            {
                menuMode = false;
                menuLeaving = false;
                Plugin.L.LogInfo($"freecam: off ({why}); the main menu's camera back: Cinemachine {(brain != null && brain.enabled ? "on" : "off")} (back to the menu's own framing), " +
                                 $"field of view {gameFov.ToString("0.##", c)}, moved back before drawing {poseFights} time(s), Cinemachine switched back on {brainFights} time(s); " +
                                 $"{menuInput ?? "the menu's input as it was"}; time x{Time.timeScale.ToString("0.##", c)} (physics step {Time.fixedDeltaTime.ToString("0.####", c)} s)" +
                                 (failed.Count > 0 ? "; NOT given back: " + string.Join(", ", failed) : "") + ".");
                Say("Free camera off.", status: "FREE CAMERA OFF");
                cam = null;
                camT = null;
                camPtr = IntPtr.Zero;
                return;
            }
            Plugin.L.LogInfo($"freecam: off ({why}); the game's camera back: Cinemachine {(brain != null && brain.enabled ? "on" : "off")}, " +
                             $"HUD back (UI camera mask 0x{(uiCam != null ? uiCam.cullingMask : 0):X8}), occlusion culling {(gdoc != null && gdoc.enabled ? "on" : "off")} as the game had it, " +
                             $"game keys {(actions != null && actions.Enabled ? "on" : "off")}, time x{Time.timeScale.ToString("0.##", c)}{(timePending ? " (the game's menu holds it; put back as it closes)" : "")} " +
                             $"(physics step {Time.fixedDeltaTime.ToString("0.####", c)} s)" +
                             (failed.Count > 0 ? "; NOT given back: " + string.Join(", ", failed) : "") + ".");
            Say("Free camera off.", status: "FREE CAMERA OFF");
            cam = null;
            camT = null;
            camPtr = IntPtr.Zero;
        }

        // ---- Held every frame while on.

        private static void Hold(bool gameUi, bool keys, bool loading)
        {
            // game turned it back on: keep off, restore at the end
            if (brain != null && brain.enabled)
            {
                brain.enabled = false;
                if (brainOff && brainFights++ == 0) Plugin.L.LogWarning("freecam: something switched Cinemachine back on; kept off until the free camera stops.");
                brainOff = true;
            }
            if (interiorDraw != null && interiorDraw.enabled)
            {
                interiorDraw.enabled = false;
                interiorOff = true;
            }

            // game menus and windows still show, HUD and prompts don't
            if (uiCam != null)
            {
                int m = uiCam.cullingMask;
                if (!gameUi)
                {
                    if (m != 0) { uiMask = m; uiCam.cullingMask = 0; } // restore the game's latest mask
                    uiHidden = true;
                }
                else if (uiHidden) { uiCam.cullingMask = uiMask; uiHidden = false; }
                else uiMask = m;
            }
            if (promptsBit != 0)
            {
                int m = cam.cullingMask;
                if (!gameUi && (m & promptsBit) != 0) { cam.cullingMask = m & ~promptsBit; promptsOff = true; }
                else if (gameUi && promptsOff) { cam.cullingMask = m | promptsBit; promptsOff = false; }
            }

            if (menuMode)
            {
                // menu input held while flying or in a take, released once the game leaves the menu
                bool hold = !loading && ((keys && cameraKeys) || take != null || stills);
                MenuInput.Want(MenuInput.FreeCamera, hold, lockCursor: hold && keys && cameraKeys);
                return;
            }

            bool off = !VRConfig.FreeCamKeepOcclusion.Value;
            if (gdoc != null)
            {
                if (off && gdoc.enabled)
                {
                    // GDOC.Disable: its OnDisable unhides everything it hid
                    GDOC.Disable();
                    gdocOff = true;
                }
                else if (!off && gdocOff)
                {
                    GDOC.Enable();
                    gdocOff = false;
                }
            }
            if (off) KeepInstancersFromOcclusion();
            else RestoreInstancers();

            BlockInput(keys && cameraKeys && !gameUi && take == null);
        }

        /// <summary>GPU Instancer occlusion also uses last frame's depth: off while free. Rechecked each second.</summary>
        private static void KeepInstancersFromOcclusion()
        {
            if (Time.unscaledTime < nextInstancerPass) return;
            nextInstancerPass = Time.unscaledTime + 1f;
            var list = GPUInstancer.GPUInstancerManager.activeManagerList;
            int n = list != null ? list.Count : 0;
            for (int i = 0; i < n; i++)
            {
                var m = list[i];
                if (m == null) continue;
                if (!instancers.ContainsKey(m.Pointer)) instancers[m.Pointer] = (m, m.isOcclusionCulling);
                else if (m.isOcclusionCulling) instancers[m.Pointer] = (m, true); // switched on again by the game
                if (m.isOcclusionCulling) m.isOcclusionCulling = false;
            }
        }

        private static void RestoreInstancers()
        {
            if (instancers.Count == 0) return;
            foreach (var kv in instancers)
                if (kv.Value.m != null) kv.Value.m.isOcclusionCulling = kv.Value.was;
            instancers.Clear();
            nextInstancerPass = 0f;
        }

        private static PlayerActions Actions()
        {
            if (Time.unscaledTime >= nextActionsCheck || actions == null)
            {
                nextActionsCheck = Time.unscaledTime + 1f;
                var gi = GameInput.Instance;
                actions = gi != null ? gi.playerActions : null;
            }
            return actions;
        }

        private static void BlockInput(bool block)
        {
            var a = Actions();
            if (block)
            {
                if (a != null && a.Enabled)
                {
                    a.Enabled = false;
                    actionsBlocked = true;
                }
                if (!cursorTaken) { cursorLock = Cursor.lockState; cursorVisible = Cursor.visible; cursorTaken = true; }
                if (Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
                if (Cursor.visible) Cursor.visible = false;
                return;
            }
            if (actionsBlocked)
            {
                if (a != null && !a.Enabled) a.Enabled = true;
                actionsBlocked = false;
            }
            if (cursorTaken)
            {
                // only if still as we left it, else the game set it
                if (Cursor.lockState == CursorLockMode.Locked && !Cursor.visible)
                {
                    Cursor.lockState = cursorLock;
                    Cursor.visible = cursorVisible;
                }
                cursorTaken = false;
            }
        }

        // ---- Slow motion (physics step scaled too, for smooth motion).

        private static void ApplySlow(bool gameUi, bool loading)
        {
            if (loading) { RestoreTime(); return; } // a district loads at the game's own pace
            if (gameUi) return;                     // the game's own pause wins
            // main menu: only takes and stills are slowed
            float want = take != null ? take.Path.Slow : menuMode && !stills ? 1f : path.Slow;
            if (want < 0.999f)
            {
                if (!slowApplied)
                {
                    baseFixedDelta = Time.fixedDeltaTime;
                    if (Time.timeScale > 0f) gameScale = Time.timeScale;
                    slowApplied = true;
                }
                float scale = gameScale * want;
                if (Time.timeScale > 0f && Time.timeScale != scale) Time.timeScale = scale;
                appliedScale = scale;
                float step = baseFixedDelta * want;
                if (Time.fixedDeltaTime != step) Time.fixedDeltaTime = step;
            }
            else RestoreTime();
        }

        private static void RestoreTime()
        {
            if (!slowApplied) return;
            slowApplied = false;
            Time.fixedDeltaTime = baseFixedDelta;
            if (Time.timeScale > 0f) Time.timeScale = gameScale;
            else timePending = true; // paused by the game's menu now
        }

        /// <summary>After a pausing menu closes, puts the slowed time scale back to the game's.</summary>
        private static void PutTimeBack(Global g)
        {
            if (!timePending || g == null || g.IsInMenu() || Time.timeScale == 0f) return; // still paused
            timePending = false;
            if (slowApplied || Time.timeScale != appliedScale) return;
            Time.timeScale = gameScale;
            Plugin.L.LogInfo($"freecam: the game's menu closed and gave back the slowed time; put back to x{gameScale.ToString("0.##", CultureInfo.InvariantCulture)}.");
        }

        // ---- The pose.

        private static void Write(ShotPose local)
        {
            ToWorld(local, out var p, out var r);
            camT.SetPositionAndRotation(p, r);
            if (cam.fieldOfView != local.Fov) cam.fieldOfView = local.Fov;
            // read back: under a parent it isn't bit-identical to what was set
            written = camT.position;
            writtenRot = camT.rotation;
            take?.Record(p, r, local.Fov, following ? target : null);
        }

        private static Camera.CameraCallback cullHook;

        /// <summary>Global pre-cull callback (the VR render hook doesn't run flat).</summary>
        private static void HookCull()
        {
            try
            {
                if (cullHook == null) cullHook = (Camera.CameraCallback)new Action<Camera>(BeforeCull);
                var current = Camera.onPreCull;
                Camera.onPreCull = current == null ? cullHook : (current - cullHook) + cullHook;
            }
            catch (Exception e)
            {
                if (!cullHookFailed) Plugin.L.LogWarning("freecam: no pre-cull callback, the camera is only placed before the game's late updates: " + e.Message);
                cullHookFailed = true;
            }
        }

        private static void UnhookCull()
        {
            var current = Camera.onPreCull;
            if (current != null && cullHook != null) Camera.onPreCull = current - cullHook;
        }

        /// <summary>Before the main camera culls: puts it back if something moved it after us.</summary>
        private static void BeforeCull(Camera c)
        {
            if (!On || (object)c == null || c.Pointer != camPtr) return;
            var t = camT;
            var p = t.position;
            var r = t.rotation;
            if (p.x == written.x && p.y == written.y && p.z == written.z && r.x == writtenRot.x && r.y == writtenRot.y && r.z == writtenRot.z && r.w == writtenRot.w) return;
            t.SetPositionAndRotation(written, writtenRot);
            if (poseFights++ == 0) Plugin.L.LogWarning($"freecam: something moved the game's camera after the free camera placed it (to {p}); put back before drawing.");
        }

        private static void ToWorld(ShotPose local, out Vector3 p, out Quaternion r)
        {
            if (!following)
            {
                p = local.Position;
                r = local.Rotation;
                return;
            }
            p = anchorPos + Quaternion.Euler(0f, anchorYaw, 0f) * local.Position;
            r = Quaternion.Euler(-local.Pitch, anchorYaw + local.Yaw, local.Roll);
        }

        private static ShotPose WorldPose(ShotPose local)
        {
            if (!following) return local;
            var w = local;
            w.Position = anchorPos + Quaternion.Euler(0f, anchorYaw, 0f) * local.Position;
            w.Yaw = ShotPose.Angle(anchorYaw + local.Yaw);
            return w;
        }

        private static ShotPose LocalPose(ShotPose world)
        {
            var l = world;
            l.Position = Quaternion.Euler(0f, -anchorYaw, 0f) * (world.Position - anchorPos);
            l.Yaw = ShotPose.Angle(world.Yaw - anchorYaw);
            return l;
        }

        private static void Fly(float dt)
        {
            if (!mouseChecked) FindMouseAxes();
            float look = 0.1f * VRConfig.FreeCamLookSpeed.Value;
            if (mouseX != null)
            {
                pose.Yaw = ShotPose.Angle(pose.Yaw + UnityEngine.Input.GetAxisRaw(mouseX) * mouseScale * look);
                pose.Pitch = Math.Clamp(pose.Pitch + UnityEngine.Input.GetAxisRaw(mouseY) * mouseScale * look, -89f, 89f);
            }
            else
            {
                // no mouse axis in this build: the arrow keys look around
                float turn = 90f * dt;
                if (Held(KeyCode.LeftArrow)) pose.Yaw -= turn;
                if (Held(KeyCode.RightArrow)) pose.Yaw += turn;
                if (Held(KeyCode.UpArrow)) pose.Pitch = Math.Clamp(pose.Pitch + turn, -89f, 89f);
                if (Held(KeyCode.DownArrow)) pose.Pitch = Math.Clamp(pose.Pitch - turn, -89f, 89f);
            }
            float wheel = UnityEngine.Input.mouseScrollDelta.y;
            if (wheel != 0f) pose.Fov = Math.Clamp(pose.Fov * MathF.Pow(0.93f, wheel), 10f, 100f);
            if (UnityEngine.Input.GetMouseButtonDown(2)) pose.Fov = gameFov;

            var move = Vector3.zero;
            if (Held(KeyCode.W) || Held(KeyCode.Z)) move.z += 1f;
            if (Held(KeyCode.S)) move.z -= 1f;
            if (Held(KeyCode.D)) move.x += 1f;
            if (Held(KeyCode.A) || Held(KeyCode.Q)) move.x -= 1f;
            if (Held(KeyCode.Space) || Held(KeyCode.E)) move.y += 1f;
            if (Held(KeyCode.C)) move.y -= 1f;
            float speed = VRConfig.FreeCamMoveSpeed.Value;
            if (Held(KeyCode.LeftShift) || Held(KeyCode.RightShift)) speed *= 4f;
            if (Held(KeyCode.LeftControl) || Held(KeyCode.RightControl)) speed *= 0.25f;
            // forward along the view, strafe level, up/down straight
            Vector3 want = Quaternion.Euler(-pose.Pitch, pose.Yaw, 0f) * new Vector3(0f, 0f, move.z)
                           + Quaternion.Euler(0f, pose.Yaw, 0f) * new Vector3(move.x, 0f, 0f)
                           + new Vector3(0f, move.y, 0f);
            if (want.sqrMagnitude > 1f) want.Normalize();
            want *= speed;
            // short ease in and out, no jolts in hand-flown shots
            flyVelocity += (want - flyVelocity) * (1f - MathF.Exp(-dt / 0.12f));
            pose.Position += flyVelocity * dt;
        }

        private static void FindMouseAxes()
        {
            mouseChecked = true;
            foreach (var (x, y, scale) in new[] { ("mouse x", "mouse y", 1f), ("Mouse X", "Mouse Y", 10f) })
            {
                try
                {
                    UnityEngine.Input.GetAxisRaw(x);
                    UnityEngine.Input.GetAxisRaw(y);
                    mouseX = x; mouseY = y; mouseScale = scale;
                    Plugin.L.LogInfo($"freecam: mouse look from the input axes '{x}' / '{y}'.");
                    return;
                }
                catch (Exception) { }
            }
            Plugin.L.LogWarning("freecam: no mouse axis in this build; the arrow keys look around instead.");
        }

        // ---- Following the HOVA.

        private static bool FindTarget(out Transform t, out string name)
        {
            t = null; name = null;
            var g = Global.Instance;
            if (g == null) return false;
            if (Global.IsInVehicle && g.playerCar != null) { t = g.playerCar.transform; name = "the HOVA"; return true; }
            if (g.player != null) { t = g.player.transform; name = "Rania"; return true; }
            return false;
        }

        private static float YawOf(Transform t)
        {
            var f = t.forward;
            return MathF.Atan2(f.x, f.z) * (180f / MathF.PI);
        }

        private static bool StartFollow()
        {
            if (menuMode || !FindTarget(out var t, out var name)) return false; // nothing to follow on the main menu
            var world = WorldPose(pose);
            target = t;
            targetName = name;
            anchorPos = t.position;
            anchorYaw = YawOf(t);
            anchorVel = Vector3.zero;
            anchorYawVel = 0f;
            following = true;
            pose = LocalPose(world);
            Plugin.L.LogInfo($"freecam: following {name}, the camera {pose.Position.magnitude.ToString("0.0", CultureInfo.InvariantCulture)} m from it (smoothing {VRConfig.FreeCamFollowSmoothing.Value.ToString("0.##", CultureInfo.InvariantCulture)} s).");
            return true;
        }

        private static void StopFollow()
        {
            if (!following) return;
            pose = WorldPose(pose);
            following = false;
            Plugin.L.LogInfo("freecam: not following any more; the camera stays where it is.");
        }

        /// <summary>Critically damped spring in game time, so slow motion slows the follow too.</summary>
        private static void Follow(float dt)
        {
            if (!following) return;
            if (!FindTarget(out var t, out var name))
            {
                StopFollow();
                Say("Nothing to follow any more.");
                return;
            }
            if (t.Pointer != target.Pointer)
            {
                target = t;
                targetName = name;
                Plugin.L.LogInfo($"freecam: now following {name}.");
            }
            Vector3 tp = t.position;
            float ty = anchorYaw + ShotPose.Angle(YawOf(t) - anchorYaw);
            float smooth = VRConfig.FreeCamFollowSmoothing.Value;
            if (smooth < 0.01f) { anchorPos = tp; anchorYaw = ty; anchorVel = Vector3.zero; anchorYawVel = 0f; return; }
            if (dt <= 0f) return;
            Spring(ref anchorPos.x, ref anchorVel.x, tp.x, smooth, dt);
            Spring(ref anchorPos.y, ref anchorVel.y, tp.y, smooth, dt);
            Spring(ref anchorPos.z, ref anchorVel.z, tp.z, smooth, dt);
            Spring(ref anchorYaw, ref anchorYawVel, ty, smooth, dt);
        }

        private static void Spring(ref float x, ref float v, float target, float smooth, float dt)
        {
            float omega = 2f / smooth;
            float e = omega * dt;
            float decay = 1f / (1f + e + 0.48f * e * e + 0.235f * e * e * e);
            float change = x - target;
            float temp = (v + omega * change) * dt;
            v = (v - omega * temp) * decay;
            x = target + (change + temp) * decay;
        }

        // ---- Keys.

        private static void Keys()
        {
            if (Down(KeyCode.F8))
            {
                if (take != null) EndTake("stopped (F8)");
                else StartTake();
                return;
            }
            if (take != null) return; // during a take, only F8 and F6
            if (Down(KeyCode.Tab)) SetCameraKeys(!cameraKeys);
            if (Down(KeyCode.F7)) AddKey();
            if (Down(KeyCode.Backspace)) RemoveKey();
            if (Down(KeyCode.F5)) ToggleFollow();
            if (Down(KeyCode.PageUp)) StepSlow(+1);
            if (Down(KeyCode.PageDown)) StepSlow(-1);
            if (Down(KeyCode.Home)) SetDuration(path.Duration + 1f);
            if (Down(KeyCode.End)) SetDuration(path.Duration - 1f);
            bool ctrl = Held(KeyCode.LeftControl) || Held(KeyCode.RightControl);
            for (int i = 1; i <= 9; i++)
            {
                if (!Down(KeyCode.Alpha0 + i)) continue;
                if (ctrl) Save(i.ToString(CultureInfo.InvariantCulture));
                else Load(i.ToString(CultureInfo.InvariantCulture), byKey: true);
            }
        }

        private static void SetCameraKeys(bool camera)
        {
            cameraKeys = camera;
            flyVelocity = Vector3.zero;
            Say(camera ? "Keys and mouse fly the camera (Tab: to the game)." : "Keys and mouse go to the game, Esc included; the camera holds (Tab: back to the camera).");
            Plugin.L.LogInfo($"freecam: keys to the {(camera ? "camera" : "game")}.");
        }

        private static void ToggleFollow()
        {
            if (following)
            {
                StopFollow();
                Say("Not following any more.");
            }
            else if (StartFollow()) Say($"Following {targetName}: the camera keeps its place from it, eased.");
            else Say("Nothing to follow.");
        }

        // ---- The path.

        private static CameraPath NewPath() => new CameraPath
        {
            Duration = VRConfig.FreeCamDuration.Value,
            Ease = VRConfig.FreeCamEase.Value,
        };

        /// <summary>Loads last.json the first time a path is needed.</summary>
        private static void EnsurePath()
        {
            if (path != null) return;
            path = NewPath();
            if (File.Exists(Path.Combine(ShotsDir, "last.json"))) Load("last", quiet: true);
        }

        /// <summary>Marks the path changed and autosaves last.json.</summary>
        private static void Touch()
        {
            bool wasTimed = path.Timed;
            float duration = path.Duration;
            path.Changed();
            path.Unsaved = true;
            Autosave();
            // report an edit that breaks the key times, or a duration stretched by the last key's time
            if (wasTimed && !path.Timed)
            {
                Plugin.L.LogWarning("freecam: " + path.TimingNote + ".");
                // on screen too, added to the edit's own line by Say
                timingWarning = "Keyframe times off: " + path.TimingNote + ". Undo the last change, or fix the t values in the shot's file.";
            }
            else if (path.Timed && path.Duration > duration)
                Plugin.L.LogInfo($"freecam: the take lasts {path.Duration.ToString("0.###", CultureInfo.InvariantCulture)} s, the last keyframe's time.");
        }

        private static void AddKey()
        {
            if (take != null) return;
            if (path.Keys.Count >= CameraPath.MaxKeys)
            {
                Say($"{CameraPath.MaxKeys} keyframes already, the most a path takes. Backspace removes the last one.");
                return;
            }
            var space = following ? ShotSpace.Hova : ShotSpace.World;
            if (path.Keys.Count > 0 && path.Space != space)
            {
                Say(path.Space == ShotSpace.Hova
                    ? "This path moves around the HOVA: F5 to follow it again first (or Backspace the keyframes away)."
                    : "This path is set in the city: F5 to stop following first (or Backspace the keyframes away).");
                return;
            }
            path.Space = space;
            path.Keys.Add(pose.Rounded());
            Touch();
            Plugin.L.LogInfo($"freecam: keyframe {path.Keys.Count} at {path.Keys[path.Keys.Count - 1]}{(space == ShotSpace.Hova ? " (from " + targetName + ")" : "")}; path {DescribePath()}.");
            Say($"Keyframe {path.Keys.Count} added{(space == ShotSpace.Hova ? " (around " + targetName + ")" : "")}.");
        }

        private static void RemoveKey()
        {
            if (take != null || path.Keys.Count == 0) return;
            path.Keys.RemoveAt(path.Keys.Count - 1);
            Touch();
            Plugin.L.LogInfo($"freecam: last keyframe removed; path {DescribePath()}.");
            Say(path.Keys.Count > 0 ? $"Keyframe {path.Keys.Count + 1} removed." : "No keyframes left.");
        }

        private static void StepSlow(int dir)
        {
            int i = 0;
            while (i < SlowSteps.Length - 1 && SlowSteps[i] < path.Slow - 1e-4f) i++;
            if (dir > 0 && SlowSteps[i] <= path.Slow + 1e-4f) i++;
            if (dir < 0) i--;
            SetSlow(SlowSteps[Math.Clamp(i, 0, SlowSteps.Length - 1)]);
        }

        private static void SetSlow(float s)
        {
            path.Slow = MathF.Round(Math.Clamp(s, SlowestSlow, 1f), 3);
            Touch();
            Plugin.L.LogInfo($"freecam: slow motion x{path.Slow.ToString("0.###", CultureInfo.InvariantCulture)}.");
            Say(path.Slow < 0.999f ? $"Slow motion: the world at x{path.Slow.ToString("0.##", CultureInfo.InvariantCulture)}." : "Normal speed.");
        }

        private static void SetDuration(float s)
        {
            path.Duration = Math.Clamp(MathF.Round(s, 2), 1f, 600f);
            Touch();
            Say($"Takes last {path.Duration.ToString("0.##", CultureInfo.InvariantCulture)} s.");
        }

        private static void SetEase(float e)
        {
            path.Ease = Math.Clamp(MathF.Round(e, 3), 0f, 0.5f);
            Touch();
        }

        private static string DescribePath()
        {
            var c = CultureInfo.InvariantCulture;
            if (path == null) return "none";
            return $"{(path.Name.Length > 0 ? "'" + path.Name + "'" : "unsaved")}{(path.Unsaved && path.Name.Length > 0 ? " (changed since)" : "")}, " +
                   $"{path.Keys.Count} keyframe(s) {(path.Space == ShotSpace.Hova ? "around the followed HOVA" : On && menuMode ? "in the main menu's scene" : "in the city")}, " +
                   $"{path.Duration.ToString("0.##", c)} s, ease {path.Ease.ToString("0.##", c)}, world at x{path.Slow.ToString("0.##", c)}" +
                   $"{(path.RelightAt >= 0f ? ", the sign relit at " + path.RelightAt.ToString("0.##", c) + " s" : "")}" +
                   $"{(path.Timed ? ", keyframes timed" : path.TimingNote.Length > 0 ? ", keyframe times NOT used" : "")}, sig {path.Signature():x8}";
        }

        // ---- Files: BepInEx\config\CloudpunkVR-shots\<name>.json, "last" autosaved on every change.

        private static void Autosave()
        {
            try { WriteFile(Path.Combine(ShotsDir, "last.json"), path.ToJson(District())); }
            catch (Exception e) { if (errors++ < 20) Plugin.L.LogWarning("freecam: last path not written: " + e.Message); }
        }

        private static void WriteFile(string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, text);
        }

        private static void Save(string name)
        {
            if (!CameraPath.ValidName(name) || name == "last" || name == BeforeLoad)
            {
                Plugin.L.LogInfo($"freecam: '{name}' can't be a shot's name.");
                Say($"'{name}' can't be a shot's name.");
                return;
            }
            if (path.Keys.Count == 0) { Say("Nothing to save: no keyframes yet."); return; }
            string file = Path.Combine(ShotsDir, name + ".json");
            try
            {
                path.Name = name;
                path.Unsaved = false;
                WriteFile(file, path.ToJson(District()));
                Autosave();
                // read back to check the file plays what was set up
                var back = CameraPath.FromJson(File.ReadAllText(file), out string error);
                bool same = back != null && back.Signature() == path.Signature();
                Plugin.L.LogInfo($"freecam: path saved to {file}: {DescribePath()}; " +
                                 (same ? "read back identical." : $"read back DIFFERENT ({(back != null ? $"sig {back.Signature():x8}" : error)})."));
                Say($"Saved as shot {name}.");
            }
            catch (Exception e)
            {
                path.Unsaved = true;
                Plugin.L.LogWarning($"freecam: path not saved to {file}: {e.Message}");
                Say($"Shot {name} not saved: {e.Message}");
            }
        }

        /// <summary>Over unsaved keyframes: needs a second key press, backs them up to before-load.json.</summary>
        private static void Load(string name, bool quiet = false, bool byKey = false)
        {
            if (take != null) return;
            if (!CameraPath.ValidName(name))
            {
                Plugin.L.LogInfo($"freecam: no shot can be called '{name}'.");
                return;
            }
            string file = Path.Combine(ShotsDir, name + ".json");
            if (!File.Exists(file))
            {
                if (!quiet) Say($"No shot {name} saved yet (Ctrl + {name} saves one).");
                Plugin.L.LogInfo($"freecam: no shot {name} in {ShotsDir}.");
                return;
            }
            bool unsaved = name != "last" && path != null && path.Unsaved && path.Keys.Count > 0;
            if (unsaved && byKey && !(pendingLoad == name && Time.unscaledTime < pendingLoadUntil))
            {
                pendingLoad = name;
                pendingLoadUntil = Time.unscaledTime + 3f;
                Say($"Your keyframes here aren't saved: press {name} again to load shot {name} over them, or Ctrl + a number to save them first.");
                return;
            }
            pendingLoad = null;
            string error;
            CameraPath loaded;
            try { loaded = CameraPath.FromJson(File.ReadAllText(file), out error); }
            catch (Exception e) { loaded = null; error = e.Message; }
            if (loaded == null)
            {
                Plugin.L.LogWarning($"freecam: {file} not read: {error}");
                Say($"Shot {name} could not be read: {error}");
                return;
            }
            string kept = "";
            if (unsaved)
            {
                try
                {
                    WriteFile(Path.Combine(ShotsDir, BeforeLoad + ".json"), path.ToJson(District()));
                    kept = $" The keyframes you had are in {BeforeLoad}.json.";
                }
                catch (Exception e) { kept = $" The keyframes you had could not be kept: {e.Message}."; }
            }
            if (name != "last")
            {
                loaded.Name = name == BeforeLoad ? "" : name; // file name wins over the name inside
                loaded.Unsaved = name == BeforeLoad;
            }
            path = loaded;
            if (name != "last") Autosave();
            string madeIn = loaded.District, here = District();
            string elsewhere = madeIn.Length > 0 && madeIn != here ? $" Made in {madeIn}, you are in {here}: it may be inside walls here." : "";
            string timing = path.TimingNote.Length > 0 ? " " + char.ToUpperInvariant(path.TimingNote[0]) + path.TimingNote.Substring(1) + "." : "";
            string loadedLine = $"freecam: path {(name == "last" ? "of last time" : name)} loaded from {file}: {DescribePath()}{(error != null ? " (" + error + ")" : "")}.{timing}{elsewhere}{kept}";
            // unusable key times are a mistake in the file: warn
            if (path.TimingNote.Length > 0 && !path.Timed) Plugin.L.LogWarning(loadedLine);
            else Plugin.L.LogInfo(loadedLine);
            if (!quiet) Say($"Shot {name} loaded: {path.Keys.Count} keyframe(s), {path.Duration.ToString("0.##", CultureInfo.InvariantCulture)} s. F8 plays it.{elsewhere}{kept}");
        }

        private static string District()
        {
            if ((On && menuMode) || CleanFrame.MainMenuUp()) return "main menu";
            var sb = new StringBuilder();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (s.name == "Main" || s.name == "Startup") continue;
                sb.Append(sb.Length > 0 ? " " : "").Append(s.name);
            }
            return sb.ToString();
        }

        // ---- Takes.

        private static void StartTake()
        {
            if (take != null) return;
            if (path.Keys.Count == 0 && !following)
            {
                Say("Add keyframes with F7 first (or follow the HOVA with F5 and play that).");
                return;
            }
            if (stills) return;
            if (path.Keys.Count > 0)
            {
                if (path.Space == ShotSpace.Hova && !following && !StartFollow())
                {
                    Say(menuMode ? "This path moves around the HOVA: not on the main menu." : "This path moves around the HOVA, and there is nothing to follow.");
                    return;
                }
                if (path.Space == ShotSpace.World) StopFollow();
            }
            int fps = VRConfig.FreeCamFramerate.Value;
            var format = GrabTimes != null ? FrameFormat.Png : VRConfig.FreeCamSaveFrames.Value;
            take = new Take(++takes, path.Copy(), fps, following ? targetName : null, menuMode ? "on the main menu" : "in the city",
                            format, GrabTimes, Probe, VRConfig.FreeCamKeepOcclusion.Value);
            take.Lens(cam.aspect, cam.nearClipPlane, cam.farClipPlane);
            keysBeforeTake = cameraKeys;
            cameraKeys = false; // game gets the keys during a take: drive the HOVA
            flyVelocity = Vector3.zero;
            if (take.WantsGrab && instance != null) instance.StartCoroutine(Grabber(take).WrapToIl2Cpp());
            string frames = format == FrameFormat.Off ? "no frames saved"
                : take.FramesRefused != null ? "NO frames saved: " + take.FramesRefused
                : GrabTimes != null ? "frames at " + Times(GrabTimes) + " s saved" : format + " frames saved";
            Plugin.L.LogInfo($"freecam take {take.Number}: {DescribePath()}; {(fps > 0 ? fps + " fps fixed steps" : "real time")}, {frames}" +
                             $"{(Probe ? ", probe on" : "")}, {Occlusion()}, after a {Take.PreRollSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s still pre-roll.");
            if (take.FramesRefused != null) Say($"Not enough room for the frames ({take.FramesRefused}): this take plays without saving them.");
        }

        private static void EndTake(string stoppedBecause)
        {
            var t = take;
            take = null;
            cameraKeys = keysBeforeTake;
            t.Finish(stoppedBecause);
            if (t.Path.RelightAt >= 0f) UI.MenuSign.EndDrive(stoppedBecause ?? $"take {t.Number} done");
            if (stoppedBecause != null) Say($"Take {t.Number} stopped.");
            else Say($"Take {t.Number} done ({t.Path.Duration.ToString("0.##", CultureInfo.InvariantCulture)} s). F8 plays it again.");
        }

        private static System.Collections.IEnumerator Grabber(Take mine)
        {
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                if (take != mine) yield break;
                mine.GrabAtEndOfFrame();
            }
        }

        // ---- What the log and the note say.

        private static string Times(float[] times)
        {
            var parts = new string[times.Length];
            for (int i = 0; i < times.Length; i++) parts[i] = times[i].ToString("0.##", CultureInfo.InvariantCulture);
            return string.Join(", ", parts);
        }

        private static string Prompts() =>
            promptsBit == 0 ? "no 'Text' layer for the floating prompts" : promptsOff ? "the floating prompts (layer 'Text') left out" : "the floating prompts were not drawn anyway";

        private static string Occlusion()
        {
            if (VRConfig.FreeCamKeepOcclusion.Value) return "the game's occlusion culling kept (KeepOcclusionCulling)";
            return $"occlusion culling off (GDOC {(gdoc != null ? (gdocWasOn ? "was on" : "was already off") : "not found")}, instanced props' occlusion off in {instancers.Count} manager(s))";
        }

#if DEV
        /// <summary>
        /// Dev command "freecam gdoc": what GDOC hides now. Switched off it should show everything again (its OnDisable
        /// calls EnableAllOccludees), none left off or shadows-only.
        /// </summary>
        private static string GdocCheck()
        {
            var gd = GDOC.instance;
            if (gd == null) return "no GDOC here";
            var list = GDOC.occludees; // by id, up to the highest one given
            int n = list != null ? Math.Min(list.Length, GDOC.maxOcludeeId + 1) : 0, hidden = 0, count = 0;
            var states = new SortedDictionary<int, int>();
            var names = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var o = list[i];
                if (o == null) continue;
                count++;
                int s = o.currentState;
                states.TryGetValue(s, out int k);
                states[s] = k + 1;
                bool shown = o.gameObject.activeInHierarchy;
                var r = o.renderer;
                if (shown && r != null)
                    shown = r.enabled && !(r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                                           && o.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly);
                if (shown) continue;
                hidden++;
                if (names.Count < 6) names.Add(o.name);
            }
            var st = new StringBuilder();
            foreach (var kv in states) st.Append(st.Length > 0 ? ", " : "").Append(kv.Key).Append(" x").Append(kv.Value);
            return $"GDOC {(gd.enabled ? "on" : "off")}: {count} occludees, {hidden} not shown now{(names.Count > 0 ? " (" + string.Join(", ", names) + (hidden > names.Count ? ", ..." : "") + ")" : "")}, " +
                   $"states {(st.Length > 0 ? st.ToString() : "none")}";
        }
#endif

        private static string StatusLine()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("FREE CAMERA");
            sb.Append(cameraKeys ? "  |  CAMERA KEYS" : "  |  GAME KEYS");
            if (path != null)
            {
                sb.Append($"  |  {path.Keys.Count} KEYFRAME{(path.Keys.Count == 1 ? "" : "S")}");
                sb.Append($"  |  {path.Duration.ToString("0.##", c)} S");
                if (path.Slow < 0.999f) sb.Append($"  |  SLOW X{path.Slow.ToString("0.##", c)}");
                if (path.Name.Length > 0) sb.Append($"  |  SHOT {path.Name.ToUpperInvariant()}{(path.Unsaved ? "*" : "")}");
            }
            if (following) sb.Append($"  |  FOLLOWING {targetName.ToUpperInvariant()}");
            return sb.ToString();
        }

        private static string timingWarning; // key times just went off: shown with the next line

        private static void Say(string message, string status = null)
        {
            if (timingWarning != null) { message = (message ?? "") + " " + timingWarning; timingWarning = null; }
            FreeCamNote.Show(status ?? StatusLine(), message);
        }

        /// <summary>Game time, pacing, keys and camera state.</summary>
        private static string GameState()
        {
            var c = CultureInfo.InvariantCulture;
            var g = Global.Instance;
            var a = Actions();
            var ui = g != null ? g.uiCamera : null;
            bool menu = CleanFrame.MainMenuUp();
            var camHere = menu ? g.menuCamera : g != null && g.mainCamera != null ? g.mainCamera.transform : null;
            var b = camHere != null ? camHere.GetComponent<CinemachineBrain>() : null;
            var gd = GDOC.instance;
            return $"time x{Time.timeScale.ToString("0.###", c)}, physics step {Time.fixedDeltaTime.ToString("0.####", c)} s, capture rate {Time.captureFramerate}, " +
                   $"vsync {QualitySettings.vSyncCount}, target frame rate {Application.targetFrameRate}; game keys {(a == null ? "none" : a.Enabled ? "on" : "off")}, " +
                   $"cursor {Cursor.lockState}{(Cursor.visible ? "" : " hidden")}; UI camera mask 0x{(ui != null ? ui.cullingMask : 0):X8}, " +
                   $"{(menu ? "the menu camera's " : "")}Cinemachine {(b != null ? (b.enabled ? "on" : "off") : "none")}, GDOC {(gd != null ? (gd.enabled ? "on" : "off") : "none")}";
        }

        public static string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            if (!On) return $"off; {GameState()}; keys {(VRConfig.FreeCamEnabled.Value ? "on (F6)" : "off ([FreeCam] Enabled = false)")}; path {DescribePath()}.";
            var sb = new StringBuilder(menuMode ? $"on, the main menu's camera '{cam.name}'" : "on");
            sb.Append($"; keys to the {(cameraKeys ? "camera" : "game")}{(actionsBlocked ? " (the game's actions off)" : "")}");
            ToWorld(pose, out var p, out var r);
            sb.Append($"; camera at {ShotPose.From(p, r, pose.Fov)}");
            if (following) sb.Append($"; following {targetName}, {(p - target.position).magnitude.ToString("0.0", c)} m from it");
            else if (!menuMode && FindTarget(out var t, out var n)) sb.Append($"; {(p - t.position).magnitude.ToString("0.0", c)} m from {n}");
            sb.Append($"; path {DescribePath()}");
            if (take != null) sb.Append($"; take {take.Number} {(take.Rolling ? "rolling" : "in its pre-roll")}");
            if (stills) sb.Append("; rendering the keyframe stills");
            sb.Append($"; framerate {VRConfig.FreeCamFramerate.Value}, frames {(GrabTimes != null ? "at " + Times(GrabTimes) : VRConfig.FreeCamSaveFrames.Value.ToString())}{(Probe ? ", probe on" : "")}");
            if (menuMode) sb.Append($"; {MenuInput.Describe()}; {SignFromHere()}; {GameState()}");
            else
            {
                sb.Append($"; cockpit draw {(interiorDraw != null ? (interiorDraw.enabled ? "ON" : "off") : "none")}, prompts {(promptsBit != 0 && (cam.cullingMask & promptsBit) != 0 ? "DRAWN" : "out")}");
                sb.Append($", instanced props' occlusion off in {instancers.Count} manager(s); {GameState()}");
            }
            sb.Append($"; moved back {poseFights} time(s), Cinemachine switched back on {brainFights} time(s); district {District()}");
            return sb.ToString();
        }

        /// <summary>The menu sign's centre from the camera, and the yaw/pitch to look straight at it.</summary>
        private static string SignFromHere()
        {
            if (camT == null || !UI.MenuSign.SignBounds(out var b)) return "no menu sign here";
            var c = CultureInfo.InvariantCulture;
            var from = camT.position;
            Toward(from, b.center, out float yaw, out float pitch);
            return string.Format(c, "the sign's centre at ({0:0.0}, {1:0.0}, {2:0.0}), {3:0.0} away and {4:0.0} wide: straight at it is yaw {5:0.00} pitch {6:0.00} (the camera: yaw {7:0.00} pitch {8:0.00} fov {9:0.0})",
                                 b.center.x, b.center.y, b.center.z, (b.center - from).magnitude, b.size.x, yaw, pitch, pose.Yaw, pose.Pitch, pose.Fov);
        }

        /// <summary>Yaw (right) and pitch (up) in degrees to look from one point at another.</summary>
        private static void Toward(Vector3 from, Vector3 to, out float yaw, out float pitch)
        {
            var d = to - from;
            yaw = MathF.Atan2(d.x, d.z) * (180f / MathF.PI);
            pitch = MathF.Atan2(d.y, MathF.Sqrt(d.x * d.x + d.z * d.z)) * (180f / MathF.PI);
        }

        // ---- Menu sign relight in a take (CameraPath.RelightAt).

        /// <summary>
        /// Drives the relight from the take's clock (dark through the pre-roll), so letters land on the same frames in
        /// every take. The crackle sound only plays in real-time takes (no frames saved).
        /// </summary>
        private static void DriveRelight(Take t)
        {
            if (t.Path.RelightAt < 0f || t.SignMissing) return;
            if (UI.MenuSign.Drive(t.RelightClockNow, !t.WantsGrab)) return;
            t.SignMissing = true;
            Plugin.L.LogWarning($"freecam take {t.Number}: the path relights the menu sign at {t.Path.RelightAt.ToString("0.##", CultureInfo.InvariantCulture)} s, " +
                                "but the sign isn't here (the main menu, with MenuSign on): no relight in this take.");
        }

        // ---- Dev tools (FreeCam.Dev.cs): keyframe stills.

        static partial void StillsFrame(ref ShotPose shown);
        static partial void StopStills(string why);

        // ---- Dev command "freecam ..." (DevCommands).

#if DEV
        public static string Dev(string[] a)
        {
            if (instance == null) return "the free camera works in the flat game only (StartInVR = Never, or no headset); this launch is in VR.";
            string cmd = a.Length > 1 ? a[1].ToLowerInvariant() : "info";
            float F(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
            string Arg(int i) => a.Length > i ? a[i].ToLowerInvariant() : "";
            EnsurePath();
            bool needsOn = cmd is "key" or "undo" or "nudge" or "play" or "follow" or "controls" or "keys" or "aim";
            if (needsOn && !On) return "off: 'freecam on' first.";
            if (stills && cmd is "key" or "undo" or "clear" or "nudge" or "play" or "load" or "follow" or "aim" or "keys") return "the keyframe stills are being rendered.";
            switch (cmd)
            {
                case "on": if (!On && !TurnOn()) return "not on: no game camera yet."; break;
                case "off": if (On) TurnOff("developer command"); break;
                case "key": AddKey(); break;
                case "undo": RemoveKey(); break;
                case "clear":
                    if (take != null) return "a take is running.";
                    path.Keys.Clear();
                    path.Name = "";
                    Touch();
                    break;
                case "nudge": // nudge <right> <up> <ahead> [yaw] [pitch] [fov]: m and degrees, camera heading
                    if (take != null) return "a take is running.";
                    pose.Position += Quaternion.Euler(0f, pose.Yaw, 0f) * new Vector3(F(2), 0f, F(4)) + new Vector3(0f, F(3), 0f);
                    if (a.Length > 5) pose.Yaw = ShotPose.Angle(pose.Yaw + F(5));
                    if (a.Length > 6) pose.Pitch = Math.Clamp(pose.Pitch + F(6), -89f, 89f);
                    if (a.Length > 7) pose.Fov = Math.Clamp(pose.Fov + F(7), 10f, 100f);
                    break;
                case "play": // play [seconds]
                    if (take != null) return "a take is running.";
                    if (a.Length > 2) SetDuration(F(2));
                    StartTake();
                    if (take == null) return "no take: no keyframes and not following.";
                    break;
                case "stop": if (take != null) EndTake("stopped (developer command)"); break;
                case "save": Save(Arg(2)); break;
                case "load": Load(Arg(2)); break;
                case "list":
                    var files = Directory.Exists(ShotsDir) ? Directory.GetFiles(ShotsDir, "*.json") : new string[0];
                    var names = new List<string>();
                    foreach (var f in files)
                        if (!Path.GetFileName(f).StartsWith("spots-", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(f)); // `spots` district maps, not shots
                    return $"shots in {ShotsDir}: {(names.Count > 0 ? string.Join(", ", names) : "none")}.";
                case "follow": // follow [on|off]
                    if (Arg(2) == "off" || (Arg(2) == "" && following)) StopFollow();
                    else if (!following && !StartFollow()) return "nothing to follow.";
                    break;
                case "slow": SetSlow(F(2)); break;
                case "ease": SetEase(F(2)); break;
                case "fps": VRConfig.FreeCamFramerate.Value = (int)F(2); break;
                case "frames": // frames off|png|jpg|at <seconds...>
                    if (Arg(2) == "at")
                    {
                        var times = new List<float>();
                        for (int i = 3; i < a.Length; i++) times.Add(F(i));
                        GrabTimes = times.ToArray();
                    }
                    else
                    {
                        GrabTimes = null;
                        VRConfig.FreeCamSaveFrames.Value = (FrameFormat)Enum.Parse(typeof(FrameFormat), Arg(2), true);
                    }
                    break;
                case "occlusion": VRConfig.FreeCamKeepOcclusion.Value = Arg(2) == "on"; break; // on: the game's occlusion culling kept
                case "gdoc": return GdocCheck();
                case "check": return "check: " + Describe();
                case "probe": Probe = Arg(2) != "off"; break;
                case "controls": SetCameraKeys(Arg(2) != "game"); break;
                case "keys": return Stills(Arg(2)); // keys [png|jpg]: one still posed at each keyframe
                case "relight": return Relight(Arg(2)); // relight <s>|off: the menu sign relit that far into a take
                case "aim": // aim sign | aim <x> <y> <z>: the camera turned straight at it
                    return Arg(2) == "sign" ? Aim(null) : a.Length > 4 ? Aim(new Vector3(F(2), F(3), F(4))) : "aim sign | aim <x> <y> <z>";
                case "info": break;
                default: return "unknown: freecam on|off|key|undo|clear|nudge|play [s]|stop|save|load|list|follow [on|off]|slow|ease|fps|frames|occlusion|gdoc|check|probe|controls|keys [png|jpg]|relight <s>|off|aim sign|<x> <y> <z>|info";
            }
            return Describe();
        }
#endif
    }
}
