#if DEV
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR
{
    /// <summary>Dev command "art": dumps the game's fonts, UI colors, sprites and textures to the log and BepInEx\art\.</summary>
    internal static class ArtProbe
    {
        private static string dir;

        public static void Run()
        {
            dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "art");
            Directory.CreateDirectory(dir);
            var log = new System.Text.StringBuilder("art probe:\n");
            Try(log, "rania", () => Rania(log));
            Try(log, "fonts", () => Fonts(log));
            Try(log, "colors", () => Colors(log));
            Try(log, "sprites", () => Sprites(log));
            Try(log, "shaders", () => Shaders(log));
            Plugin.L.LogInfo(log.ToString());
        }

        private static void Try(System.Text.StringBuilder log, string what, Action a)
        {
            try { a(); } catch (Exception e) { log.AppendLine($"  {what}: failed {e.GetType().Name}: {e.Message}"); }
        }

        private static void Rania(System.Text.StringBuilder log)
        {
            var g = Global.Instance;
            var def = g?.raniaCharacter;
            if (def?.portrait != null) { SaveSprite(def.portrait, "rania-portrait"); log.AppendLine($"  rania portrait {def.portrait.name} {def.portrait.rect}"); }
            var p = g?.player;
            if (p == null) { log.AppendLine("  no player"); return; }
            foreach (var r in p.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                var tex = m != null ? m.mainTexture : null;
                log.AppendLine($"  player renderer {r.name} ({r.GetIl2CppType().Name}) enabled={r.enabled} self={r.gameObject.activeSelf} active={r.gameObject.activeInHierarchy} layer={r.gameObject.layer} " +
                    $"mat={m?.name} shader={m?.shader?.name} tex={tex?.name} {tex?.width}x{tex?.height} color={(m != null && m.HasProperty("_Color") ? m.color.ToString() : "-")}");
                if (tex != null) SaveTexture(tex, "player-" + Safe(r.name) + (r.gameObject.activeSelf ? "-worn" : ""));
            }
            int n = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Character>())
            {
                if (c == null || n++ >= 3) break;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true).Take(2))
                {
                    var m = r.sharedMaterial;
                    log.AppendLine($"  npc {c.name} renderer {r.name} ({r.GetIl2CppType().Name}) mat={m?.name} shader={m?.shader?.name} tex={m?.mainTexture?.name}");
                    if (n == 1 && m?.mainTexture != null) SaveTexture(m.mainTexture, "npc-" + Safe(m.mainTexture.name));
                }
            }
        }

        private static void Fonts(System.Text.StringBuilder log)
        {
            var used = new Dictionary<string, int>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
            {
                if (t == null || t.font == null) continue;
                string k = $"{t.font.name}{(t.isActiveAndEnabled ? "" : " (hidden)")}";
                used[k] = used.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            log.AppendLine("  TMP fonts in scene: " + string.Join(", ", used.OrderByDescending(x => x.Value).Select(x => $"{x.Key} x{x.Value}")));
            var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Select(f => f.name).Distinct();
            log.AppendLine("  TMP font assets loaded: " + string.Join(", ", all));
            var legacy = Resources.FindObjectsOfTypeAll<Font>().Select(f => f.name).Distinct();
            log.AppendLine("  Unity fonts loaded: " + string.Join(", ", legacy));
            // sample texts, to copy sizes and spacing
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>().Where(t => t != null && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text)).Take(25))
                log.AppendLine($"    text \"{Short(t.text)}\" font={t.font?.name} size={t.fontSize} color={Hex(t.color)} spacing={t.characterSpacing} style={t.fontStyle} path={PathOf(t.transform)}");
        }

        private static void Colors(System.Text.StringBuilder log)
        {
            var images = new Dictionary<string, int>();
            foreach (var im in UnityEngine.Object.FindObjectsOfType<Image>())
            {
                if (im == null || !im.isActiveAndEnabled) continue;
                string k = Hex(im.color) + (im.sprite != null ? " " + im.sprite.name : "");
                images[k] = images.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            log.AppendLine("  active Image colors/sprites: " + string.Join(", ", images.OrderByDescending(x => x.Value).Take(30).Select(x => $"{x.Key} x{x.Value}")));
            var sel = new Dictionary<string, int>();
            foreach (var s in UnityEngine.Object.FindObjectsOfType<Selectable>())
            {
                if (s == null) continue;
                var cb = s.colors;
                string k = $"normal {Hex(cb.normalColor)} highlight {Hex(cb.highlightedColor)} pressed {Hex(cb.pressedColor)} transition {s.transition}";
                sel[k] = sel.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            log.AppendLine("  selectable colors: " + string.Join(" | ", sel.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} x{x.Value}")));
        }

        private static void Sprites(System.Text.StringBuilder log)
        {
            var keys = new[] { "reticle", "crosshair", "cursor", "dot", "circle", "frame", "border", "panel", "select", "glow", "button", "bracket", "corner", "line", "notif", "popup", "box" };
            var found = Resources.FindObjectsOfTypeAll<Sprite>()
                .Where(s => s != null && keys.Any(k => s.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                .GroupBy(s => s.name).Select(gr => gr.First()).Take(80).ToList();
            log.AppendLine("  sprites: " + string.Join(", ", found.Select(s => $"{s.name} {s.rect.width}x{s.rect.height} border={s.border}")));
            foreach (var s in found.Take(40)) SaveSprite(s, "sprite-" + Safe(s.name));
        }

        private static void Shaders(System.Text.StringBuilder log)
        {
            var names = new[] { "Hidden/Internal-Colored", "Standard", "Unlit/Color", "Unlit/Texture", "Legacy Shaders/Diffuse", "Legacy Shaders/VertexLit", "Mobile/Diffuse", "Particles/Standard Unlit", "Sprites/Default", "UI/Default", "TextMeshPro/Distance Field", "TextMeshPro/Mobile/Distance Field" };
            log.AppendLine("  shaders: " + string.Join(", ", names.Select(n => $"{n}={(Shader.Find(n) != null ? "yes" : "no")}")));
        }

        private static void SaveSprite(Sprite s, string name)
        {
            if (s == null || s.texture == null) return;
            var r = s.textureRect;
            Save(s.texture, name, ((int)r.x, (int)r.y, Math.Max(1, (int)r.width), Math.Max(1, (int)r.height)));
        }

        private static void SaveTexture(Texture t, string name) => Save(t, name, (0, 0, t.width, t.height));

        private static void Save(Texture t, string name, (int x, int y, int width, int height) rect)
        {
            string path = Path.Combine(dir, name + ".png");
            if (File.Exists(path)) return;
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(t, rt);
            RenderTexture.active = rt;
            var tex = new Texture2D(rect.width, rect.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(rect.x, rect.y, rect.width, rect.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex);
        }

        private static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);
        private static string Short(string s) => (s.Length > 40 ? s.Substring(0, 40) : s).Replace("\n", " ");
        private static string Safe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_'));

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
#endif
