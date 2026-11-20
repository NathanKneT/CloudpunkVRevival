using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR
{
    /// <summary>Monitor card after an auto flat start (SteamVR wasn't running), styled like the game's UI. Shown 20 s.</summary>
    public class FlatNotice : MonoBehaviour
    {
        public FlatNotice(IntPtr ptr) : base(ptr) { }

        private const float Shown = 20f;
        private const string Title = "VR OFF FOR THIS LAUNCH";
        private const string Body = "SteamVR was not running, so Cloudpunk started on the monitor.\n" +
                                    "Start SteamVR first, then the game, to play in VR. (StartInVR = Always in CloudpunkVR.cfg starts VR every time.)";
        private static readonly Color Navy = new Color32(11, 18, 32, 230);
        private static readonly Color Amber = new Color32(245, 166, 35, 255);

        private float start = -1f;
        private CanvasGroup group;
        private bool fallback;

        private void Update()
        {
            if (start < 0f) start = Time.unscaledTime;
            float t = Time.unscaledTime - start;
            if (t > Shown) { if (group != null) Destroy(group.gameObject); enabled = false; return; }
            if (group == null && !fallback)
            {
                // fonts load with the first scene, give them 8 s
                if (!TryBuild() && t > 8f) fallback = true;
            }
            if (group != null) group.alpha = Mathf.Clamp01(t * 2f) * Mathf.Clamp01((Shown - t) / 2f);
        }

        private bool TryBuild()
        {
            TMP_FontAsset titleFont = null, bodyFont = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f == null) continue;
                if (f.name == "RistrettoPro-Regular") titleFont = f;
                else if (f.name == "Aquawax-Pro-DemiBold") bodyFont = f;
            }
            if (titleFont == null || bodyFont == null) return false;

            var root = new GameObject("CloudpunkVR_FlatNotice");
            DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            group = root.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            // top left, like the game's location sign
            var panel = Rect("Panel", root.transform, new Vector2(0f, 1f), new Vector2(60f, -60f), new Vector2(760f, 132f));
            panel.gameObject.AddComponent<Image>().color = Navy;
            var edge = Rect("Edge", panel, new Vector2(0f, 1f), Vector2.zero, new Vector2(6f, 132f));
            edge.gameObject.AddComponent<Image>().color = Amber;

            var title = Text("Title", panel, new Vector2(28f, -14f), new Vector2(712f, 44f));
            title.font = titleFont;
            title.text = Title;
            title.fontSize = 38f;
            title.characterSpacing = 6f;
            title.color = Amber;
            title.alignment = TextAlignmentOptions.TopLeft;
            title.enableWordWrapping = false;

            var body = Text("Body", panel, new Vector2(28f, -62f), new Vector2(712f, 64f));
            body.font = bodyFont;
            body.text = Body;
            body.fontSize = 19f;
            body.color = new Color(0.86f, 0.9f, 0.95f, 1f);
            body.alignment = TextAlignmentOptions.TopLeft;
            body.enableWordWrapping = true;
            return true;
        }

        /// <summary>Size is set after adding the text component, which resets it.</summary>
        private static TextMeshProUGUI Text(string name, Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return text;
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        // plain fallback if the fonts never showed up
        private void OnGUI()
        {
            if (!fallback) return;
            float t = Time.unscaledTime - start;
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((Shown - t) / 2f));
            float w = Mathf.Min(900f, Screen.width - 40f);
            var rect = new Rect((Screen.width - w) / 2f, 20f, w, 60f);
            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 12f, rect.y + 8f, rect.width - 24f, rect.height - 16f), Title + ": " + Body);
            GUI.color = old;
        }
    }
}
