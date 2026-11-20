using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Input
{
    public enum PromptStyle { Auto, PlayStation, Xbox }

    /// <summary>
    /// PlayStation button prompts on PS VR2 Sense. The game's atlas already has them, the PC build just never uses them.
    /// Other controllers keep the Xbox letters.
    /// </summary>
    internal static class ButtonPrompts
    {
        private const string Atlas = "itematlas_volaris_";

        // Xbox icon -> PlayStation icon, indexes in the game's prompt atlas.
        private static readonly Dictionary<int, int> XboxToPs = new Dictionary<int, int>
        {
            [0] = 14,  // A -> Cross
            [1] = 12,  // B -> Circle
            [2] = 15,  // X -> Square
            [3] = 13,  // Y -> Triangle
            [4] = 16,  // left stick -> L3
            [5] = 17,  // right stick -> R3
            [6] = 27,  // LT -> L2
            [7] = 26,  // RT -> R2
            [10] = 20, // RB -> R1
            [11] = 21, // LB -> L1
        };

        private static readonly Dictionary<string, string> toPs = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> toXbox = new Dictionary<string, string>();
        private static Dictionary<string, Sprite> sprites;
        private static float nextSweep;
        private static bool loggedMissing;

        public static bool PlayStation { get; private set; }
        public static PromptStyle? Override; // developer command

        static ButtonPrompts()
        {
            foreach (var kv in XboxToPs)
            {
                toPs[Atlas + kv.Key] = Atlas + kv.Value;
                toXbox[Atlas + kv.Value] = Atlas + kv.Key;
            }
        }

        public static void Update()
        {
            SteamVRInput.RefreshControllerType();
            var style = Override ?? VRConfig.ButtonPrompts.Value;
            bool ps = style == PromptStyle.PlayStation ||
                      (style == PromptStyle.Auto && SteamVRInput.ControllerType == "playstation_vr2_sense");
            if (ps != PlayStation)
            {
                PlayStation = ps;
                nextSweep = 0f;
                nextFullScan = 0f;
                VirtualGamepad.SetStyle(ps);
                Plugin.L.LogInfo($"Button prompts: {(ps ? "PlayStation" : "Xbox")} (controllers: {(SteamVRInput.ControllerType.Length > 0 ? SteamVRInput.ControllerType : "none")}, setting {style}).");
            }
            ScanSlice();
            if (Time.unscaledTime >= nextSweep)
            {
                nextSweep = Time.unscaledTime + 0.25f;
                Sweep();
            }
        }

        private static readonly List<Image> tracked = new List<Image>();
        private static readonly HashSet<int> trackedIds = new HashSet<int>();
        private static float nextFullScan, menuRescanAt = -1f;
        private static int seenScenes = -1;
        private static bool wasInMenu, spritesStale;
        // last full scan, read a slice per frame: all at once stalls the headset
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Image> scanning;
        private static int scanIndex;
        private const int ScanSliceSize = 150;

        private static bool InMenu()
        {
            if (VR.CameraDriver.Mode == VR.ViewMode.Menu) return true;
            try
            {
                var g = Global.Instance;
                return g != null && (g.IsInMenu() || g.HasBigWindowOpen());
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Re-skins prompt icons, including plain menu images that aren't InputIcons. Full scan on scene load,
        /// menu open (+1 s), then every 2 s in menus or 10 s otherwise.
        /// </summary>
        private static void Sweep()
        {
            float now = Time.unscaledTime;
            bool inMenu = InMenu();
            if (inMenu && !wasInMenu)
            {
                nextFullScan = 0f;
                menuRescanAt = now + 1f;
            }
            wasInMenu = inMenu;
            if (menuRescanAt > 0f && now >= menuRescanAt)
            {
                menuRescanAt = -1f;
                nextFullScan = 0f;
            }
            if (VR.SceneWatch.Changed(ref seenScenes))
            {
                nextFullScan = 0f;
                spritesStale = true;
            }
            if (scanning == null && now >= nextFullScan)
            {
                nextFullScan = now + (inMenu ? 2f : 10f);
                tracked.RemoveAll(i => i == null);
                scanning = UnityEngine.Object.FindObjectsOfType<Image>();
                scanIndex = 0;
            }
            foreach (var image in tracked)
                if (image != null) Fix(image);
        }

        private static void ScanSlice()
        {
            if (scanning == null) return;
            int end = Math.Min(scanning.Length, scanIndex + ScanSliceSize);
            for (; scanIndex < end; scanIndex++)
            {
                var image = scanning[scanIndex];
                var s = image != null ? image.sprite : null;
                if (s == null || (!toPs.ContainsKey(s.name) && !toXbox.ContainsKey(s.name))) continue;
                if (trackedIds.Add(image.GetInstanceID()))
                {
                    tracked.Add(image);
                    Fix(image);
                }
            }
            if (scanIndex >= scanning.Length) scanning = null;
        }

        /// <summary>Dev check: prompt icons on screen and their atlas sprites.</summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder($"prompt icons ({(PlayStation ? "PlayStation" : "Xbox")}):");
            foreach (var image in tracked)
                if (image != null && image.isActiveAndEnabled && image.sprite != null)
                    sb.Append($" {image.transform.parent?.name}/{image.name}={image.sprite.name.Replace(Atlas, "#")}");
            return sb.ToString();
        }

        internal static void Fix(Image image)
        {
            if (image == null) return;
            var s = image.sprite;
            if (s == null) return;
            var map = PlayStation ? toPs : toXbox;
            if (!map.TryGetValue(s.name, out var wanted)) return;
            var replacement = FindSprite(wanted);
            if (replacement != null) image.sprite = replacement;
        }

        private static Sprite FindSprite(string name)
        {
            // the listing is slow: redo it only after a scene load, and only if a sprite is missing
            if (sprites == null || (!sprites.ContainsKey(name) && spritesStale))
            {
                spritesStale = false;
                sprites = new Dictionary<string, Sprite>();
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sp != null && sp.name.StartsWith(Atlas, StringComparison.Ordinal)) sprites[sp.name] = sp;
                // the atlas is also a TMP sprite asset
                foreach (var asset in Resources.FindObjectsOfTypeAll<TMPro.TMP_SpriteAsset>())
                {
                    var list = asset.spriteInfoList;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var info = list[i];
                        if (info?.sprite != null && info.sprite.name.StartsWith(Atlas, StringComparison.Ordinal)) sprites[info.sprite.name] = info.sprite;
                    }
                }
            }
            if (sprites.TryGetValue(name, out var found)) return found;
            if (!loggedMissing) { loggedMissing = true; Plugin.L.LogWarning($"Prompt icon {name} is not loaded; keeping the game's icon."); }
            return null;
        }
    }

    /// <summary>Swaps the icon right after the game sets it, so the wrong one never shows for a frame.</summary>
    [HarmonyPatch(typeof(InputIcon), nameof(InputIcon.SetupSprite))]
    internal static class InputIconPatch
    {
        private static void Postfix(InputIcon __instance)
        {
            try { ButtonPrompts.Fix(__instance.imageObj); }
            catch (Exception) { /* prompts are cosmetic */ }
        }
    }
}
