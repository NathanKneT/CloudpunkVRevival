#if DEV
using System;
using System.Globalization;
using System.Text;
using CloudpunkVR.VR;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Dev command `vrsettings`: alone, the menu's state; `point entry|options|resume|&lt;setting&gt; [left|right]` clicks
    /// it with the laser; `select &lt;setting&gt;` like the d-pad; `set &lt;setting&gt; &lt;value&gt;` through the row's own apply and
    /// save; `check [label]` row vs memory vs file vs the system, ok or FAILED.
    /// </summary>
    internal static partial class VrevivalMenu
    {
        public static string Dev(string[] a)
        {
            if (settings == null) settings = Make();
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "point": return Point(a.Length > 2 ? a[2] : "", a.Length > 3 ? a[3] : "");
                case "select":
                {
                    var s = Named(a.Length > 2 ? a[2] : "");
                    var g = Global.Instance;
                    if (s == null || s.Row == null || g == null) return "select: no such row built (" + Keys() + ")";
                    g.SelectUIObject(s.Row.gameObject);
                    return $"select: {s.Name} selected; " + Describe();
                }
                case "set":
                {
                    var s = Named(a.Length > 2 ? a[2] : "");
                    if (s == null || a.Length < 4) return "set: vrsettings set <setting> <value or label> (" + Keys() + ")";
                    Offer(s);
                    string want = string.Join(" ", a, 3, a.Length - 3);
                    int at = -1;
                    for (int i = 0; i < s.Names.Count && at < 0; i++)
                        if (s.Names[i].Equals(want, StringComparison.OrdinalIgnoreCase)) at = i;
                    if (at < 0 && float.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) at = Find(s.Values, v);
                    if (at < 0) return $"set: {s.Name} has no '{want}' ({string.Join(", ", s.Names)})";
                    Apply(s, s.Values[at], s.Names[at], "developer command");
                    return "set: " + RowState(s);
                }
                case "check": return Check(a.Length > 2 ? string.Join(" ", a, 2, a.Length - 2) : "now");
            }
            return Describe();
        }

        private static Setting Named(string key)
        {
            for (int i = 0; i < settings.Length; i++)
                if (settings[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return settings[i];
            return null;
        }

        private static string Keys()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < settings.Length; i++) sb.Append(i > 0 ? ", " : "").Append(settings[i].Key);
            return sb.ToString();
        }

        public static string Describe()
        {
            var g = Global.Instance;
            var sb = new StringBuilder(built);
            if (entry != null) sb.Append($"; entry {Diagnostics.Path(entry.transform)} {(entry.activeInHierarchy ? "shown" : "hidden")} at {Pixel(entry.transform)}, {Neighbours()}");
            bool swapped = g != null && Is(g.options, groupPtr);
            sb.Append($"; window {(window == null ? "none" : window.activeInHierarchy ? "up" : "closed")}, {(open ? "open" : "not open")}, " +
                      $"the game's options window {(swapped ? "is this one (swapped while open)" : "its own")}");
            if (g != null && !swapped && g.options != null) sb.Append($" ({(g.options.gameObject.activeInHierarchy ? "open" : "closed")})");
            sb.Append($"; in a menu {(g != null && g.IsInMenu())}, menu input {(g != null && g.blockMenuInput ? "blocked" : "free")}");
            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            sb.Append($"; selected {(sel != null ? "'" + sel.name + "'" : "nothing")}");
            if (note != null && window != null && window.activeInHierarchy) sb.Append($", note '{note.text}' {NotePlace(out _)}");
            var first = settings[0].Row != null ? settings[0].Row.GetComponent<UnityEngine.UI.Selectable>() : null;
            if (first != null)
                sb.Append($"; a row's Selectable: {first.GetIl2CppType().Name}, enabled {first.enabled}, interactable {first.interactable}, " +
                          $"allowed by its groups {first.IsInteractable()}, transition {first.transition}");
            sb.Append("; rows:");
            for (int i = 0; i < settings.Length; i++) sb.Append(' ').Append(RowState(settings[i])).Append(';');
            sb.Append(" view ").Append(VRUI.DescribeView());
            return sb.ToString();
        }

        private static string RowState(Setting s)
        {
            if (s.Row == null) return $"{s.Key} (no row) = {Entries(s)}";
            var value = s.Row.value;
            return $"{s.Key} '{(value != null ? value.text : "?")}' [{s.Row.currentValue + 1}/{s.Values.Count}] = {Entries(s)} at {Pixel(s.Row.transform)}";
        }

        /// <summary>A piece's centre on the UI picture (pixels, bottom left origin), where the laser finds it.</summary>
        private static Vector2 Centre(Transform t)
        {
            var cam = CameraDriver.UiCamera;
            var rt = t != null ? t.GetComponent<RectTransform>() : null;
            if (cam == null || rt == null) return new Vector2(-1f, -1f);
            return RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
        }

        /// <summary>A piece's rectangle on the UI picture, in pixels.</summary>
        private static Rect PixelRect(RectTransform rt)
        {
            var cam = CameraDriver.UiCamera;
            if (cam == null || rt == null) return default;
            var r = rt.rect;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(r.min));
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(r.max));
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>Where the note is, and whether it's inside the window and the picture.</summary>
        private static string NotePlace(out bool inside)
        {
            var n = PixelRect(noteBox);
            var w = PixelRect(window != null ? window.GetComponent<RectTransform>() : null);
            bool picture = n.width > 1f && n.xMin >= -1f && n.yMin >= -1f && n.xMax <= VRUI.Width + 1f && n.yMax <= VRUI.Height + 1f;
            // a window root with no size of its own isn't a frame to be inside of
            bool frame = w.width < 100f || w.height < 100f || (n.xMin >= w.xMin - 2f && n.xMax <= w.xMax + 2f && n.yMin >= w.yMin - 2f && n.yMax <= w.yMax + 2f);
            inside = picture && frame;
            return $"at {UiPicture.Describe(n)} in the window's {UiPicture.Describe(w)} ({(inside ? "inside" : picture ? "OUTSIDE the window" : "OUTSIDE the picture")})";
        }

        private static string Pixel(Transform t)
        {
            var p = Centre(t);
            return p.x < 0f ? "no pixel" : $"({p.x:0}, {p.y:0})";
        }

        /// <summary>Laser click on the entry, a game button, or a row (left or right half of its value).</summary>
        private static string Point(string what, string side)
        {
            var g = Global.Instance;
            if (g == null) return "point: no game";
            Transform t = null;
            Vector2 at;
            switch (what.ToLowerInvariant())
            {
                case "entry": t = entry != null ? entry.transform : null; break;
                case "options": t = g.optionsButton != null ? g.optionsButton.transform : null; break;
                case "resume": t = g.resumeButton != null ? g.resumeButton.transform : null; break;
            }
            if (t != null) at = Centre(t);
            else
            {
                // one of our rows by name, or the option row selected now (the game's too)
                var s = Named(what);
                OptionsButton row = s != null ? s.Row : null;
                if (row == null && what.Equals("selected", StringComparison.OrdinalIgnoreCase))
                {
                    var es = EventSystem.current;
                    var sel = es != null ? es.currentSelectedGameObject : null;
                    row = sel != null ? sel.GetComponentInParent<OptionsButton>() : null;
                }
                if (row == null) return $"point: nothing called '{what}' (entry, options, resume, selected, {Keys()})";
                var value = row.value;
                if (side.Length == 0 || value == null) at = Centre(row.transform);
                else
                {
                    // a quarter of the value's width off centre (MenuValues: left half steps back)
                    var rt = value.rectTransform;
                    float quarter = Mathf.Abs(RectTransformUtility.WorldToScreenPoint(CameraDriver.UiCamera, rt.TransformPoint(new Vector3(rt.rect.xMax, rt.rect.center.y))).x
                                              - RectTransformUtility.WorldToScreenPoint(CameraDriver.UiCamera, rt.TransformPoint(rt.rect.center)).x) * 0.5f;
                    at = Centre(value.transform) + new Vector2(side.Equals("left", StringComparison.OrdinalIgnoreCase) ? -quarter : quarter, 0f);
                }
            }
            if (at.x < 0f) return $"point: '{what}' is not on the UI picture";
            return $"point {what}{(side.Length > 0 ? " " + side : "")}: the laser at ({at.x:0}, {at.y:0}) clicks " + LaserPointer.DebugPoint(at, true);
        }

        /// <summary>Each setting as the row shows it, in memory, in the file and as its system reads it; fails on any mismatch.</summary>
        private static string Check(string label)
        {
            var file = VRConfig.FileValues();
            var sb = new StringBuilder($"vrsettings check {label}:");
            string fail = "";
            for (int i = 0; i < settings.Length; i++)
            {
                var s = settings[i];
                string shown = s.Row != null && s.Row.value != null ? s.Row.value.text : null;
                string expect = Expected(s);
                sb.Append($" {s.Key} {(shown ?? "(no row)")}");
                if (shown != null && shown != expect) fail += $" {s.Key} shows '{shown}' but the settings say '{expect}';";
                for (int k = 0; k < s.Entries.Length; k++)
                {
                    var d = s.Entries[k].Definition;
                    string mem = s.Entries[k].GetSerializedValue();
                    string disk = file.TryGetValue(d.Section + "." + d.Key, out var v) ? v : null;
                    sb.Append($" {d.Key} {mem} (file {(disk ?? "missing")})");
                    if (disk != mem) fail += $" {d.Key} is {mem} in memory but {(disk ?? "missing")} in the file;";
                }
                if (s.Live != null) sb.Append($", {s.Live()}");
                sb.Append(';');
            }
            if (entry != null && entry.activeInHierarchy)
            {
                string n = Neighbours(out var above);
                var options = Global.Instance != null ? Global.Instance.optionsButton : null;
                sb.Append($" entry {n};");
                if (options == null || above == null || above.Pointer != options.transform.Pointer) fail += $" the entry is not right under OPTIONS ({n});";
            }
            if (note != null && window != null && window.activeInHierarchy)
            {
                string at = NotePlace(out bool inside);
                sb.Append($" note {at};");
                if (!inside) fail += $" the note is not inside the window and the picture ({at});";
            }
            if (window != null && window.activeInHierarchy)
            {
                // A row off the picture can't be reached by the laser. Three quarters of its height is enough: the game's
                // window runs a couple of pixels past the bottom, so its last row is always clipped a little.
                int rows = 0;
                string outside = "";
                for (int i = 0; i < settings.Length; i++)
                {
                    var row = settings[i].Row;
                    var r = row != null ? PixelRect(row.GetComponent<RectTransform>()) : default;
                    float shown = Mathf.Min(r.yMax, VRUI.Height) - Mathf.Max(r.yMin, 0f);
                    if (r.width > 1f && r.height > 1f && r.xMin >= -1f && r.xMax <= VRUI.Width + 1f && shown >= r.height * 0.75f) rows++;
                    else outside += $" {settings[i].Key} {UiPicture.Describe(r)}";
                }
                sb.Append($" rows {rows} of {settings.Length} in the picture;");
                if (outside.Length > 0) fail += " rows outside the picture:" + outside + ";";
            }
            sb.Append(fail.Length == 0 ? " -> vrsettings check ok" : " -> vrsettings check FAILED:" + fail);
            string line = sb.ToString();
            if (fail.Length > 0) Plugin.L.LogWarning(line);
            else Plugin.L.LogInfo(line);
            return "checked (" + (fail.Length == 0 ? "ok" : "FAILED") + ")";
        }
    }
}
#endif
