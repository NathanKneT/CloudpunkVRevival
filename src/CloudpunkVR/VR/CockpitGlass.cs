using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// HOVA windows. The Windshield Shader's GrabPass blur (a fixed share of the screen wide) smears differently in
    /// each eye, so in VR it's zeroed unless CockpitWindowBlur. Property blocks, since the game copies the material for cracks.
    /// </summary>
    internal static class CockpitGlass
    {
        private const string ShaderName = "Roman's Shaders/Windshield Shader";
        private static readonly int BlurId = Shader.PropertyToID("_BlurIntensity");
        private static readonly int GrainId = Shader.PropertyToID("_NoiseIntensity");
        private static readonly List<Renderer> windows = new List<Renderer>();
        private static readonly HashSet<IntPtr> overridden = new HashSet<IntPtr>();
        private static MaterialPropertyBlock block;
        private static Transform root;
        private static float nextCheck;
        private static int seenScenes = -1;
        private static bool warned, applied;
        private static string lastLog;

        public static bool Clear => VRSession.Active && !VRConfig.CockpitWindowBlur.Value;

        public static List<Renderer> WindowRenderers()
        {
            var list = new List<Renderer>();
            var found = Root();
            if (found == null) return list;
            var renderers = found.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && IsWindow(renderers[i])) list.Add(renderers[i]);
            return list;
        }

        /// <summary>Drawn with the window shader (glass or drops).</summary>
        public static bool IsWindow(Renderer r)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].shader != null && mats[i].shader.name == ShaderName) return true;
            return false;
        }

        /// <summary>Every 2 s and on scene change. Windows are collected once per HOVA.</summary>
        public static void Update()
        {
            bool scenes = SceneWatch.Changed(ref seenScenes);
            if (!scenes && Time.unscaledTime < nextCheck) return;
            Pass(scenes);
        }

        public static void Refresh() => Pass(true);

        private static void Pass(bool rescan)
        {
            nextCheck = Time.unscaledTime + 2f;
            try
            {
                if (!Clear) { Restore(); return; }
                var found = Root();
                if (found == null) return;
                if (found != root || rescan) Collect(found);
                int n = 0;
                for (int i = 0; i < windows.Count; i++) if (Override(windows[i])) n++;
                if (n > 0) Log(n);
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; Plugin.L.LogWarning("HOVA windows not checked: " + e.Message); }
            }
        }

        /// <summary>Parent of the windscreen's "_Windshields" group.</summary>
        private static Transform Root()
        {
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            var front = vi != null ? vi.windshieldFrontMesh : null;
            if (front == null) return null;
            var t = front.transform.parent;
            return t != null && t.parent != null ? t.parent : t;
        }

        /// <summary>Window renderers with blur or grain above zero.</summary>
        private static void Collect(Transform found)
        {
            root = found;
            windows.Clear();
            var renderers = found.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var m = renderers[i] != null ? renderers[i].sharedMaterial : null;
                if (m == null || m.shader == null || m.shader.name != ShaderName || !m.HasProperty(BlurId)) continue;
                if (m.GetFloat(BlurId) == 0f && (!m.HasProperty(GrainId) || m.GetFloat(GrainId) == 0f)) continue;
                windows.Add(renderers[i]);
            }
        }

        /// <summary>Zeroes blur and grain, keeps the rest of the block. Rewritten each pass in case it got replaced.</summary>
        private static bool Override(Renderer r)
        {
            if (r == null) return false;
            if (block == null) block = new MaterialPropertyBlock();
            block.Clear();
            r.GetPropertyBlock(block);
            block.SetFloat(BlurId, 0f);
            block.SetFloat(GrainId, 0f);
            r.SetPropertyBlock(block);
            applied = true;
            return overridden.Add(r.Pointer);
        }

        /// <summary>Puts the material's own values back (the game never changes them).</summary>
        private static void Restore()
        {
            if (!applied) return;
            int n = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                var r = windows[i];
                try
                {
                    if (r == null || !overridden.Contains(r.Pointer)) continue;
                    var m = r.sharedMaterial;
                    if (m == null) continue;
                    if (block == null) block = new MaterialPropertyBlock();
                    block.Clear();
                    r.GetPropertyBlock(block);
                    block.SetFloat(BlurId, m.GetFloat(BlurId));
                    block.SetFloat(GrainId, m.HasProperty(GrainId) ? m.GetFloat(GrainId) : 0f);
                    r.SetPropertyBlock(block);
                    n++;
                }
                catch (Exception) { }
            }
            overridden.Clear();
            applied = false;
            lastLog = null;
            Plugin.L.LogInfo($"HOVA windows: the game's blur is back on {n} window(s) (CockpitWindowBlur = true, or VR off).");
        }

        private static void Log(int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < windows.Count; i++)
            {
                var r = windows[i];
                var m = r != null ? r.sharedMaterial : null;
                if (m != null) sb.Append($" '{r.name}' ({m.name}: blur {m.GetFloat(BlurId):0.###}, grain {(m.HasProperty(GrainId) ? m.GetFloat(GrainId) : 0f):0.###})");
            }
            string line = $"HOVA windows: the blur behind the glass is off in VR on {overridden.Count} window(s), drawn at zero, the same clear glass in both eyes (it smeared lights in one eye only):{sb}; drops, wipers and cracks stay.";
            if (line == lastLog) return;
            lastLog = line;
            Plugin.L.LogInfo(line);
        }

        /// <summary>Dev command: each window's blur as drawn vs the material's.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder($"windows {(Clear ? "clear (blur off in VR)" : "as in the game (blurred)")}:");
            if (windows.Count == 0) sb.Append(" none found yet");
            if (block == null) block = new MaterialPropertyBlock();
            for (int i = 0; i < windows.Count; i++)
            {
                var r = windows[i];
                var m = r != null ? r.sharedMaterial : null;
                if (m == null) continue;
                float blur = m.GetFloat(BlurId), grain = m.HasProperty(GrainId) ? m.GetFloat(GrainId) : 0f;
                block.Clear();
                r.GetPropertyBlock(block);
                bool over = overridden.Contains(r.Pointer);
                sb.Append($" '{r.name}' drawn with blur {(over ? block.GetFloat(BlurId) : blur):0.###} grain {(over ? block.GetFloat(GrainId) : grain):0.###} (game {blur:0.###}/{grain:0.###});");
            }
            return sb.ToString();
        }
    }
}
