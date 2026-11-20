using System;
using System.Collections.Generic;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using HarmonyLib;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// On foot, the pickup you look at (eyes on PS VR2 with the toolkit, else head) gets a pixel halo behind it,
    /// and becomes the game's interaction target instead of the nearest one.
    /// </summary>
    internal static class LookHighlight
    {
        private const float MaxDistance = 12f;
        private const float EyeCone = 4f, HeadCone = 6f;   // degrees beyond the thing's outline
        private const float Dwell = 0.12f, Linger = 0.3f;

        private sealed class Thing
        {
            public GameObject Go;
            public bool Glows;          // items only, people and the HOVA are just targeted
            public Bounds Outline;
            public float OutlineAt = -10f;
        }

        private static readonly Dictionary<IntPtr, Thing> things = new Dictionary<IntPtr, Thing>();
        private static readonly List<Thing> pool = new List<Thing>();
        private static float nextPool;
        private static Thing looked, candidate;
        private static float candidateSince, lostSince = -1f;
        private static GameObject halo;
        private static Material haloMat;
        private static float haloAlpha;
        private static bool usedEyes;

        // dev "look nearest": gaze snaps to the nearest glowing thing
        public static bool SimNearest;
        private static Thing demo;

#if DEV
        /// <summary>Dev "look demo": spawns an item-sized box in front of Rania.</summary>
        public static void Demo()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "CloudpunkVR_LookDemoItem";
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            var mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.55f, 0.42f, 0.30f, 1f) };
            var dr = go.GetComponent<MeshRenderer>();
            dr.sharedMaterial = mat;
            dr.allowOcclusionWhenDynamic = false;
            Vector3 fwd = CameraDriver.HeadWorldRotation * Vector3.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 left = Vector3.Cross(fwd, Vector3.up);
            go.transform.position = CameraDriver.HeadWorldPosition + fwd * 1.3f + Vector3.down * 0.15f;
            Plugin.L.LogInfo($"look demo: head at {CameraDriver.HeadWorldPosition}, view camera at {(CameraDriver.ViewCamera != null ? CameraDriver.ViewCamera.transform.position.ToString() : "none")}, near clip {(CameraDriver.ViewCamera != null ? CameraDriver.ViewCamera.nearClipPlane : -1f)}.");
            go.transform.localScale = new Vector3(0.3f, 0.24f, 0.22f);
            demo = new Thing { Go = go, Glows = true };
            Plugin.L.LogInfo($"look demo: a box at {go.transform.position}.");
        }
#endif

        /// <summary>Read by LookPicksTarget below.</summary>
        public static GameObject Target => looked != null && looked.Go != null ? looked.Go : null;
        public static bool Active => VRSession.Active && VRConfig.LookHighlight.Value && CameraDriver.Mode == ViewMode.OnFoot && !VRUI.Interactive;

        /// <summary>Before the cameras render, after the head is placed.</summary>
        public static void Update()
        {
            try
            {
                if (!Active) { looked = candidate = null; Fade(false); return; }
                if (Time.unscaledTime >= nextPool) { nextPool = Time.unscaledTime + 1f; RefreshPool(); }
                Choose();
                Fade(looked != null && looked.Glows && looked.Go != null && looked.Go.activeInHierarchy);
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Look highlight: " + e.Message);
                looked = candidate = null;
                Fade(false);
            }
        }

        private static void RefreshPool()
        {
            pool.Clear();
            Vector3 head = CameraDriver.HeadWorldPosition;
            void Add(GameObject go, bool glows)
            {
                if (go == null || !go.activeInHierarchy) return;
                if ((go.transform.position - head).sqrMagnitude > (MaxDistance + 3f) * (MaxDistance + 3f)) return;
                if (!things.TryGetValue(go.Pointer, out var t)) { t = new Thing { Go = go }; things[go.Pointer] = t; }
                t.Glows |= glows;
                if (!pool.Contains(t)) pool.Add(t);
            }
            if (demo != null && demo.Go != null) pool.Add(demo);
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PickupItem>()) if (p != null) Add(p.gameObject, true);
            foreach (var g in UnityEngine.Object.FindObjectsOfType<GenericInteractableItem>()) if (g != null && g.isActiveAndEnabled) Add(g.gameObject, true);
            // the game's current candidates (people, parked HOVA): targeted, not lit
            var candidates = Player.Instance?.interaction?.candidates;
            if (candidates != null)
                for (int i = 0; i < candidates.Count; i++) Add(candidates[i], false);
            if (things.Count > 400) things.Clear();
        }

        private static void Choose()
        {
            Vector3 origin = CameraDriver.HeadWorldPosition;
            Vector3 dir;
            usedEyes = Psvr2Toolkit.TryGetGaze(0.15f, out var gaze);
            dir = CameraDriver.HeadWorldRotation * (usedEyes ? gaze : Vector3.forward);
            if (SimNearest)
            {
                float nearest = float.MaxValue;
                foreach (var t in pool)
                {
                    if (!t.Glows || t.Go == null || !t.Go.activeInHierarchy) continue;
                    if (Time.unscaledTime - t.OutlineAt > 1f) { t.Outline = Outline(t.Go); t.OutlineAt = Time.unscaledTime; }
                    float d = (t.Outline.center - origin).magnitude;
                    if (d < nearest && d < MaxDistance) { nearest = d; dir = (t.Outline.center - origin) / d; }
                }
            }
            float cone = usedEyes ? EyeCone : HeadCone;

            Thing best = null;
            float bestScore = float.MaxValue;
            foreach (var t in pool)
            {
                if (t.Go == null || !t.Go.activeInHierarchy) continue;
                if (Time.unscaledTime - t.OutlineAt > 1f) { t.Outline = Outline(t.Go); t.OutlineAt = Time.unscaledTime; }
                Vector3 to = t.Outline.center - origin;
                float d = to.magnitude;
                if (d > MaxDistance || d < 0.05f) continue;
                float r = Mathf.Min(t.Outline.extents.magnitude, 1.5f);
                float score = Vector3.Angle(dir, to) - Mathf.Rad2Deg * Mathf.Atan2(r, d);
                if (score > cone || score >= bestScore) continue;
                if (Hidden(origin, t, d, r)) continue;
                best = t;
                bestScore = score;
            }

            float now = Time.unscaledTime;
            if (best != candidate) { candidate = best; candidateSince = now; }
            if (best != null)
            {
                lostSince = -1f;
                if (best != looked && now - candidateSince >= Dwell) looked = best;
            }
            else if (looked != null)
            {
                if (lostSince < 0f) lostSince = now;
                if (now - lostSince > Linger) looked = null;
            }
        }

        /// <summary>Occlusion check. Rania's own capsule is skipped, the eyes sit at its top.</summary>
        private static bool Hidden(Vector3 origin, Thing t, float d, float r)
        {
            Vector3 dir = (t.Outline.center - origin) / d;
            var hits = Physics.RaycastAll(origin, dir, d, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var player = Player.Instance != null ? Player.Instance.transform : null;
            var go = t.Go.transform;
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.distance >= d - r) continue;
                var h = hit.transform;
                if (h == null || h == go || h.IsChildOf(go) || go.IsChildOf(h)) continue;
                if (player != null && (h == player || h.IsChildOf(player))) continue;
                return true;
            }
            return false;
        }

        /// <summary>Renderer bounds, else colliders, else a small box.</summary>
        private static Bounds Outline(GameObject go)
        {
            bool any = false;
            var b = new Bounds(go.transform.position, Vector3.zero);
            var renderers = go.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var rd = renderers[i];
                if (rd == null || !rd.enabled || rd.TryCast<ParticleSystemRenderer>() != null) continue;
                if (!any) { b = rd.bounds; any = true; } else b.Encapsulate(rd.bounds);
            }
            if (!any)
            {
                var colliders = go.GetComponentsInChildren<Collider>();
                for (int i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            }
            if (!any) b = new Bounds(go.transform.position, Vector3.one * 0.3f);
            return b;
        }

        // ---- The halo.

        public static bool HaloShown => halo != null && halo.activeSelf;

        private static void Fade(bool on)
        {
            haloAlpha = Mathf.MoveTowards(haloAlpha, on ? 1f : 0f, Time.unscaledDeltaTime * (on ? 7f : 4f));
            if (Film.CleanFrame.HidesOverlays) haloAlpha = 0f; // clean frame: no glow, targeting still works
            if (haloAlpha <= 0.001f) { if (halo != null && halo.activeSelf) halo.SetActive(false); return; }
            if (halo == null && !MakeHalo()) return;
            if (!halo.activeSelf) halo.SetActive(true);
            if (looked == null || looked.Go == null) return; // fading out where it was
            var b = looked.Outline;
            Vector3 head = CameraDriver.HeadWorldPosition;
            Vector3 away = (b.center - head).normalized;
            float r = Mathf.Clamp(Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)), 0.08f, 1.2f);
            // just behind the thing, facing the eyes, scaled up so perspective doesn't shrink the rim
            float back = r * 0.5f;
            float d = Mathf.Max(Vector3.Distance(head, b.center), 0.1f);
            float size = 2f * (r + Mathf.Max(0.06f, 0.35f * r)) * (d + back) / d;
            halo.transform.SetPositionAndRotation(b.center + away * back, Quaternion.LookRotation(away, Vector3.up));
            halo.transform.localScale = Vector3.one * size;
            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 4.2f);
            haloMat.color = new Color(0.78f, 0.9f, 1f, 0.55f * haloAlpha * pulse);
        }

        private static bool MakeHalo()
        {
            // Sprites/Default is always in builds and depth-tests, so the thing covers the halo's middle
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader == null) return false;
            const int n = 24;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "CloudpunkVR_LookHalo", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - Mathf.InverseLerp(0.45f, 1f, r));
                    a = Mathf.Round(a * 4f) / 4f; // four steps, chunky like the game
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply(false);
            halo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            halo.name = "CloudpunkVR_LookHalo";
            UnityEngine.Object.Destroy(halo.GetComponent<Collider>());
            UnityEngine.Object.DontDestroyOnLoad(halo);
            halo.layer = 0; // drawn by the game's cameras
            haloMat = new Material(shader) { name = "CloudpunkVR_LookHalo", mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var mr = halo.GetComponent<MeshRenderer>();
            mr.sharedMaterial = haloMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.allowOcclusionWhenDynamic = false; // baked occlusion would cull it
            Plugin.L.LogInfo($"Look highlight ready ({shader.name}).");
            return true;
        }

        public static string Describe()
        {
            string what = looked == null || looked.Go == null ? "nothing" : $"'{looked.Go.name}' ({(looked.Glows ? "glows" : "targeted only")}, outline {looked.Outline.center} size {looked.Outline.size})";
            return $"look highlight {(Active ? "on" : "off")}, {(usedEyes ? "eyes" : "head")}, {pool.Count} things around, looking at {what}, halo {haloAlpha:0.00}";
        }
    }

    /// <summary>The game picks the nearest thing in reach. The one you look at wins instead.</summary>
    [HarmonyPatch(typeof(Interaction), nameof(Interaction.FindClosestObject))]
    internal static class LookPicksTarget
    {
        private static void Postfix(Interaction __instance)
        {
            if (!LookHighlight.Active) return;
            var target = LookHighlight.Target;
            if (target == null) return;
            var candidates = __instance.candidates;
            if (candidates == null) return;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null) continue;
                if (c.Pointer == target.Pointer || c.transform.IsChildOf(target.transform) || target.transform.IsChildOf(c.transform))
                {
                    if (__instance.closestObject == null || __instance.closestObject.Pointer != c.Pointer) __instance.closestObject = c;
                    return;
                }
            }
        }
    }
}
