using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Patches
{
    /// <summary>
    /// With HudOnWindshield, the cockpit's dialogue and call boxes (low on the dashboard) are hidden and the HUD's
    /// own boxes show their content instead. Two copies at once was hard to read.
    /// </summary>
    internal static class CockpitBoxes
    {
        private static bool logged;
        private static string lastText = "", lastName = "";
        private static IntPtr lastPortrait;
        private static readonly Dictionary<IntPtr, List<(CanvasGroup hud, CanvasGroup cockpit)>> groups =
            new Dictionary<IntPtr, List<(CanvasGroup, CanvasGroup)>>();
        // cockpit boxes we hid, given back when the HUD lets go
        private static readonly HashSet<IntPtr> hidden = new HashSet<IntPtr>();
        private static readonly List<(IntPtr ptr, CanvasGroup group)> hiddenGroups = new List<(IntPtr, CanvasGroup)>();
        private static readonly HashSet<IntPtr> loggedHide = new HashSet<IntPtr>(), loggedGiveBack = new HashSet<IntPtr>();
        private static readonly HashSet<string> failed = new HashSet<string>();
        private static bool givingBack;

        /// <summary>VR, HudOnWindshield, HUD shown, cockpit view. The cockpit flag outlives getting out, hence IsInVehicle.</summary>
        public static bool KeepOnHud()
        {
            var g = Global.Instance;
            bool keep = VR.VRSession.Active && VRConfig.HudOnWindshield.Value && g != null && Global.IsInVehicle && Global.IsInVehicleCockpit && !g.optionDisableHUD;
            if (keep && !logged)
            {
                logged = true;
                Plugin.L.LogInfo("Cockpit: the dialogue and call boxes show on the HUD only, the game's on the dashboard hidden (HudOnWindshield).");
            }
            return keep;
        }

        public static bool Hidden(CanvasGroup cockpit) => cockpit != null && hidden.Contains(cockpit.Pointer);

        /// <summary>Takes the box or gives it back. After a switch the game already set both, so no second ChangeVehicleState.</summary>
        public static void Dialog(DialogHUD d, bool switched)
        {
            if (d == null || givingBack) return;
            var cockpit = d.interiorCanvasGroupGlobal;
            if (Take(d.exteriorCanvasGroupGlobal, cockpit, d.textField, d.textFieldInterior, d.nameObj, d.nameObjInterior, d.portrait, d.portraitInterior)) return;
            if (!Forget(cockpit)) return;
            if (!switched)
            {
                givingBack = true;
                try { d.ChangeVehicleState(); }
                finally { givingBack = false; }
            }
            GaveBack(cockpit);
        }

        public static void Call(IncomingCall c, bool switched)
        {
            if (c == null || givingBack) return;
            var cockpit = c.interiorCanvasGroupGlobal;
            if (Take(c.exteriorCanvasGroupGlobal, cockpit, null, null, c.nameObj, c.nameObjInterior, c.portrait, c.portraitInterior)) return;
            if (!Forget(cockpit)) return;
            if (!switched)
            {
                givingBack = true;
                try { c.ChangeVehicleState(); }
                finally { givingBack = false; }
            }
            GaveBack(cockpit);
        }

        /// <summary>False if it wasn't hidden.</summary>
        private static bool Forget(CanvasGroup cockpit)
        {
            if (cockpit == null || !hidden.Remove(cockpit.Pointer)) return false;
            for (int i = hiddenGroups.Count - 1; i >= 0; i--)
                if (hiddenGroups[i].ptr == cockpit.Pointer) hiddenGroups.RemoveAt(i);
            return true;
        }

        // logged once per box, it happens on every switch out of cockpit view
        private static void GaveBack(CanvasGroup cockpit)
        {
            if (loggedGiveBack.Add(cockpit.Pointer))
                Plugin.L.LogInfo($"Cockpit: '{cockpit.name}' given back to the game (alpha {cockpit.alpha:0.00} as it sets it now; logged once).");
        }

        /// <summary>LateUpdate: re-hides boxes shown since Update (the instant OnChangeVehicleState isn't patched).</summary>
        public static void Enforce()
        {
            if (hiddenGroups.Count == 0) return;
            try
            {
                if (!KeepOnHud()) return; // given back at their next Update
                for (int i = hiddenGroups.Count - 1; i >= 0; i--)
                {
                    var (ptr, group) = hiddenGroups[i];
                    if (group == null) { hidden.Remove(ptr); hiddenGroups.RemoveAt(i); continue; } // scene unloaded
                    if (group.alpha > 0f) group.alpha = 0f;
                }
            }
            catch (Exception e) { Failed("late update", e); }
        }

        /// <summary>Logs the first failure of each kind.</summary>
        public static void Failed(string where, Exception e)
        {
            if (failed.Add(where)) Plugin.L.LogWarning($"Cockpit boxes ({where}): {e.Message}; logged once, the game's boxes left as it set them.");
        }

        /// <summary>Lends the cockpit box's alphas to the HUD box, then hides it. False when the HUD isn't taking boxes.</summary>
        private static bool Take(CanvasGroup hud, CanvasGroup cockpit, TMP_Text hudText, TMP_Text cockpitText,
                                 TMP_Text hudName, TMP_Text cockpitName, Image hudPortrait, Image cockpitPortrait)
        {
            if (hud == null || cockpit == null || !KeepOnHud()) return false;
            if (!groups.TryGetValue(hud.Pointer, out var pairs))
            {
                pairs = Pair(hud.transform, cockpit.transform);
                groups[hud.Pointer] = pairs;
                Plugin.L.LogInfo($"Cockpit: '{cockpit.name}' shown in '{hud.name}' ({pairs.Count} matching canvas groups).");
            }
            foreach (var (h, c) in pairs)
                if (h != null && c != null && c.alpha > h.alpha) h.alpha = c.alpha;
            Lend(cockpitText, hudText);
            Lend(cockpitName, hudName);
            if (hudPortrait != null && cockpitPortrait != null && cockpitPortrait.m_Color.a > hudPortrait.m_Color.a)
            {
                var color = hudPortrait.m_Color;
                color.a = cockpitPortrait.m_Color.a;
                hudPortrait.color = color;
            }
            if (cockpit.alpha > 0f) cockpit.alpha = 0f;
            if (hidden.Add(cockpit.Pointer))
            {
                hiddenGroups.Add((cockpit.Pointer, cockpit));
                if (loggedHide.Add(cockpit.Pointer))
                    Plugin.L.LogInfo($"Cockpit: '{cockpit.name}' on the dashboard hidden, '{hud.name}' on the HUD shows it (logged once).");
            }
            return true;
        }

        private static void Lend(TMP_Text from, TMP_Text to)
        {
            if (from == null || to == null) return;
            float a = from.m_fontColor.a;
            if (a > to.m_fontColor.a) to.alpha = a;
        }

        /// <summary>Pairs canvas groups by path (both boxes are built alike).</summary>
        private static List<(CanvasGroup, CanvasGroup)> Pair(Transform hudRoot, Transform cockpitRoot)
        {
            var pairs = new List<(CanvasGroup, CanvasGroup)>
            {
                (hudRoot.GetComponent<CanvasGroup>(), cockpitRoot.GetComponent<CanvasGroup>()),
            };
            var inside = cockpitRoot.GetComponentsInChildren<CanvasGroup>(true);
            for (int i = 0; i < inside.Length; i++)
            {
                var c = inside[i];
                if (c == null || c.transform.Pointer == cockpitRoot.Pointer) continue;
                string path = c.name;
                for (var t = c.transform.parent; t != null && t.Pointer != cockpitRoot.Pointer; t = t.parent) path = t.name + "/" + path;
                var match = hudRoot.Find(path);
                var h = match != null ? match.GetComponent<CanvasGroup>() : null;
                if (h != null) pairs.Add((h, c));
            }
            return pairs;
        }

        /// <summary>Copies cockpit-only lines to the HUD box. Changes only, so an old line never overwrites a newer one.</summary>
        public static void Mirror(DialogHUD d)
        {
            if (d == null) return;
            Copy(d.textFieldInterior, d.textField, ref lastText);
            Copy(d.nameObjInterior, d.nameObj, ref lastName);
            var from = d.portraitInterior != null ? d.portraitInterior.m_Sprite : null;
            if (from != null && from.Pointer != lastPortrait)
            {
                lastPortrait = from.Pointer;
                if (d.portrait != null) d.portrait.sprite = from;
            }
        }

        private static void Copy(TMP_Text from, TMP_Text to, ref string last)
        {
            if (from == null || to == null) return;
            string text = from.text ?? "";
            if (text == last) return;
            last = text;
            if (text.Length > 0) to.text = text;
        }
    }

    [HarmonyPatch(typeof(DialogHUD), nameof(DialogHUD.Update))]
    internal static class DialogBoxShownOnHudPatch
    {
        private static void Postfix(DialogHUD __instance)
        {
            try { CockpitBoxes.Dialog(__instance, false); }
            catch (Exception e) { CockpitBoxes.Failed("dialogue box", e); }
        }
    }

    // the game picks the shown box on view change (delayed Invoke): retake or release right after
    [HarmonyPatch(typeof(DialogHUD), nameof(DialogHUD.ChangeVehicleState))]
    internal static class DialogBoxSwitchPatch
    {
        private static void Postfix(DialogHUD __instance)
        {
            try { CockpitBoxes.Dialog(__instance, true); }
            catch (Exception e) { CockpitBoxes.Failed("dialogue box switch", e); }
        }
    }

    [HarmonyPatch(typeof(IncomingCall), nameof(IncomingCall.Update))]
    internal static class CallBoxShownOnHudPatch
    {
        private static void Postfix(IncomingCall __instance)
        {
            try { CockpitBoxes.Call(__instance, false); }
            catch (Exception e) { CockpitBoxes.Failed("call box", e); }
        }
    }

    [HarmonyPatch(typeof(IncomingCall), nameof(IncomingCall.ChangeVehicleState))]
    internal static class CallBoxSwitchPatch
    {
        private static void Postfix(IncomingCall __instance)
        {
            try { CockpitBoxes.Call(__instance, true); }
            catch (Exception e) { CockpitBoxes.Failed("call box switch", e); }
        }
    }
}
