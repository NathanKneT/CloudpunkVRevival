using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Film
{
    /// <summary>Free camera status line, bottom left, in the game's UI style. Fades after a few seconds, hidden in takes.</summary>
    internal static class FreeCamNote
    {
        private const float Shown = 4f;
        private static readonly Color Navy = new Color32(11, 18, 32, 215);
        private static readonly Color Amber = new Color32(245, 166, 35, 255);

        private static GameObject root;
        private static CanvasGroup group;
        private static TextMeshProUGUI status, message;
        private static float shownAt = -100f;
        private static float retryAt;
        private static bool warned;

        /// <summary>On screen now (for the clean frame check).</summary>
        public static bool Visible => group != null && group.alpha > 0.01f;

        public static void Show(string statusLine, string messageLine)
        {
            if (!Build()) return;
            status.text = statusLine;
            message.text = messageLine ?? "";
            shownAt = Time.unscaledTime;
        }

        /// <summary>Fades out after a while, hidden at once in a take or a clean frame.</summary>
        public static void Update(bool hide)
        {
            if (group == null) return;
            float t = Time.unscaledTime - shownAt;
            float alpha = hide || CleanFrame.HidesOverlays ? 0f : Mathf.Clamp01((Shown + 1f - t) / 1f);
            if (group.alpha != alpha) group.alpha = alpha;
        }

        private static bool Build()
        {
            if (group != null) return true;
            if (Time.unscaledTime < retryAt) return false;
            TMP_FontAsset titleFont = null, bodyFont = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f == null) continue;
                if (f.name == "RistrettoPro-Regular") titleFont = f;
                else if (f.name == "Aquawax-Pro-DemiBold") bodyFont = f;
            }
            if (titleFont == null || bodyFont == null)
            {
                // fonts load with the game's scenes: retry later
                retryAt = Time.unscaledTime + 10f;
                if (!warned) { warned = true; Plugin.L.LogWarning("Free camera: the game's fonts are not loaded yet, so no line on the screen for now (the log has everything)."); }
                return false;
            }

            root = new GameObject("CloudpunkVR_FreeCamNote");
            Object.DontDestroyOnLoad(root);
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

            var panel = Rect("Panel", root.transform, new Vector2(40f, 40f), new Vector2(1240f, 112f));
            panel.gameObject.AddComponent<Image>().color = Navy;
            var edge = Rect("Edge", panel, Vector2.zero, new Vector2(6f, 112f));
            edge.gameObject.AddComponent<Image>().color = Amber;

            status = Text("Status", panel, new Vector2(24f, 74f), new Vector2(1200f, 32f));
            status.font = titleFont;
            status.fontSize = 26f;
            status.characterSpacing = 3f;
            status.color = Amber;

            message = Text("Message", panel, new Vector2(24f, 10f), new Vector2(1200f, 62f)); // up to three lines
            message.font = bodyFont;
            message.fontSize = 17f;
            message.color = new Color(0.86f, 0.9f, 0.95f, 1f);
            message.enableWordWrapping = true;
            return true;
        }

        // anchored bottom left, pos is the bottom-left corner
        private static RectTransform Rect(string name, Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private static TextMeshProUGUI Text(string name, Transform parent, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            text.alignment = TextAlignmentOptions.BottomLeft;
            text.enableWordWrapping = false;
            return text;
        }
    }
}
