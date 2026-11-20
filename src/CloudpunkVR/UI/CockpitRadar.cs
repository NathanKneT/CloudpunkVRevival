using System;
using CloudpunkVR.VR;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The minimap camera's picture as a round HUD piece, since the dashboard screen is hard to glance at in flight.
    /// Uses the game's own arrow. The game's markers stay on the dashboard only.
    /// </summary>
    internal static class CockpitRadar
    {
        private const int Segments = 48;
        private static Transform root, back, map, arrow;
        private static Material mapMaterial, arrowMaterial;
        private static Mesh disc;
        private static readonly Vector2[] uvs = new Vector2[Segments + 1];
        private static RawImage image;      // game's image showing the minimap camera
        private static float nextSearch, arrowSize;
        private static Vector2 lastC = new Vector2(float.NaN, 0f), lastUx, lastUy;
        private static IntPtr arrowOf;
        private static RectTransform arrowRt;
        private static Image arrowImage;
        private static bool warned;

        public static void Create(Transform parent, int layer, int queue)
        {
            if (root != null) return;
            root = new GameObject("CloudpunkVR_CockpitRadar") { layer = layer }.transform;
            root.SetParent(parent, false);
            disc = MakeDisc();
            back = MakePart("Back", layer, disc, queue, out var backMaterial);
            // dark rim behind the map
            backMaterial.color = new Color(11f / 255f, 18f / 255f, 32f / 255f, 0.8f);
            back.localScale = new Vector3(1.06f, 1.06f, 1f);
            back.localPosition = new Vector3(0f, 0f, 0.002f);
            map = MakePart("Map", layer, disc, queue + 1, out mapMaterial);
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            arrow = MakePart("Arrow", layer, quad.GetComponent<MeshFilter>().sharedMesh, queue + 2, out arrowMaterial);
            UnityEngine.Object.Destroy(quad);
            arrow.localPosition = new Vector3(0f, 0f, -0.002f);
            root.gameObject.SetActive(false);
        }

        private static Transform MakePart(string name, int layer, Mesh mesh, int queue, out Material material)
        {
            var go = new GameObject("CloudpunkVR_CockpitRadar" + name) { layer = layer };
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            material = new Material(Shader.Find("UI/Default")) { name = go.name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            material.renderQueue = queue;
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Unit-diameter triangle fan. UVs are updated in Follow.</summary>
        private static Mesh MakeDisc()
        {
            var verts = new Vector3[Segments + 1];
            var tris = new int[Segments * 3];
            for (int i = 0; i < Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                verts[i + 1] = new Vector3(Mathf.Cos(a) * 0.5f, Mathf.Sin(a) * 0.5f, 0f);
                tris[i * 3] = 0;
                tris[i * 3 + 1] = (i + 1) % Segments + 1;
                tris[i * 3 + 2] = i + 1;
            }
            var m = new Mesh { name = "CloudpunkVR_RadarDisc", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        public static bool Wanted()
        {
            if (root == null || !VRConfig.CockpitRadar.Value || CameraDriver.Mode != ViewMode.Cockpit) return false;
            var g = Global.Instance;
            var mm = Minimap.Instance;
            if (g == null || mm == null || g.optionDisableHUD) return false;
            var small = mm.mapSmall;
            if (small == null || !small.isActiveAndEnabled || small.alpha < 0.05f) return false;
            if (image == null || !image.isActiveAndEnabled) Find(mm);
            return image != null && image.m_Texture != null;
        }

        private static void Find(Minimap mm)
        {
            image = null;
            if (Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 2f;
            var cam = mm.miniMapCamera;
            var picture = cam != null ? cam.targetTexture : null;
            if (picture == null) return;
            var images = mm.mapSmall.GetComponentsInChildren<RawImage>(true);
            for (int i = 0; i < images.Length; i++)
                if (images[i] != null && images[i].m_Texture != null && images[i].m_Texture.Pointer == picture.Pointer) { image = images[i]; break; }
            if (image == null && !warned) { warned = true; Plugin.L.LogWarning("Cockpit radar: the image showing the minimap camera's picture was not found."); }
        }

        /// <summary>dir is a direction from the eyes, degrees is the radar's angular size.</summary>
        public static void Place(bool show, Vector3 eye, Quaternion dir, float distance, float degrees)
        {
            if (root == null) return;
            if (show != root.gameObject.activeSelf) root.gameObject.SetActive(show);
            if (!show) return;
            try
            {
                Follow();
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Cockpit radar: " + e.Message);
                image = null;
                root.gameObject.SetActive(false);
                return;
            }
            float size = UiPicture.Span(degrees, distance);
            root.SetPositionAndRotation(eye + dir * Vector3.forward * distance, dir);
            root.localScale = new Vector3(size, size, size);
            arrow.localScale = new Vector3(arrowSize, arrowSize, 1f);
        }

        /// <summary>Maps the game's round mask through the image's rect and UV rect, so the disc shows the same area.</summary>
        private static void Follow()
        {
            var mm = Minimap.Instance;
            mapMaterial.mainTexture = image.m_Texture;
            var raw = image.rectTransform;
            var mask = mm.minimapMaskTransform != null ? mm.minimapMaskTransform : raw;
            Rect m = mask.rect, r = raw.rect;
            Rect uvRect = image.m_UVRect;
            Vector2 Uv(Vector2 local)
            {
                Vector3 inRaw = raw.InverseTransformPoint(mask.TransformPoint(local));
                return new Vector2(uvRect.x + (inRaw.x - r.xMin) / r.width * uvRect.width, uvRect.y + (inRaw.y - r.yMin) / r.height * uvRect.height);
            }
            Vector2 c = Uv(m.center), ux = Uv(m.center + new Vector2(m.width * 0.5f, 0f)) - c, uy = Uv(m.center + new Vector2(0f, m.height * 0.5f)) - c;
            // setting mesh.uv allocates, so only when the mapping changed
            if (c != lastC || ux != lastUx || uy != lastUy)
            {
                lastC = c; lastUx = ux; lastUy = uy;
                uvs[0] = c;
                for (int i = 0; i < Segments; i++)
                {
                    float a = i * Mathf.PI * 2f / Segments;
                    uvs[i + 1] = c + ux * Mathf.Cos(a) + uy * Mathf.Sin(a);
                }
                disc.uv = uvs;
            }

            // cached per minimap
            if (arrowOf != mm.Pointer || arrowRt == null || arrowImage == null)
            {
                arrowOf = mm.Pointer;
                arrowRt = mm.arrow != null ? mm.arrow.TryCast<RectTransform>() : null;
                arrowImage = arrowRt != null ? arrowRt.GetComponent<Image>() : null;
            }
            var sprite = arrowImage != null ? arrowImage.m_Sprite : null;
            if (sprite == null || sprite.texture == null) { arrow.gameObject.SetActive(false); return; }
            if (!arrow.gameObject.activeSelf) arrow.gameObject.SetActive(true);
            var tex = sprite.texture;
            Rect tr = sprite.textureRect;
            arrowMaterial.mainTexture = tex;
            arrowMaterial.mainTextureScale = new Vector2(tr.width / tex.width, tr.height / tex.height);
            arrowMaterial.mainTextureOffset = new Vector2(tr.x / tex.width, tr.y / tex.height);
            // clamped, the sprite rect may carry padding
            arrowSize = Mathf.Clamp(arrowRt.rect.width * arrowRt.lossyScale.x / Mathf.Max(1e-5f, m.width * mask.lossyScale.x), 0.05f, 0.35f);
            float turn = (Quaternion.Inverse(mask.rotation) * arrowRt.rotation).eulerAngles.z;
            arrow.localRotation = Quaternion.Euler(0f, 0f, turn);
        }

        public static string Describe() =>
            root == null ? "not created" : $"radar {(root.gameObject.activeSelf ? "shown" : "hidden")}, picture {(image != null ? image.name : "not found")}";
    }
}
