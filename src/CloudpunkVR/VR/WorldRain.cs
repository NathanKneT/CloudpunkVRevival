using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// On-foot rain. The game's RainAndSheets streaks are a screen overlay, stuck to the face in a headset.
    /// Rain = World hides them and spawns two copies (ahead, behind) of the cockpit's rain particles around the player.
    /// </summary>
    internal static class WorldRain
    {
        private static GameObject copy, back;
        private static Vector3 offset = new Vector3(0f, 3.5f, 0f); // eye to drops, cockpit frame
        private static Quaternion localRotation = Quaternion.identity;
        private static float nextSearch;
        private static bool warned;
        private static readonly Dictionary<IntPtr, bool> isStreaks = new Dictionary<IntPtr, bool>();

        public static bool HideStreaks => VRSession.Active && VRConfig.Rain.Value != RainMode.Overlay;

        /// <summary>Is this the RainAndSheets effect (cached per instance).</summary>
        public static bool IsStreaks(PostProcessEffectSettings s)
        {
            IntPtr p = s.Pointer;
            if (isStreaks.TryGetValue(p, out bool r)) return r;
            if (isStreaks.Count > 512) isStreaks.Clear(); // settings of unloaded scenes
            r = s.GetIl2CppType().Name == "RainAndSheets";
            isStreaks[p] = r;
            return r;
        }

        /// <summary>Before render, after the head is placed.</summary>
        public static void OnBeforeRender()
        {
            try { Place(); }
            catch (Exception e)
            {
                if (!warned) { warned = true; Plugin.L.LogWarning("World rain not shown: " + e.Message); }
                if (copy != null) copy.SetActive(false);
            }
        }

        /// <summary>The game's streaks are on (outside, not under cover).</summary>
        private static bool GameRainsHere()
        {
            var rain = Global.Instance?.rain;
            return rain != null && rain.active;
        }

        private static void Place()
        {
            var mode = CameraDriver.Mode;
            bool want = VRSession.Active && VRConfig.Rain.Value == RainMode.World && GameRainsHere()
                        && (mode == ViewMode.OnFoot || mode == ViewMode.Chase) && !TransitionGuard.Active;
            if (!want)
            {
                Show(copy, false);
                Show(back, false);
                return;
            }
            if (copy == null && !MakeCopy()) return;
            // turns with the body, not the head
            Vector3 head = CameraDriver.HeadWorldPosition;
            Quaternion turn = Quaternion.Euler(0f, CameraDriver.Yaw(CameraDriver.AnchorRotation), 0f);
            copy.transform.SetPositionAndRotation(head + turn * offset, turn * localRotation);
            Show(copy, true);
            if (back != null)
            {
                Quaternion behind = turn * Quaternion.Euler(0f, 180f, 0f);
                back.transform.SetPositionAndRotation(head + behind * offset, behind * localRotation);
                Show(back, true);
            }
        }

        private static void Show(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        /// <summary>Copies the cockpit's rain, keeping its offset from the driver's eye.</summary>
        private static bool MakeCopy()
        {
            if (Time.unscaledTime < nextSearch) return false;
            nextSearch = Time.unscaledTime + 2f;
            var vi = Global.Instance?.vehicleInterior;
            if (vi == null) return false;
            GameObject source = vi.interiorRainParent;
            if (source == null || source.GetComponentsInChildren<ParticleSystem>(true).Length == 0) source = FindByName();
            if (source == null) return false;

            Transform eye = CameraDriver.CockpitCamera != null ? CameraDriver.CockpitCamera.transform : vi.transform;
            Quaternion toCockpit = Quaternion.Inverse(Quaternion.Euler(0f, vi.transform.eulerAngles.y, 0f));
            offset = toCockpit * (source.transform.position - eye.position);
            localRotation = toCockpit * source.transform.rotation;

            copy = UnityEngine.Object.Instantiate(source);
            copy.name = "CloudpunkVR_WorldRain";
            UnityEngine.Object.DontDestroyOnLoad(copy);
            // drop the scripts, they'd run the cockpit logic twice
            var scripts = copy.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < scripts.Length; i++) UnityEngine.Object.Destroy(scripts[i]);
            copy.SetActive(true);
            var systems = copy.GetComponentsInChildren<ParticleSystem>(true);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i];
                ps.gameObject.SetActive(true);
                ps.Play(true);
                Vector3 local = toCockpit * (source.transform.TransformPoint(copy.transform.InverseTransformPoint(ps.transform.position)) - eye.position);
                sb.Append($" '{ps.name}' at {local}");
            }
            back = UnityEngine.Object.Instantiate(copy);
            back.name = "CloudpunkVR_WorldRainBehind";
            UnityEngine.Object.DontDestroyOnLoad(back);
            Plugin.L.LogInfo($"World rain: two copies (ahead, behind) of the cockpit's '{source.name}' ({systems.Length} particle systems, {scripts.Length} scripts left out), {offset} from the driver's eye;{sb}.");
            return true;
        }

        private static GameObject FindByName()
        {
            foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                if (ps == null || !ps.name.StartsWith("Particles_Rain_Refractive")) continue;
                var parent = ps.transform.parent;
                if (parent != null && parent.gameObject.scene.IsValid()) return parent.gameObject;
            }
            return null;
        }

        public static string Describe()
        {
            string streaks = GameRainsHere() ? (HideStreaks ? "the game's streaks are on, left out of the picture" : "the game's streaks are shown") : "no streaks from the game here";
            return $"rain {VRConfig.Rain.Value}: {streaks}; world drops {(copy == null ? "not made" : copy.activeSelf ? (back != null && back.activeSelf ? "falling ahead and behind" : "falling ahead") : "hidden")}";
        }
    }

    /// <summary>Hides the streaks at draw time so the game's own on/off still says where it rains.</summary>
    [HarmonyPatch(typeof(PostProcessEffectSettings), nameof(PostProcessEffectSettings.IsEnabledAndSupported))]
    internal static class NoRainStreaks
    {
        private static void Postfix(PostProcessEffectSettings __instance, ref bool __result)
        {
            if (__result && WorldRain.HideStreaks && WorldRain.IsStreaks(__instance)) __result = false;
        }
    }
}
