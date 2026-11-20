#if DEV
using System;
using System.Globalization;
using System.IO;
using CloudpunkVR.Input;
using InControl;
using UnityEngine;

namespace CloudpunkVR
{
    /// <summary>
    /// With CommandFile on, runs the lines of BepInEx/config/CloudpunkVR.devcmd, then deletes it.
    /// Input goes through the game's code or the virtual gamepad, never Windows input.
    ///   dump | status | continue | recenter | cutcheck | screen
    ///   press Action1 0.2 | stick lx ly rx ry 1.5      virtual gamepad, for N s
    ///   title [keep] | prompts [style] | drink | effects reduced | lift [list|enter|call] [n]
    ///   goto | spots | park                            any district through the game's passages (Film/Districts)
    ///   perf, spectator, clean, glass, call ...        usage on each case below
    /// </summary>
    internal static class DevCommands
    {
        private static float nextPoll;
        private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "CloudpunkVR.devcmd");

        // hud expect: awaited piece and deadline
        private static string expected;
        private static float expectedUntil;

        public static void Poll()
        {
            RestoreLiftTrigger();
            Film.Districts.Tick();
            CheckExpected();
            if (!VR.VRSession.Active) UI.CallProbe.PollFlat();
            CheckTrader();
            if (!VRConfig.DevCommandFile.Value || Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.5f;
            if (!File.Exists(FilePath)) return;
            string[] lines;
            try { lines = File.ReadAllLines(FilePath); File.Delete(FilePath); }
            catch (IOException) { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { Run(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)); }
                catch (Exception e) { Plugin.L.LogWarning($"devcmd '{line}' failed: {e.Message}"); }
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static void CheckExpected()
        {
            if (expected == null) return;
            if (UI.HudLayout.Showing(expected))
            {
                Plugin.L.LogInfo("hud check: " + expected + " piece in view. " + UI.HudLayout.Describe());
                expected = null;
            }
            else if (Time.unscaledTime > expectedUntil)
            {
                Plugin.L.LogWarning("hud check FAILED: no " + expected + " piece in view. Dialogue: " + UI.HudLayout.DialogueReport + ". " + UI.VRUI.DescribeView());
                expected = null;
            }
        }

        private static void Run(string[] a)
        {
            Plugin.L.LogInfo("devcmd: " + string.Join(" ", a));
            switch (a[0].ToLowerInvariant())
            {
                case "dump":
                    Diagnostics.Dump();
                    break;
                case "continue":
                    Global.Instance.ContinueGame();
                    break;
                case "title": // title [keep]
                    UI.TitleScreen.PassNow(a.Length > 1 && a[1].Equals("keep", StringComparison.OrdinalIgnoreCase));
                    break;
                case "press":
                    VirtualGamepad.Hold((InputControlType)Enum.Parse(typeof(InputControlType), a[1], true), a.Length > 2 ? F(a[2]) : 0.15f);
                    break;
                case "stick":
                    VirtualGamepad.HoldSticks(new Vector2(F(a[1]), F(a[2])), new Vector2(F(a[3]), F(a[4])), F(a[5]));
                    break;
                case "recenter":
                    VR.CameraDriver.Recenter();
                    break;
                case "status":
                    var g = Global.Instance;
                    Plugin.L.LogInfo($"status: mode={VR.CameraDriver.Mode} vehicle={(g != null && Global.IsInVehicle)} cockpit={(g != null && Global.IsInVehicleCockpit)} menu={(g != null && g.IsInMenu())} " +
                                     $"dialog={(g != null && g.IsInDialog(false))} map={(g != null && Global.FullMapOpen)} ui={UI.VRUI.Interactive} {VR.VRSession.FrameStats()}; " +
                                     $"game prompts for a controller={(g != null && g.usingController)}, {HovaState(g)}; {WindowFocus.Describe()}");
                    break;
                case "xref":
                    Xref(a[1], a[2]);
                    break;
                case "sprites":
                    DumpPromptSprites();
                    break;
                case "prompts": // prompts auto|playstation|xbox
                    if (a.Length > 1) ButtonPrompts.Override = (PromptStyle)Enum.Parse(typeof(PromptStyle), a[1], true);
                    else Plugin.L.LogInfo(ButtonPrompts.Describe());
                    break;
                case "vrstick": // vrstick lx ly rx ry seconds
                    SteamVRInput.Simulate(new Vector2(F(a[1]), F(a[2])), new Vector2(F(a[3]), F(a[4])), F(a[5]));
                    break;
                case "vrpress": // vrpress recenter|switchview|clickright|clickleft|menu|map|freelook|back
                    SteamVRInput.SimulatePress(a[1]);
                    break;
                case "uipoint": // uipoint x y [click], 1080p pixels from bottom-left
                    Plugin.L.LogInfo("uipoint: " + UI.LaserPointer.DebugPoint(new Vector2(F(a[1]), F(a[2])), a.Length > 3 && a[3] == "click"));
                    break;
                case "vignette": // vignette [s] | info | trace <s>
                    if (a.Length > 1 && a[1] == "trace") UI.Vignette.Trace(a.Length > 2 ? F(a[2]) : 20f);
                    else if (a.Length < 2 || a[1] != "info") UI.Vignette.Force(a.Length > 1 ? F(a[1]) : 3f);
                    Plugin.L.LogInfo("vignette: " + UI.Vignette.Describe());
                    break;
                case "drink": // alcohol effect, to check ScreenEffects
                    Global.Instance.UseAlcohol();
                    break;
                case "effects": // effects full|reduced|off
                    VRConfig.ScreenEffects.Value = (ScreenEffectLevel)Enum.Parse(typeof(ScreenEffectLevel), a[1], true);
                    break;
                case "perf": // perf [measure <label> [s] | <switch> on|off | reset]
                    VR.PerfProbe.Run(a);
                    break;
                case "spectator": // spectator [on|off|key|format|fov|smooth|tilt|trace|shake|flip|mirror ...]
                    Plugin.L.LogInfo("spectator: " + VR.SpectatorView.Command(a));
                    break;
                case "clean": // clean [on|off|key | dialogue on|off | check [label] | watch <s> [label]]
                    Plugin.L.LogInfo("clean: " + Film.CleanFrame.Command(a));
                    break;
                case "traffic": // traffic [density]
                    if (a.Length > 1) VRConfig.TrafficDensity.Value = F(a[1]);
                    Plugin.L.LogInfo("traffic: " + VR.PerformanceTweaks.Describe());
                    break;
                case "hands": // hands demo [curl|open|fingers] [view] [pull] [squeeze] | readout [s] | off
                    if (a.Length > 1 && a[1] == "readout")
                    {
                        UI.VoxelHands.LogReadout();
                        UI.VoxelHands.ReadoutUntil = a.Length > 2 ? Time.unscaledTime + F(a[2]) : -1f;
                        break;
                    }
                    UI.VoxelHands.Demo = a.Length > 1 && a[1] == "demo";
                    // number: curl 0-1, else finger letters ("open" for none)
                    bool curl = a.Length > 2 && float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                    UI.VoxelHands.DemoCurl = curl ? F(a[2]) : -1f;
                    UI.VoxelHands.DemoFingers = a.Length > 2 && !curl ? (a[2] == "open" ? "" : a[2]) : null;
                    UI.VoxelHands.DemoView = a.Length > 3 ? (int)F(a[3]) : 0;
                    UI.VoxelHands.DemoTrigger = a.Length > 4 ? F(a[4]) : 0f;
                    UI.VoxelHands.DemoSqueeze = a.Length > 5 ? F(a[5]) : 0f;
                    Plugin.L.LogInfo(UI.VoxelHands.Demo
                        ? "hands demo: " + (curl ? "curl " + a[2] : UI.VoxelHands.DemoFingers != null ? "fingers on '" + UI.VoxelHands.DemoFingers + "'" : "moving")
                          + ", view " + UI.VoxelHands.DemoView + ", pull " + (a.Length > 4 ? a[4] : "0") + ", squeeze " + (a.Length > 5 ? a[5] : "0")
                        : "hands demo off");
                    break;
                // fx list | skybox|extrapost|bloom|volumetric|fxaa on|off | pp|cam [<Type> off|on|reset]
                // fx hx on|off|lights | eyes on|off|dump | eyes fake [turn [ipd [tilt]]|off]
                case "fx":
                    if (a.Length > 1 && a[1] == "pp") Plugin.L.LogInfo("fx pp: " + VR.PostFxProbe.Pp(a));
                    else if (a.Length > 1 && a[1] == "cam") Plugin.L.LogInfo("fx cam: " + VR.PostFxProbe.Cam(a));
                    else if (a.Length > 1 && a[1] == "hx")
                    {
                        if (a.Length > 2 && a[2] == "lights") VR.VolumetricPerEye.CountLightsNext = true;
                        else if (a.Length > 2) VR.VolumetricPerEye.Enabled = a[2] != "off";
                        Plugin.L.LogInfo("fx hx: " + VR.VolumetricPerEye.Describe());
                    }
                    else if (a.Length > 2 && a[1] == "eyes" && a[2] == "fake")
                        Plugin.L.LogInfo("fx eyes: " + VR.FakeEyes.Set(a));
                    else if (a.Length > 1 && a[1] == "eyes")
                    {
                        if (a.Length > 2 && a[2] == "dump") VR.FogRaysPerEye.DumpNext = true;
                        else if (a.Length > 2) VR.FogRaysPerEye.Enabled = a[2] != "off";
                        Plugin.L.LogInfo("fx eyes: " + VR.FogRaysPerEye.Describe());
                    }
                    else Effects(a.Length > 1 ? a[1] : "list", a.Length > 2 ? a[2] : "");
                    break;
                case "eyes": // eyes [frames]: logs what differs between the eyes
                    Plugin.L.LogInfo("eyes: " + VR.EyeProbe.Start(a.Length > 1 ? (int)F(a[1]) : 3));
                    break;
                case "focus": // where the menu focus is
                    Plugin.L.LogInfo("focus: " + UI.VRUI.DescribeView() + "; option row " + (UI.MenuValues.Selected()?.name ?? "none"));
                    break;
                case "hud": // hud [scale x | angle deg | menu deg | radar on|off | expect Kind [s]]
                    if (a.Length > 2 && a[1] == "expect")
                    {
                        // 6 s default, a line takes a moment to start
                        expected = a[2];
                        expectedUntil = Time.unscaledTime + (a.Length > 3 ? F(a[3]) : 6f);
                        break;
                    }
                    if (a.Length > 2 && a[1] == "scale") VRConfig.HudScale.Value = F(a[2]);
                    else if (a.Length > 2 && a[1] == "angle") VRConfig.HudAngle.Value = F(a[2]);
                    else if (a.Length > 2 && a[1] == "menu") VRConfig.MenuAngle.Value = F(a[2]);
                    else if (a.Length > 2 && a[1] == "radar") VRConfig.CockpitRadar.Value = a[2] != "off";
                    if (a.Length > 2) UI.HudLayout.Relayout();
                    Plugin.L.LogInfo("hud: " + UI.VRUI.DescribeView() + "; retired settings: " + VRConfig.Retired);
                    break;
                case "goto": // goto list | <scene> [exit] | stop: through a passage (Film/Districts); goto trader | <word>: Rania walks there
                    if (a.Length > 1 && Film.Districts.Handles(a[1])) Plugin.L.LogInfo("goto: " + Film.Districts.Goto(a));
                    else if (a.Length > 1 && a[1] == "trader") GotoTrader();
                    else GotoNamed(a.Length > 1 ? a[1] : "");
                    break;
                case "spots": // this district's places -> CloudpunkVR-shots\spots-<scene>.json
                    Plugin.L.LogInfo("spots: " + Film.Districts.Spots());
                    break;
                case "bench": // bench run or not, the achievement guard
                    Plugin.L.LogInfo("bench: " + Bench.Describe());
                    break;
                case "actions": // input actions and their bindings
                    Actions();
                    break;
                case "probe": // which special mechanics the loaded scenes have
                    Probe();
                    break;
                case "wheel": // wheel grab|release [hand] | turn <deg> | jump|freeze|drift|slip <hand> | thaw | stop
                    // wheel physics | rate [s] | sweep <deg>... [hold <s>] | sweep compare | lock <deg>
                    if (a.Length > 1 && Input.WheelSteering.Probe(a)) break;
                    Input.WheelGrab.Simulate(a);
                    Plugin.L.LogInfo("wheel: " + Input.WheelGrab.Describe());
                    break;
                case "lever": // lever grab [hand] [cm] | release [hand] | push <x> | jump|freeze|slip [hand] | thaw | stop
                    Input.LeverGrab.Simulate(a);
                    Plugin.L.LogInfo("lever: " + Input.LeverGrab.Describe());
                    break;
                case "headlook": // headlook <down deg> [right deg], alone resets
                    VR.VRSession.TestLook = a.Length > 1 ? Quaternion.Euler(F(a[1]), a.Length > 2 ? F(a[2]) : 0f, 0f) : (Quaternion?)null;
                    Plugin.L.LogInfo($"headlook: the head turned {(a.Length > 1 ? a[1] : "0")} degrees down, {(a.Length > 2 ? a[2] : "0")} right on top of its tracking.");
                    break;
                case "stereotest": // stereotest on|off
                    VRConfig.StereoTest.Value = a.Length < 2 || a[1] != "off";
                    break;
                case "art": // see ArtProbe
                    ArtProbe.Run();
                    break;
                case "rain": // rain [world|overlay|off]
                    if (a.Length > 1) VRConfig.Rain.Value = (RainMode)Enum.Parse(typeof(RainMode), a[1], true);
                    Plugin.L.LogInfo("rain: " + VR.WorldRain.Describe());
                    break;
                case "rainprobe": // every rain effect the game draws
                    RainProbe();
                    break;
                case "glass": // see GlassProbe
                    Plugin.L.LogInfo("glass: " + VR.GlassProbe.Dev(a));
                    break;
                case "uimap": // HUD parts on screen, and the game data the wrist reads
                    UiMap();
                    break;
                case "onfoot": // onfoot [reach]
                    if (a.Length > 1 && a[1] == "reach") Input.OnFoot.SimReach = true;
                    Plugin.L.LogInfo("onfoot: " + Input.OnFoot.Describe());
                    break;
                case "footview": // footview [info|check|third|orbit|tutorial|hold [off]|fight [s]] [label], see VR/FootView
                    Plugin.L.LogInfo("footview: " + VR.FootView.Dev(a));
                    break;
                case "charm": // charm [info|poke|swing|spin|tree|place|at right up ahead]
                    Plugin.L.LogInfo("charm: " + UI.CockpitCharm.Dev(a));
                    break;
                case "coffee": // coffee [info|grab|sip|hold|pour|drop|throw|fling|slosh|spill|level|refill|cabin|place|at ...]
                    Plugin.L.LogInfo("coffee: " + UI.CockpitCoffee.Dev(a));
                    break;
                case "itemprobe": // items in loaded scenes, and nearby interactables
                    ItemProbe();
                    break;
                case "gotoitem": // walk up to the nearest item
                    GotoItem();
                    break;
                case "look": // look [nearest|free|demo]
                    if (a.Length > 1 && a[1] == "demo") UI.LookHighlight.Demo();
                    else if (a.Length > 1) UI.LookHighlight.SimNearest = a[1] == "nearest";
                    Plugin.L.LogInfo("look: " + UI.LookHighlight.Describe());
                    break;
                case "call": // call [who] [lines] | next | check [label] | watch [s] | windshield on|off | hud off|on
                    Plugin.L.LogInfo("call: " + UI.CallProbe.Dev(a));
                    break;
                case "fuel20": // low-fuel story trigger, a Camus line
                    Global.Instance.fuel20Trigger.Unlock();
                    break;
                case "fuel0": // empty-tank story trigger, more Camus lines
                    Global.Instance.fuel0Trigger.Unlock();
                    break;
                case "wrist": // wrist [show|auto|parts|trace <s>|type <who> <words>]
                    if (a.Length > 1 && a[1] == "parts") { Plugin.L.LogInfo(UI.Wrist.DescribeParts()); break; }
                    if (a.Length > 1 && a[1] == "trace") { UI.Wrist.StartTrace(a.Length > 2 ? F(a[2]) : 10f); break; }
                    if (a.Length > 3 && a[1] == "type") UI.Wrist.TypeTest(a[2], string.Join(" ", a, 3, a.Length - 3));
                    else if (a.Length > 1) UI.Wrist.ForceShow = a[1] == "show";
                    Plugin.L.LogInfo("wrist: " + UI.Wrist.Describe());
                    break;
                case "cams":
                    Plugin.L.LogInfo("cams: " + VR.CameraDriver.Describe());
                    break;
                case "hova": // hova [near|show|hide|trace <s>|glass pin|car|pair|none]
                    if (a.Length > 1 && (a[1] == "show" || a[1] == "hide")) { VR.HovaBody.ShowForTest = a[1] == "show"; VR.HovaBody.Update(); }
                    else if (a.Length > 1 && a[1] == "trace") VR.HovaBody.Trace(a.Length > 2 ? F(a[2]) : 10f);
                    else if (a.Length > 2 && a[1] == "glass" && Enum.TryParse(a[2], true, out VR.HovaBody.GlassMode glass)) VR.HovaBody.Glass = glass;
                    Plugin.L.LogInfo("hova: " + VR.HovaBody.Describe(a.Length > 1 && a[1] == "near"));
                    break;
                case "mirrors": // mirrors [info|game|own|off|low|high|angle|rear|grade|cull|at|place|look|strip|snap ...]
                    Plugin.L.LogInfo("mirrors: " + VR.CockpitMirrors.Dev(a));
                    break;
                case "overlay":
                    if (VR.CameraDriver.OverlayCamera) VR.CameraDriver.OverlayCamera.enabled = a.Length < 2 || a[1] != "off";
                    break;
                case "uisnap":
                    SnapUi(a.Length > 1 ? a[1] : "ui");
                    break;
                case "lift": // lift [list|enter|call] [n]
                    Lifts(a.Length > 1 ? a[1].ToLowerInvariant() : "list", a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 0);
                    break;
                case "cutcheck": // blinks since the last check
                    int cuts = VR.CameraDriver.VisibleCuts - cutsAtCheck;
                    cutsAtCheck = VR.CameraDriver.VisibleCuts;
                    Plugin.L.LogInfo($"cut check {++cutChecks}: {(cuts == 0 ? "none" : $"{cuts} blink(s) since the last check")}");
                    break;
                case "park": // park [item | <index> | <name>]: nearest volume, near an item, or one from `spots`
                    if (a.Length > 1 && a[1] != "item") Film.Districts.Park(string.Join(" ", a, 1, a.Length - 1));
                    else MoveCarToNearestParking(a.Length > 1);
                    break;
                case "menu": // menu [close]
                    var gm = Global.Instance;
                    bool open = gm != null && gm.IsInMenu();
                    if (open && a.Length > 1 && a[1] == "close") { gm.CloseMenu(); Plugin.L.LogInfo("menu: the game's menu was open; closed it."); }
                    else Plugin.L.LogInfo("menu: " + (open ? "the game's menu is open." : "no game menu open."));
                    break;
                case "vrsettings": // vrsettings [point ... | select <setting> | set <setting> <value> | check [label]], see UI/VrevivalMenu
                    Plugin.L.LogInfo("vrsettings: " + UI.VrevivalMenu.Dev(a));
                    break;
                case "board": // stand by the parked HOVA, ready to get in
                    WalkToHova();
                    break;
                case "sign": // sign [relight|at <s>|lit|check [s]|snap <name>|on|off|flicker on|off]
                    Plugin.L.LogInfo("sign: " + UI.MenuSign.Dev(a));
                    break;
                case "freecam": // freecam on|off|key|play [s]|stop|save|load|follow|slow|keys|relight|aim|..., see Film/FreeCam
                    Plugin.L.LogInfo("freecam: " + Film.FreeCam.Dev(a));
                    break;
                case "screen": // the game's window size, see Film/WindowSize
                    Plugin.L.LogInfo("screen: " + Film.WindowSize.Command(a));
                    break;
                default:
                    Plugin.L.LogWarning("devcmd: unknown command " + a[0]);
                    break;
            }
        }

        private static void Actions()
        {
            var pa = GameInput.Instance != null ? GameInput.Instance.playerActions : null;
            if (pa == null) { Plugin.L.LogWarning("actions: no PlayerActions"); return; }
            var sb = new System.Text.StringBuilder("actions:\n");
            var actions = pa.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                var names = new System.Collections.Generic.List<string>();
                var bindings = action.Bindings;
                for (int j = 0; j < bindings.Count; j++)
                {
                    var bnd = bindings[j];
                    if (bnd != null) names.Add($"{bnd.Name} [{bnd.BindingSourceType}]");
                }
                sb.AppendLine($"  {action.Name}: {string.Join(", ", names)}");
            }
            Plugin.L.LogInfo(sb.ToString());
        }

        private static void Probe()
        {
            string Count<T>() where T : UnityEngine.Object => UnityEngine.Object.FindObjectsOfType<T>().Length.ToString();
            var sb = new System.Text.StringBuilder("probe:");
            sb.Append($" CameraControlVolume={Count<CameraControlVolume>()} CutsceneStarter={Count<CutsceneStarter>()} Minigame={Count<MinigameInteractableItem>()}");
            sb.Append($" TeleportVolume={Count<TeleportVolume>()} SceneLoader.IsLoading={SceneLoader.IsLoading()}");
            sb.Append($" GarageUI={Count<GarageUI>()} ApartmentUpgradeUI={Count<ApartmentUpgradeUI>()} RaceManager={Count<RaceManager>()}");
            sb.Append($" GasStation={Count<GasStation>()} TradeOffer={Count<TradeOffer>()} IncomingCall={Count<IncomingCall>()} Credits={Count<Credits>()}");
            var effects = new System.Collections.Generic.List<string>();
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null) continue;
                foreach (var c in cam.GetComponents<Behaviour>())
                {
                    if (c == null) continue;
                    string n = c.GetIl2CppType().Name;
                    if (n.StartsWith("CameraFilterPack")) effects.Add($"{cam.name}:{n}{(c.enabled ? "" : "(off)")}");
                }
            }
            sb.Append(" camera filters: " + (effects.Count > 0 ? string.Join(", ", effects) : "none"));
            var post = new System.Collections.Generic.List<string>();
            foreach (var v in UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
            {
                var profile = v != null ? v.sharedProfile : null;
                if (profile == null || profile.settings == null) continue;
                foreach (var s in profile.settings)
                    if (s != null) post.Add($"{v.name}:{s.GetIl2CppType().Name}{(s.active ? "" : "(inactive)")}");
            }
            sb.Append("; post-process volumes: " + (post.Count > 0 ? string.Join(", ", post) : "none"));
            Plugin.L.LogInfo(sb.ToString());
        }

        /// <summary>Matching properties of an interop object with values (fields show as properties).</summary>
        private static string Props(object o, string pattern)
        {
            if (o == null) return "null";
            var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var sb = new System.Text.StringBuilder();
            foreach (var p in o.GetType().GetProperties())
            {
                if (!rx.IsMatch(p.Name) || p.GetIndexParameters().Length > 0) continue;
                string v;
                try
                {
                    object x = p.GetValue(o);
                    if (x is TMPro.TMP_Text tmp) v = $"TMP '{tmp.text}'";
                    else if (x is UnityEngine.UI.Text ut) v = $"Text '{ut.text}'";
                    else if (x is Component c) v = $"{x.GetType().Name} '{c.name}' active {c.gameObject.activeInHierarchy}";
                    else if (x is GameObject go) v = $"GameObject '{go.name}' active {go.activeInHierarchy}";
                    else v = x == null ? "null" : $"{x} ({x.GetType().Name})";
                }
                catch (Exception e) { v = "! " + e.GetType().Name; }
                if (v.Length > 160) v = v.Substring(0, 160) + "...";
                sb.Append($" {p.Name}={v};");
            }
            return sb.ToString();
        }

        private static void UiMap()
        {
            var L = Plugin.L;
            var g = Global.Instance;
            L.LogInfo("uimap Global:" + Props(g, "hud|money|lims|credit|cash|fuel|separate|interior|location|district|quest|notification|prompt"));
            L.LogInfo("uimap PlayerCar:" + Props(g?.playerCar, "fuel|gas|tank|damage|health|boost"));
            var player = Player.Instance;
            L.LogInfo("uimap Player:" + Props(player, "speed|sneak|interaction|inputVector|isGrounded|isInApartment"));
            var inter = player != null ? player.interaction : null;
            L.LogInfo("uimap Interaction:" + Props(inter, "closest|candidates|timeout"));
            if (inter != null && inter.closestObject != null)
            {
                var co = inter.closestObject;
                var cam = Camera.main;
                L.LogInfo($"uimap closest: '{co.name}' type {co.GetIl2CppType().FullName} at {co.transform.position} ({(cam != null ? Vector3.Distance(cam.transform.position, co.transform.position) : -1f):0.0} m from the eye)");
            }
            foreach (var d in UnityEngine.Object.FindObjectsOfType<DialogHUD>())
                L.LogInfo($"uimap DialogHUD '{d.name}' active {d.isActiveAndEnabled}:" + Props(d, "text|name|portrait|currentLine|character|dialog$|blockSkipping|canvasGroup|phase"));
            foreach (var q in UnityEngine.Object.FindObjectsOfType<Quest>())
                L.LogInfo($"uimap Quest '{q.name}' active {q.isActiveAndEnabled}:" + Props(q, ".") + " questText:" + Props(q.questText, "."));
            foreach (var lt in UnityEngine.Object.FindObjectsOfType<LocationText>())
                L.LogInfo($"uimap LocationText '{lt.name}' active {lt.isActiveAndEnabled}:" + Props(lt, "."));
            var mm = Minimap.Instance;
            L.LogInfo("uimap Minimap:" + Props(mm, "mapSmall|InVehicleCockpit|interiorTransformCopy|exteriorTransformParent|hiddenInScene|t_In"));

            // HUD parts on the panel texture, two levels deep
            var ui = VR.CameraDriver.UiCamera;
            if (ui == null) { L.LogInfo("uimap: no UI camera"); return; }
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceCamera || canvas.worldCamera != ui) continue;
                var sb = new System.Text.StringBuilder($"uimap canvas '{canvas.name}' order {canvas.sortingOrder}:");
                void Walk(Transform t, int depth)
                {
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        if (!c.gameObject.activeInHierarchy) continue;
                        var rt = c.GetComponent<RectTransform>();
                        if (rt == null) continue;
                        var r = rt.rect;
                        Vector2 a = RectTransformUtility.WorldToScreenPoint(ui, rt.TransformPoint(new Vector3(r.xMin, r.yMin)));
                        Vector2 b = RectTransformUtility.WorldToScreenPoint(ui, rt.TransformPoint(new Vector3(r.xMax, r.yMax)));
                        var cg = c.GetComponent<CanvasGroup>();
                        sb.Append($"\n  {new string(' ', depth * 2)}{c.name} [{a.x:0},{a.y:0} - {b.x:0},{b.y:0}]{(cg != null ? $" alpha {cg.alpha:0.00}" : "")}");
                        if (depth < 2) Walk(c, depth + 1);
                    }
                }
                Walk(canvas.transform, 0);
                L.LogInfo(sb.ToString());
            }

            // canvas scales (pinned to 1080p), then the parts the layout frames and cuts
            var scales = new System.Text.StringBuilder($"uimap scale: window {Screen.width}x{Screen.height};");
            foreach (var canvas in UI.VRUI.UiCanvases)
            {
                if (canvas == null) continue;
                var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                scales.Append($" '{canvas.name}' {canvas.scaleFactor:0.00}{(scaler != null ? $" ({scaler.m_UiScaleMode}, reference {scaler.m_ReferenceResolution.x:0}x{scaler.m_ReferenceResolution.y:0})" : "")};");
            }
            L.LogInfo(scales.ToString());
            string Part(string label, Component c)
            {
                var rt = c != null ? c.GetComponent<RectTransform>() : null;
                if (rt == null) return $" {label} none;";
                UI.UiPicture.StartScan(); // each part on its own, though parts overlap
                var drawn = UI.UiPicture.Content(c.transform, ui, null, true);
                return drawn.width > 0f ? $" {label} {UI.UiPicture.Describe(drawn)};" : $" {label} (hidden) {UI.UiPicture.Describe(UI.UiPicture.ScreenRect(rt, ui))};";
            }
            Transform Of(GameObject go) => go != null ? go.transform : null;
            var dlg = g?.dialogUI;
            if (dlg != null)
                L.LogInfo($"uimap dialogue: showing {dlg.IsActiveAndNotFadingOut()};" + Part("box", dlg.exteriorCanvasGroupGlobal) + Part("cockpit box", dlg.interiorCanvasGroupGlobal) +
                          Part("portrait", dlg.portrait) + Part("name", dlg.nameObj) + Part("text", dlg.textField) + Part("trade offers", Of(dlg.tradeOfferParent)) +
                          Part("offer description", dlg.offerDescription) + Part("done", Of(dlg.doneButton)) + Part("sell", Of(dlg.sellButton)));
            var inv = g?.inventoryUI;
            if (inv != null)
                L.LogInfo($"uimap inventory: enabled {inv.isActiveAndEnabled};" + Part("window", inv.window) + Part("description", Of(inv.descriptionParent)));
            var call = g?.incomingCallUI;
            if (call != null) L.LogInfo($"uimap call: enabled {call.isActiveAndEnabled};" + Part("box", call.exteriorCanvasGroupGlobal) + Part("cockpit box", call.interiorCanvasGroupGlobal));
            var parents = g?.hudParents;
            if (parents != null) L.LogInfo("uimap unlock:" + Part("popup", parents.unlockPopup));
            var mapFull = Minimap.Instance != null ? Minimap.Instance.mapFull : null;
            if (mapFull != null) L.LogInfo("uimap full map:" + Part("map", mapFull));
            L.LogInfo("uimap layout: " + UI.VRUI.DescribeView());
        }

        private static void ItemProbe()
        {
            var L = Plugin.L;
            Vector3 me = VR.CameraDriver.HeadWorldPosition;
            string Where(Component c) => $"'{c.name}' {(c.gameObject.activeInHierarchy ? "active" : "inactive")} {Vector3.Distance(me, c.transform.position):0} m";
            var sb = new System.Text.StringBuilder("itemprobe:");
            int pickups = 0, generic = 0;
            foreach (var i in Resources.FindObjectsOfTypeAll<PickupItem>())
                if (i != null && i.gameObject.scene.IsValid()) { if (pickups++ < 12) sb.Append($" [pickup {Where(i)}]"); }
            foreach (var i in Resources.FindObjectsOfTypeAll<GenericInteractableItem>())
                if (i != null && i.gameObject.scene.IsValid()) { if (generic++ < 12) sb.Append($" [generic {Where(i)}]"); }
            sb.Append($" totals: {pickups} pickups, {generic} generic.");
            L.LogInfo(sb.ToString());
            int layer = LayerMask.NameToLayer("Interactable");
            sb.Clear().Append($"itemprobe layer Interactable={layer}:");
            if (layer >= 0)
            {
                var hits = Physics.OverlapSphere(me, 40f, 1 << layer, QueryTriggerInteraction.Collide);
                for (int i = 0; i < hits.Length && i < 30; i++)
                {
                    var c = hits[i];
                    if (c == null) continue;
                    var kinds = new System.Collections.Generic.List<string>();
                    var mbs = c.GetComponentsInParent<MonoBehaviour>(true);
                    for (int k = 0; k < mbs.Length && kinds.Count < 4; k++) if (mbs[k] != null) kinds.Add(mbs[k].GetIl2CppType().Name);
                    sb.Append($" ['{c.name}' {Vector3.Distance(me, c.transform.position):0} m trigger {c.isTrigger}: {string.Join("/", kinds)}]");
                }
                sb.Append($" ({hits.Length} colliders within 40 m)");
            }
            L.LogInfo(sb.ToString());
        }

        private static void GotoItem()
        {
            var p = Player.Instance;
            if (p == null) { Plugin.L.LogWarning("gotoitem: no player"); return; }
            Vector3 me = p.transform.position;
            Transform best = null;
            float bestD = float.MaxValue;
            int n = 0;
            void Consider(Component c)
            {
                if (c == null || !c.gameObject.activeInHierarchy) return;
                n++;
                float d = (c.transform.position - me).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c.transform; }
            }
            foreach (var i in UnityEngine.Object.FindObjectsOfType<PickupItem>()) Consider(i);
            foreach (var i in UnityEngine.Object.FindObjectsOfType<GenericInteractableItem>()) Consider(i);
            if (best == null) { Plugin.L.LogWarning("gotoitem: no item loaded"); return; }
            Plugin.L.LogInfo($"gotoitem: '{best.name}' of {n} items, {Mathf.Sqrt(bestD):0} m away; " + WalkUpTo(p, best.position));
        }

        /// <summary>HOVA parking, routine and view-switch readiness.</summary>
        private static string HovaState(Global g)
        {
            var car = g != null ? g.playerCar : null;
            if (car == null) return "no HOVA";
            try
            {
                var volume = car.parkingVolume;
                bool ready = InputBridge.HovaReady(car, out string busy);
                string climb = InputBridge.ClimbMeasured ? InputBridge.Climb.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "not measured (only while a cockpit switch may be wanted)";
                return $"HOVA in parking volume {(volume != null ? "'" + volume.name + "'" : "none")}, in a routine {car.IsInRoutine()}, " +
                       $"passage flag {car.isInLift}, climb {climb}, ready for a view switch {ready}{(ready ? "" : " (" + busy + ")")}";
            }
            catch (Exception e) { return "HOVA state unreadable (" + e.Message + ")"; }
        }

        /// <summary>Puts Rania 1 m from the HOVA's hull, facing it, so the game offers to get in.</summary>
        private static void WalkToHova()
        {
            var p = Player.Instance;
            var car = Global.Instance != null ? Global.Instance.playerCar : null;
            if (p == null || car == null) { Plugin.L.LogWarning("board: no player or no HOVA"); return; }
            Vector3 me = p.transform.position;
            var hull = car.vehicleCollider;
            Vector3 centre = car.transform.position;
            // bounds: ClosestPoint refuses concave mesh colliders
            Vector3 near = hull != null && hull.enabled ? hull.bounds.ClosestPoint(me) : centre;
            Vector3 away = me - near; away.y = 0f;
            if (away.sqrMagnitude < 0.01f) { away = me - centre; away.y = 0f; }
            if (away.sqrMagnitude < 0.01f) away = -car.transform.right;
            Vector3 spot = near + away.normalized * 1f;
            Plugin.L.LogInfo($"board: Rania was {Vector3.Distance(me, near):0.0} m from the HOVA's hull ({(hull != null ? "'" + hull.name + "' bounds" : "no collider, its centre")}, HOVA at {centre}); " +
                             WalkUpTo(p, spot, new Vector3(near.x, spot.y, near.z)) + $" View {VR.CameraDriver.Mode}.");
        }

        /// <summary>Puts Rania 1.6 m from a point, feet on the ground, facing it.</summary>
        private static string WalkUpTo(Player p, Vector3 target)
        {
            Vector3 flat = p.transform.position - target; flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.back;
            return WalkUpTo(p, target + flat.normalized * 1.6f, target);
        }

        /// <summary>Puts Rania at a spot, feet on the ground, facing a point.</summary>
        private static string WalkUpTo(Player p, Vector3 spot, Vector3 target)
        {
            if (Physics.Raycast(spot + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) spot = hit.point;
            var cc = p.charController;
            if (cc != null) cc.enabled = false;
            p.transform.position = spot;
            if (cc != null) cc.enabled = true;
            // the head catches up next frame
            Vector3 to = target - spot; to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) to = Vector3.forward;
            float delta = Mathf.DeltaAngle(VR.CameraDriver.Yaw(VR.CameraDriver.HeadWorldRotation), Quaternion.LookRotation(to).eulerAngles.y);
            VR.CameraDriver.Turn(delta);
            return $"Rania moved to {spot}, turned {delta:0} degrees.";
        }

        /// <summary>
        /// Puts Rania in the nearest trader's interaction trigger, facing the body (a shopkeeper's transform can be
        /// away from both). CheckTrader confirms the game offers them.
        /// </summary>
        private static void GotoTrader()
        {
            var p = Player.Instance;
            if (p == null) { Plugin.L.LogWarning("goto: no player"); return; }
            Vector3 me = p.transform.position;
            Character best = null;
            float bestD = float.MaxValue;
            int traders = 0;
            var people = UnityEngine.Object.FindObjectsOfType<Character>();
            for (int i = 0; i < people.Length; i++)
            {
                var c = people[i];
                if (c == null || !c.isActiveAndEnabled || !c.canTalkTo || c.itemOffers == null || c.itemOffers.Length == 0) continue;
                traders++;
                float d = (Body(c) - me).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) { Plugin.L.LogWarning($"goto: no trader among the {people.Length} people of the loaded scenes"); return; }

            Vector3 body = Body(best);
            Collider trigger = null;
            var colliders = best.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null || !col.enabled || !col.isTrigger) continue;
                if (trigger == null || col.bounds.size.sqrMagnitude > trigger.bounds.size.sqrMagnitude) trigger = col;
            }
            // inside the trigger, about 1 m from the body, on Rania's side
            Vector3 centre = trigger != null ? trigger.bounds.center : body;
            Vector3 away = me - body; away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.back;
            float reach = trigger != null ? Mathf.Clamp(Mathf.Min(trigger.bounds.extents.x, trigger.bounds.extents.z) * 0.6f, 0.6f, 1.2f) : 1.2f;
            Vector3 spot = new Vector3(centre.x, body.y, centre.z) + away.normalized * reach;
            Plugin.L.LogInfo($"goto: trader '{best.name}' ({best.itemOffers.Length} offers, buys items {best.buysItems}), nearest of {traders}, {Mathf.Sqrt(bestD):0} m away; " +
                             $"transform {best.transform.position}, body {body}, trigger {(trigger != null ? $"'{trigger.name}' at {trigger.bounds.center} size {trigger.bounds.size}" : "none")}.");
            Plugin.L.LogInfo($"goto: reached trader '{best.name}'; " + WalkUpTo(p, spot, body));
            traderToCheck = best;
            traderCheckAt = Time.unscaledTime + 1.5f;
        }

        /// <summary>Animated body position, else the transform.</summary>
        private static Vector3 Body(Character c) => c.anim != null ? c.anim.transform.position : c.transform.position;

        private static Character traderToCheck;
        private static float traderCheckAt;

        /// <summary>If the game doesn't offer the trader, hands it to Interaction as its trigger would.</summary>
        private static void CheckTrader()
        {
            if (traderToCheck == null || Time.unscaledTime < traderCheckAt) return;
            var c = traderToCheck;
            traderToCheck = null;
            var inter = Player.Instance != null ? Player.Instance.interaction : null;
            if (inter == null) { Plugin.L.LogWarning("goto: no interaction to check"); return; }
            bool Offered()
            {
                var o = inter.closestObject;
                return o != null && (o.transform.IsChildOf(c.transform) || c.transform.IsChildOf(o.transform));
            }
            string Closest() => inter.closestObject != null ? $"'{inter.closestObject.name}'" : "none";
            bool offered = Offered();
            Plugin.L.LogInfo($"goto: closest object {Closest()}; trader offered by the game: {offered}");
            if (offered) return;
            inter.AddObject(c.gameObject);
            inter.ForceUpdate();
            Plugin.L.LogInfo($"goto: trader handed to the game's Interaction; closest object {Closest()}; trader offered by the game: {Offered()}");
        }

        /// <summary>Walks to the nearest Interactable (150 m) named like the word, else any loaded object so named.</summary>
        private static void GotoNamed(string word)
        {
            var p = Player.Instance;
            int layer = LayerMask.NameToLayer("Interactable");
            if (p == null || layer < 0) { Plugin.L.LogWarning("goto: no player or no Interactable layer"); return; }
            Vector3 me = p.transform.position;
            var hits = Physics.OverlapSphere(me, 150f, 1 << layer, QueryTriggerInteraction.Collide);
            Transform best = null;
            float bestD = float.MaxValue;
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i];
                if (c == null) continue;
                if (names.Count < 30) names.Add(c.name);
                int depth = 0;
                for (var t = c.transform; t != null && depth < 3; t = t.parent, depth++)
                {
                    if (word.Length == 0 || t.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    float d = (c.transform.position - me).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c.transform; }
                    break;
                }
            }
            if (best == null && word.Length > 0)
            {
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null || t.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    float d = (t.position - me).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
            if (best == null) { Plugin.L.LogWarning($"goto: nothing named '{word}' among {hits.Length} interactables ({string.Join(", ", names)}) nor in the loaded scenes"); return; }
            Plugin.L.LogInfo($"goto: reached '{best.name}' {Mathf.Sqrt(bestD):0} m away; " + WalkUpTo(p, best.position));
        }

        private static void RainProbe()
        {
            var sb = new System.Text.StringBuilder("rainprobe:");
            void Overlay(string label, RainAndSheets r)
            {
                if (r == null) { sb.Append($" {label} none;"); return; }
                // only active: its parameters read back as garbage through interop
                sb.Append($" {label}: active {r.active};");
            }
            var g = Global.Instance;
            Overlay("overlay", g?.rain);
            var vi = g?.vehicleInterior;
            if (vi != null)
            {
                Overlay("cockpit overlay", vi.fxRain);
                sb.Append($" cockpit: rainEnabled {vi.rainEnabled}, splashes {(vi.fxSplashes != null ? vi.fxSplashes.active.ToString() : "none")}, rain parent {vi.interiorRainParent}, extra {vi.noRainParticlesExtra};");
            }
            sb.Append($" sky rain: downfall {UnityEngine.Object.FindObjectsOfType<Funly.SkyStudio.RainDownfallController>().Length}, splash {UnityEngine.Object.FindObjectsOfType<Funly.SkyStudio.RainSplashController>().Length}, weather {UnityEngine.Object.FindObjectsOfType<Funly.SkyStudio.WeatherController>().Length};");
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            int n = 0;
            foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
            {
                if (ps == null) continue;
                string name = ps.name.ToLowerInvariant();
                if (!(name.Contains("rain") || name.Contains("drop") || name.Contains("splash") || name.Contains("drip") || name.Contains("puddle") || name.Contains("wet"))) continue;
                n++;
                var t = ps.transform;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                sb.Append($" [ps '{ps.name}' under '{(t.parent != null ? t.parent.name : "-")}', playing {ps.isPlaying}, live {ps.particleCount}," +
                          $" {Vector3.Distance(t.position, eye):0.0} m from the eye (dy {t.position.y - eye.y:0.0}), layer {LayerMask.LayerToName(ps.gameObject.layer)}," +
                          $" render {(r != null ? r.renderMode.ToString() + (r.enabled ? "" : " off") : "none")}]");
            }
            sb.Append($" {n} rain particle systems.");
            Plugin.L.LogInfo(sb.ToString());
        }

        /// <summary>Toggles the game's camera effects, to find which differ between the eyes.</summary>
        private static void Effects(string which, string state)
        {
            var g = Global.Instance;
            if (g == null) { Plugin.L.LogWarning("fx: no game"); return; }
            Behaviour b = which switch
            {
                "skybox" => g.qualitySkyboxCamera,
                "extrapost" => g.qualityExtraPostProcess,
                "bloom" => g.qualityBloom,
                "volumetric" => g.qualityVolumetricCamera,
                "fxaa" => g.qualityFXAA,
                _ => null,
            };
            if (which == "list" || b == null)
            {
                var sb = new System.Text.StringBuilder("fx: ");
                sb.Append($"skybox={g.qualitySkyboxCamera?.enabled} extrapost={g.qualityExtraPostProcess?.enabled} bloom={g.qualityBloom?.enabled} volumetric={g.qualityVolumetricCamera?.enabled} fxaa={g.qualityFXAA?.enabled}; main camera:");
                if (g.mainCamera != null)
                    foreach (var c in g.mainCamera.GetComponents<Behaviour>())
                        if (c != null) sb.Append($" {c.GetIl2CppType().Name}{(c.enabled ? "" : "(off)")}");
                Plugin.L.LogInfo(sb.ToString());
                return;
            }
            b.enabled = state != "off";
            Plugin.L.LogInfo($"fx: {which} {(b.enabled ? "on" : "off")}");
        }

        private static int cutsAtCheck, cutChecks;
        private static Collider liftTriggerOff;
        private static float liftTriggerOffAt;

        /// <summary>Re-enables the trigger "lift call" turned off, once the passage ends (a minute at most).</summary>
        private static void RestoreLiftTrigger()
        {
            if (liftTriggerOff == null) return;
            float since = Time.unscaledTime - liftTriggerOffAt;
            if (since < 2f || (VR.TransitionGuard.PassageBusy && since < 60f)) return;
            liftTriggerOff.enabled = true;
            liftTriggerOff = null;
            Plugin.L.LogInfo("lift: the passage's trigger is on again.");
        }

        /// <summary>
        /// District passages ("lifts"), nearest first. enter puts the HOVA in the trigger so Lift.OnTriggerEnter
        /// fires as in flight, call runs PlayerCar.EnterLift directly.
        /// </summary>
        private static void Lifts(string action, int index)
        {
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            Vector3 from = car != null ? car.transform.position : VR.CameraDriver.HeadWorldPosition;
            var lifts = new System.Collections.Generic.List<Lift>();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Lift>())
                if (l != null && l.isActiveAndEnabled) lifts.Add(l);
            Vector3 Entry(Lift l) { var c = l.GetComponent<Collider>(); return c != null ? c.bounds.center : l.transform.position; }
            lifts.Sort((x, y) => (Entry(x) - from).sqrMagnitude.CompareTo((Entry(y) - from).sqrMagnitude));

            var sb = new System.Text.StringBuilder($"lift: {lifts.Count} passage(s) loaded");
            for (int i = 0; i < lifts.Count; i++)
            {
                var l = lifts[i];
                var c = l.GetComponent<Collider>();
                sb.Append($"\n  {i}: '{l.name}' {(Entry(l) - from).magnitude:0} m away, to scene {l.destination} exit '{l.exitID}', direction {l.direction}, " +
                          $"trigger {(c != null ? $"{c.bounds.center} size {c.bounds.size}{(c.isTrigger ? "" : " (not a trigger)")}" : "none")}, " +
                          $"end {(l.endPoint != null ? l.endPoint.position.ToString() : "none")}, camera '{(l.camera != null ? l.camera.name : "none")}'");
            }
            Plugin.L.LogInfo(sb.ToString());
            if (action == "list") return;

            if (lifts.Count == 0) { Plugin.L.LogWarning("lift: no district passage in the loaded scenes"); return; }
            if (car == null || !Global.IsInVehicle) { Plugin.L.LogWarning("lift: fly the HOVA first (a passage takes the HOVA, not Rania)"); return; }
            var lift = lifts[Mathf.Clamp(index, 0, lifts.Count - 1)];
            switch (action)
            {
                case "enter":
                    Vector3 p = Entry(lift);
                    var rb = car.rBody;
                    if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = p; }
                    car.transform.position = p;
                    Plugin.L.LogInfo($"lift: HOVA put inside the trigger of '{lift.name}' at {p}; the game's own trigger takes it from there.");
                    break;
                case "call":
                    // the routine pulls the HOVA through this trigger, which would call EnterLift again
                    var trigger = lift.GetComponent<Collider>();
                    if (trigger != null && trigger.enabled) { trigger.enabled = false; liftTriggerOff = trigger; liftTriggerOffAt = Time.unscaledTime; }
                    Plugin.L.LogInfo($"lift: PlayerCar.EnterLift('{lift.name}') called directly, its trigger off until the passage is over.");
                    car.EnterLift(lift);
                    break;
                default:
                    Plugin.L.LogWarning("lift: list, enter or call");
                    break;
            }
        }

        /// <summary>Puts the HOVA in the nearest parking volume, so Interact runs the game's parking.</summary>
        private static void MoveCarToNearestParking(bool nearItem = false)
        {
            var car = Global.Instance?.playerCar;
            if (car == null) { Plugin.L.LogWarning("park: no player car"); return; }
            // item: park near the nearest item (items wake up with their area)
            Vector3 from = car.transform.position;
            if (nearItem)
            {
                float itemD = float.MaxValue;
                foreach (var i in Resources.FindObjectsOfTypeAll<PickupItem>())
                {
                    if (i == null || !i.gameObject.scene.IsValid()) continue;
                    float d = (i.transform.position - car.transform.position).sqrMagnitude;
                    if (d < itemD) { itemD = d; from = i.transform.position; }
                }
                Plugin.L.LogInfo($"park item: nearest item at {from} ({Mathf.Sqrt(itemD):0} m away).");
            }
            ParkVolume best = null;
            float bestD = float.MaxValue;
            foreach (var v in UnityEngine.Object.FindObjectsOfType<ParkVolume>())
            {
                if (v == null || v.volumeCollider == null || !v.isActiveAndEnabled) continue;
                float d = (v.volumeCollider.bounds.center - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = v; }
            }
            if (best == null) { Plugin.L.LogWarning("park: no parking volume loaded"); return; }
            Vector3 p = best.volumeCollider.bounds.center;
            MoveCar(car, p);
            Plugin.L.LogInfo($"park: car moved to '{best.name}' at {p} ({Mathf.Sqrt(bestD):F0} m away)");
        }

        /// <summary>The HOVA to a point, at rest (body and transform).</summary>
        internal static void MoveCar(PlayerCar car, Vector3 p)
        {
            var rb = car.rBody;
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = p; }
            car.transform.position = p;
        }

        /// <summary>Lists the game's button-prompt sprites: TextMeshPro sprite assets and Global.controlIcons.</summary>
        private static void DumpPromptSprites()
        {
            var sb = new System.Text.StringBuilder("sprites:");
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMPro.TMP_SpriteAsset>())
            {
                sb.AppendLine().Append($"  asset '{asset.name}':");
                var table = asset.spriteCharacterTable;
                if (table != null)
                    for (int i = 0; i < table.Count; i++) sb.Append(' ').Append(table[i].name);
                var legacy = asset.spriteInfoList;
                if (legacy != null && legacy.Count > 0)
                {
                    sb.Append(" | legacy:");
                    for (int i = 0; i < legacy.Count; i++) sb.Append(' ').Append(legacy[i].name);
                }
            }
            var g = Global.Instance;
            if (g != null && g.controlIcons != null)
            {
                sb.AppendLine().Append("  Global.controlIcons:");
                for (int i = 0; i < g.controlIcons.Length; i++) sb.Append($" [{i}]{(g.controlIcons[i] ? g.controlIcons[i].name : "-")}");
            }
            var gi = GameInput.Instance;
            if (gi != null && gi.playerActions != null)
            {
                sb.AppendLine().Append("  prompts:");
                var actions = gi.playerActions.Actions;
                for (int i = 0; i < actions.Count; i++)
                {
                    string s = null;
                    try { s = gi.GetSpriteFromPlayerAction(actions[i]); } catch (Exception e) { s = "!" + e.Message; }
                    sb.Append($" {actions[i].Name}='{s}'");
                }
            }
            Plugin.L.LogInfo(sb.ToString());
        }

        /// <summary>Saves the VR UI texture (what the panel shows) next to the BepInEx log.</summary>
        private static void SnapUi(string name)
        {
            var rt = UI.VRUI.Texture;
            if (rt == null) { Plugin.L.LogWarning("uisnap: no UI texture yet"); return; }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var bytes = ImageConversion.EncodeToPNG(tex);
            UnityEngine.Object.Destroy(tex);
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"uisnap-{name}.png");
            File.WriteAllBytes(path, bytes);
            Plugin.L.LogInfo("uisnap: " + path);
        }

        private static System.Collections.Generic.Dictionary<long, string> pointerNames;

        /// <summary>Names native addresses from the method pointers of Unity's interop types.</summary>
        private static unsafe string NameOfPointer(IntPtr p)
        {
            if (pointerNames == null)
            {
                pointerNames = new System.Collections.Generic.Dictionary<long, string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = asm.GetName().Name;
                    if (an != "UnityEngine.CoreModule" && an != "UnityEngine.UI") continue; // engine types only, safe cctors
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                        foreach (var f in t.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                        {
                            if (!f.Name.StartsWith("NativeMethodInfoPtr_") || f.FieldType != typeof(IntPtr)) continue;
                            try
                            {
                                var mi = (IntPtr)f.GetValue(null);
                                if (mi == IntPtr.Zero) continue;
                                long code = (long)*(IntPtr*)mi; // Il2CppMethodInfo.methodPointer is the first field
                                if (code != 0 && !pointerNames.ContainsKey(code))
                                    pointerNames[code] = t.FullName + "." + f.Name.Substring("NativeMethodInfoPtr_".Length);
                            }
                            catch { }
                        }
                }
                Plugin.L.LogInfo($"xref: indexed {pointerNames.Count} native method pointers");
            }
            return pointerNames.TryGetValue((long)p, out var n) ? "= " + n : "";
        }

        /// <summary>Lists what a game method calls (methods, string literals). Poor man's decompiler.</summary>
        private static void Xref(string typeName, string methodName)
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(typeName, false);
                if (t != null) break;
            }
            if (t == null) { Plugin.L.LogWarning("xref: type not found " + typeName); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != methodName) continue;
                sb.AppendLine($"xref {typeName}.{m.Name}({m.GetParameters().Length} args):");
                foreach (var x in Il2CppInterop.Common.XrefScans.XrefScanner.XrefScan(m))
                {
                    if (x.Type == Il2CppInterop.Common.XrefScans.XrefType.Method)
                    {
                        var r = Il2CppInterop.Runtime.XrefScans.XrefInstanceExtensions.TryResolve(x);
                        sb.AppendLine(r != null ? $"   call {r.DeclaringType?.FullName}.{r.Name}" : $"   call 0x{(long)x.Pointer:X} {NameOfPointer(x.Pointer)}");
                    }
                    else
                    {
                        object o = null;
                        try { o = Il2CppInterop.Runtime.XrefScans.XrefInstanceExtensions.ReadAsObject(x); } catch { }
                        if (o != null) sb.AppendLine($"   global {o}");
                    }
                }
            }
            Plugin.L.LogInfo(sb.ToString());
        }
    }
}
#endif
