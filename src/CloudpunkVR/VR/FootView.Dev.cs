#if DEV
using System;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>Dev command `footview`.</summary>
    internal static partial class FootView
    {
        /// <summary>
        /// VRManager.LateUpdate: `footview fight` locks the camera behind Rania again with the game's SwitchCamera, after
        /// the game's updates, like a scene would.
        /// </summary>
        public static void LateUpdate()
        {
            if (Time.unscaledTime >= fightUntil) return;
            try
            {
                var p = Player.Instance;
                if (p != null && p.isActiveAndEnabled && p.FreeLook) p.SwitchCamera(false);
            }
            catch (Exception e) { fightUntil = 0f; Plugin.L.LogWarning("footview fight stopped: " + e.Message); }
        }

        /// <summary>
        /// footview [info] | check | third | orbit | tutorial | hold [off] | fight [s], each with an optional [label]
        /// that tags the lines that follow. third / orbit: the game's SwitchCamera, zoom 1 (a new game's); tutorial: third
        /// plus the game's camera tutorial coroutine; hold: no switches this run; fight: lock behind her every frame.
        /// </summary>
        public static string Dev(string[] a)
        {
            var g = Global.Instance;
            var p = Player.Instance;
            string cmd = a.Length > 1 ? a[1].ToLowerInvariant() : "info";
            if (cmd == "hold")
            {
                Hold = a.Length < 3 || a[2] != "off";
                devTag = null;
                return "switches " + (Hold ? "held for the run" : "on again") + "; " + Info(g, p);
            }
            if (g == null || p == null) return "no Rania: " + cmd + " needs her on foot";
            string label = a.Length > 2 ? a[a.Length - 1] : null;
            switch (cmd)
            {
                case "check":
                    Cameras(p);
                    bool first = p.FreeLook && p.intendedZoomFactor < FirstPersonBelow && Active(firstGo) && !Active(fixedGo) && !Active(orbitGo);
                    return $"check {label ?? ""}: {(first ? "first person" : "NOT first person")}; " + Info(g, p);
                case "third":
                case "orbit":
                    devTag = "footview " + cmd + (label != null ? " " + label : "");
                    p.intendedZoomFactor = 1f;
                    p.SwitchCamera(cmd == "orbit");
                    return (cmd == "third" ? "the game's camera put behind Rania (its SwitchCamera(false), zoom 1.00, as a new game starts)"
                                           : "the game's camera set orbiting Rania (its SwitchCamera(true), zoom 1.00, as after the flat game's unlock)") + "; " + Info(g, p);
                case "tutorial":
                    var panel = g.tutorialCamera != null ? g.tutorialCamera.gameObject : null;
                    if (panel != null && panel.activeSelf) return "the camera tutorial already shows; " + Info(g, p);
                    devTag = tutorialTag = "footview tutorial" + (label != null ? " " + label : "");
                    p.intendedZoomFactor = 1f;
                    p.SwitchCamera(false);
                    string before = Trigger(g.tutorialCameraTrigger);
                    MEC.Timing.RunCoroutine(g._DisplayTutorialCamera());
                    return $"the game's camera put behind Rania and its camera tutorial started (its own coroutine; trigger {before} before); " + Info(g, p);
                case "fight":
                    float s = a.Length > 2 && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 4f;
                    devTag = null;
                    fightTag = "footview fight" + (a.Length > 3 ? " " + a[3] : "");
                    fightUntil = Time.unscaledTime + s;
                    return $"the game's camera locked behind Rania again every frame for {s:0.0} s (its SwitchCamera(false), after the game's updates); " + Info(g, p);
                default:
                    return Info(g, p);
            }
        }

        private static string Info(Global g, Player p)
        {
            if (p == null) return "no Rania";
            Cameras(p);
            string tutorials = g == null ? "" :
                $"; tutorials: camera {(cameraPanel != null && cameraPanel.activeSelf ? "showing" : "hidden")} (trigger {Trigger(g.tutorialCameraTrigger)}), " +
                $"zoom {(zoomPanel != null && zoomPanel.activeSelf ? "showing" : "hidden")} (trigger {Trigger(g.tutorialCameraTriggerZoom)})";
            return $"camera {Describe(p)}; cameras on: fixed {Active(fixedGo)}, orbit {Active(orbitGo)}, first person {Active(firstGo)}{tutorials}; " +
                   $"setting {(VRConfig.FirstPersonOnFoot.Value ? "on" : "off")}, switches {switchNo} ({zoomBacks} zoomed back), failed {failures}, left alone {leftAlone}, " +
                   $"waiting {(waitingFor != null ? "yes" : "no")}, hold {Hold}, Square to the game {TutorialTakesSquare}";
        }
    }
}
#endif
