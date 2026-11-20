using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using CloudpunkVR.Input;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The mod's settings in the headset: a VREVIVAL entry under the game's OPTIONS (pause menu and main menu share the
    /// list), a copy of that button. It opens a copy of the game's options window with a copy of its option row per
    /// setting, so it reads, sounds and steers like OPTIONS. Circle goes back.
    ///
    /// The game's own options routine opens and closes it: while it's up, Global.options is swapped for this window,
    /// so the game fades it, handles Circle, counts it as a menu (IsInMenu reads that field) and closes it with the
    /// pause button. The game's window is swapped back as soon as ours closes.
    ///
    /// Changes apply at once and are saved straight away (VRConfig.SetAndSave); start-only ones say so.
    /// </summary>
    internal static partial class VrevivalMenu
    {
        private sealed class Setting
        {
            public string Key, Group, Name, Note;
            public bool AtStart;               // read when the game starts
            public float[] Steps;              // the values offered, in order
            public string[] Labels;
            public Func<float> Read;
            public Action<float> Write;
            public Func<float, string> Custom; // label for a file value that isn't one of the steps
            public ConfigEntryBase[] Entries;
            public Func<string> Live;          // what the system reads now, for the log

            public OptionsButton Row;
            public IntPtr RowGo;
            public readonly List<float> Values = new List<float>();
            public readonly List<string> Names = new List<string>();
            public int Index = -1;
            public string Given = "";          // the value list last given to the row
        }

        private static readonly Color Amber = new Color(0.96f, 0.65f, 0.14f, 1f); // the game's titles and prompts
        private static readonly Color NoteColor = new Color(1f, 1f, 1f, 0.72f);

        private static Setting[] settings;
        private static GameObject entry, window;
        private static CanvasGroup windowGroup;
        private static IntPtr groupPtr, firstPtr; // what we hand the game, compared by pointer (alive or not)
        private static TextMeshProUGUI note;
        private static RectTransform noteBox, listRt, firstLine;
        private static IntPtr builtFor, failedFor;
        private static float nextTry;
        private static string built = "not built yet (it is made the first time the game's menu shows)";

        // while open: the game's own options window and first row, swapped back on close
        private static bool open, shown, broken;
        private static CanvasGroup gameOptions;
        private static GameObject gameFirst;
        private static float openedAt, brokenAt;
        private static int opens, errors;
        private static Setting noteFor;
        private static float nextLook;

        public static void Update()
        {
            try { Tick(); }
            catch (Exception e) { Failed(Global.Instance, e); }
        }

        private static void Tick()
        {
            var g = Global.Instance;
            if (g == null) { open = false; return; }
            if (settings == null) settings = Make();
            if (!open && Time.unscaledTime >= nextLook)
            {
                nextLook = Time.unscaledTime + 0.25f;
                EnsureBuilt(g);
                FollowOptions(g);
            }
            if (!open) return;
            bool up = window != null && window.activeInHierarchy;
            if (up) shown = true;
            bool ours = Is(g.options, groupPtr);
            // closed once the game's routine is done with it: hidden and menu input free again
            if (!ours || (!up && (shown || Time.unscaledTime - openedAt > 3f) && !g.blockMenuInput))
            {
                Close(g, ours ? null : "something else changed the game's options window");
                return;
            }
            if (broken)
            {
                // the game's routine didn't take it away: put the menu back by hand
                if (up && Time.unscaledTime - brokenAt > 3f) ForceClose(g);
                return;
            }
            if (!up) return;
            Poll();
            ShowNote();
        }

        /// <summary>Same object by pointer (a destroyed one still counts).</summary>
        private static bool Is(Il2CppSystem.Object o, IntPtr p) => !ReferenceEquals(o, null) && p != IntPtr.Zero && o.Pointer == p;

        /// <summary>
        /// Something failed while the window was up. It must never stay on screen with the game thinking its menu is
        /// shut: ask the game's own close, and put the menu back by hand if that doesn't happen.
        /// </summary>
        private static void Failed(Global g, Exception e)
        {
            if (errors++ < 5) Plugin.L.LogError("VRevival menu: " + e);
            if (!open || broken) return;
            if (g == null || window == null || !window.activeSelf || !Is(g.options, groupPtr)) { Close(g, "an error"); return; }
            broken = true;
            brokenAt = Time.unscaledTime;
            try { if (!g.blockMenuInput) g.CloseOptions(); }
            catch (Exception) { ForceClose(g); }
        }

        /// <summary>Hide the window and show the game's menu as its own close would.</summary>
        private static void ForceClose(Global g)
        {
            try
            {
                if (window != null) window.SetActive(false);
                if (g != null)
                {
                    PutBack(g);
                    var menu = g.menuCanvas;
                    if (menu != null) { menu.gameObject.SetActive(true); menu.alpha = 1f; }
                    g.blockMenuInput = false;
                    if (g.optionsButton != null) g.SelectUIObject(g.optionsButton);
                }
            }
            catch (Exception x) { Plugin.L.LogError("VRevival menu: putting the game's menu back by hand failed: " + x.Message); }
            Close(g, "an error; the game's menu put back by hand");
        }

        /// <summary>The entry shows when the game's OPTIONS does.</summary>
        private static void FollowOptions(Global g)
        {
            if (entry == null) return;
            var options = g.optionsButton;
            if (options == null) return;
            bool want = options.activeSelf;
            if (entry.activeSelf != want) entry.SetActive(want);
        }

        // ---------------------------------------------------------------- the settings

        private static Setting[] Make()
        {
            var wheelLabels = new string[WheelSteering.Presets.Length];
            for (int i = 0; i < wheelLabels.Length; i++)
                wheelLabels[i] = $"{WheelSteering.PresetNames[i].ToUpperInvariant()} ({WheelSteering.Presets[i]:0})";
            return new[]
            {
                new Setting
                {
                    Key = "wheel", Group = "COCKPIT", Name = "WHEEL SENSITIVITY",
                    Note = "How far you turn the wheel for full steer. Less turns the HOVA sooner.",
                    Steps = WheelSteering.Presets, Labels = wheelLabels,
                    Read = () => VRConfig.WheelLock.Value, Write = v => VRConfig.WheelLock.Value = v,
                    Custom = v => $"CUSTOM ({v:0})", Entries = new ConfigEntryBase[] { VRConfig.WheelLock },
                    Live = () => "the wheel: " + WheelSteering.Describe(),
                },
                Switch("windshield", "COCKPIT", "HUD ON WINDSCREEN", "In the cockpit the HUD sits on the windscreen. Off: it follows your head.", VRConfig.HudOnWindshield),
                new Setting
                {
                    Key = "vignette", Group = "COMFORT", Name = "COMFORT VIGNETTE",
                    Note = "Darkens the edges of your view on hard turns, drops and crashes.",
                    Steps = new[] { 0f, 0.25f, 0.4f, 0.7f }, Labels = new[] { "OFF", "LIGHT", "NORMAL", "STRONG" },
                    Read = () => VRConfig.FlightVignette.Value ? VRConfig.VignetteStrength.Value : 0f,
                    Write = v => { VRConfig.FlightVignette.Value = v > 0.001f; if (v > 0.001f) VRConfig.VignetteStrength.Value = v; },
                    Custom = v => $"{v * 100f:0}%", Entries = new ConfigEntryBase[] { VRConfig.FlightVignette, VRConfig.VignetteStrength },
                },
                new Setting
                {
                    Key = "turning", Group = "COMFORT", Name = "TURNING ON FOOT",
                    Note = "How the right stick turns you on foot. Off: you turn with your body.",
                    Steps = new[] { 30f, 45f, Smooth, Off }, Labels = new[] { "SNAP 30", "SNAP 45", "SMOOTH", "OFF" },
                    Read = () => VRConfig.Turn.Value == TurnMode.Snap ? VRConfig.SnapTurnAngle.Value : VRConfig.Turn.Value == TurnMode.Smooth ? Smooth : Off,
                    Write = v =>
                    {
                        VRConfig.Turn.Value = v >= Off ? TurnMode.Off : v >= Smooth ? TurnMode.Smooth : TurnMode.Snap;
                        if (v < Smooth) VRConfig.SnapTurnAngle.Value = v;
                    },
                    Custom = v => $"SNAP {v:0}", Entries = new ConfigEntryBase[] { VRConfig.Turn, VRConfig.SnapTurnAngle },
                },
                Switch("first-person", "COMFORT", "FIRST PERSON", "On foot. Off: the game's camera behind Rania, not made for a headset.", VRConfig.FirstPersonOnFoot),
                Switch("jog", "COMFORT", "JOG", "On foot, click the left stick while walking to jog.", VRConfig.Jog),
                Switch("crouch", "COMFORT", "CROUCH TO SNEAK", "On foot, crouch for real to sneak.", VRConfig.CrouchToSneak),
                new Setting
                {
                    Key = "hud-distance", Group = "HUD", Name = "HUD DISTANCE",
                    Note = "How far the HUD floats. Its size stays the same.",
                    Steps = new[] { 1.2f, 1.6f, 2f, 2.5f, 3f }, Labels = new[] { "1.2 M", "1.6 M", "2.0 M", "2.5 M", "3.0 M" },
                    Read = () => VRConfig.HudDistance.Value, Write = v => VRConfig.HudDistance.Value = v,
                    Custom = v => $"{v:0.0#} M", Entries = new ConfigEntryBase[] { VRConfig.HudDistance },
                    Live = HudPieces,
                },
                new Setting
                {
                    Key = "hud-size", Group = "HUD", Name = "HUD SIZE",
                    Note = "Size of the HUD's pieces: minimap, jobs, lines.",
                    Steps = new[] { 0.8f, 0.9f, 1f, 1.15f, 1.3f }, Labels = new[] { "80%", "90%", "100%", "115%", "130%" },
                    Read = () => VRConfig.HudScale.Value, Write = v => VRConfig.HudScale.Value = v,
                    Custom = v => $"{v * 100f:0}%", Entries = new ConfigEntryBase[] { VRConfig.HudScale },
                },
                new Setting
                {
                    Key = "vibration", Group = "OTHER", Name = "VIBRATION",
                    Note = "Controller vibration: the game's rumble and the mod's ticks.",
                    Steps = new[] { 0f, 0.5f, 1f, 1.5f }, Labels = new[] { "OFF", "LIGHT", "NORMAL", "STRONG" },
                    Read = () => VRConfig.Haptics.Value ? VRConfig.HapticsStrength.Value : 0f,
                    Write = v => { VRConfig.Haptics.Value = v > 0.001f; if (v > 0.001f) VRConfig.HapticsStrength.Value = v; },
                    Custom = v => $"{v * 100f:0}%", Entries = new ConfigEntryBase[] { VRConfig.Haptics, VRConfig.HapticsStrength },
                },
                new Setting
                {
                    Key = "rain", Group = "OTHER", Name = "RAIN",
                    Note = "World: drops fall around you. Overlay: the game's streaks. Off: neither.",
                    Steps = new[] { (float)RainMode.World, (float)RainMode.Overlay, (float)RainMode.Off }, Labels = new[] { "WORLD", "OVERLAY", "OFF" },
                    Read = () => (float)VRConfig.Rain.Value, Write = v => VRConfig.Rain.Value = (RainMode)(int)v,
                    Entries = new ConfigEntryBase[] { VRConfig.Rain },
                },
                Switch("menu-sign", "OTHER", "MENU SIGN", "VREVIVAL under CLOUDPUNK on the main menu's neon sign.", VRConfig.MenuSign),
                Switch("skip-title", "OTHER", "SKIP TITLE SCREEN", "Skips the game's 'press any key' at start. After a restart.", VRConfig.SkipTitleScreen, true),
                new Setting
                {
                    Key = "mirrors", Group = "OTHER", Name = "MIRROR QUALITY",
                    Note = "The dashboard's rear-view screens. Low and High are sharp and cost a little.",
                    Steps = new[] { (float)MirrorQuality.Game, (float)MirrorQuality.Low, (float)MirrorQuality.High, (float)MirrorQuality.Off },
                    Labels = new[] { "GAME", "LOW", "HIGH", "OFF" },
                    Read = () => (float)VRConfig.Mirrors.Value, Write = v => VRConfig.Mirrors.Value = (MirrorQuality)(int)v,
                    Entries = new ConfigEntryBase[] { VRConfig.Mirrors },
                },
                Switch("rear-mirror", "OTHER", "REAR-VIEW MIRROR", "A mirror under the ceiling, beside Camus. Try it with mirror quality Low.", VRConfig.RearMirror),
            };
        }

        private const float Smooth = 1000f, Off = 2000f; // TURNING's keys past the snap angles

        private static Setting Switch(string key, string group, string name, string note, ConfigEntry<bool> e, bool atStart = false) => new Setting
        {
            Key = key, Group = group, Name = name, Note = note, AtStart = atStart,
            Steps = new[] { 0f, 1f }, Labels = new[] { "OFF", "ON" },
            Read = () => e.Value ? 1f : 0f, Write = v => e.Value = v > 0.5f,
            Entries = new ConfigEntryBase[] { e },
        };

        private static int Find(List<float> values, float v)
        {
            for (int i = 0; i < values.Count; i++)
                if (Mathf.Abs(values[i] - v) < 0.005f) return i;
            return -1;
        }

        /// <summary>The row's values: its steps, plus the file's value if it isn't one of them.</summary>
        private static void Offer(Setting s)
        {
            s.Values.Clear();
            s.Names.Clear();
            for (int i = 0; i < s.Steps.Length; i++) { s.Values.Add(s.Steps[i]); s.Names.Add(s.Labels[i]); }
            float now = s.Read();
            int at = Find(s.Values, now);
            if (at < 0 && s.Custom != null)
            {
                at = 0;
                while (at < s.Values.Count && s.Values[at] < now) at++;
                s.Values.Insert(at, now);
                s.Names.Insert(at, s.Custom(now));
            }
            s.Index = Mathf.Max(at, 0);
        }

        /// <summary>What the row should show now, without touching it.</summary>
        private static string Expected(Setting s)
        {
            float now = s.Read();
            for (int i = 0; i < s.Steps.Length; i++)
                if (Mathf.Abs(s.Steps[i] - now) < 0.005f) return s.Labels[i];
            return s.Custom != null ? s.Custom(now) : s.Labels[0];
        }

        /// <summary>Refresh a row from the settings.</summary>
        private static void Show(Setting s)
        {
            Offer(s);
            var row = s.Row;
            if (row == null) return;
            string given = string.Join("|", s.Names);
            if (given != s.Given)
            {
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                for (int i = 0; i < s.Names.Count; i++) list.Add(s.Names[i]);
                row.values = list;
                s.Given = given;
            }
            row.currentValue = s.Index;
            var value = row.value;
            if (value != null) value.text = s.Names[s.Index];
        }

        private static void ShowAll()
        {
            for (int i = 0; i < settings.Length; i++) Show(settings[i]);
        }

        /// <summary>Rows the game stepped (its ChangeValue, from stick, d-pad or laser): apply and save.</summary>
        private static void Poll()
        {
            for (int i = 0; i < settings.Length; i++)
            {
                var s = settings[i];
                var row = s.Row;
                if (row == null) continue;
                int now = row.currentValue;
                if (now != s.Index && now >= 0 && now < s.Values.Count) Apply(s, s.Values[now], s.Names[now], "the menu");
            }
        }

        /// <summary>Set, save, refresh every row (the reloaded file may hold other changes).</summary>
        private static void Apply(Setting s, float value, string label, string how)
        {
            string from = s.Index >= 0 && s.Index < s.Names.Count ? s.Names[s.Index] : "?";
            string before = Entries(s);
            string saved = VRConfig.SetAndSave(() => s.Write(value));
            ShowAll();
            Plugin.L.LogInfo($"VRevival menu ({how}): {s.Name} {from} -> {label} ({before} -> {Entries(s)}), " +
                             $"{(s.AtStart ? "from the next start" : "live")}; {saved}{InFile(s)}{(s.Live != null ? "; " + s.Live() : "")}.");
        }

        private static string Entries(Setting s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Entries.Length; i++)
                sb.Append(i > 0 ? ", " : "").Append(s.Entries[i].Definition.Key).Append(' ').Append(s.Entries[i].GetSerializedValue());
            return sb.ToString();
        }

        /// <summary>DEV builds: the setting's lines as the file has them now.</summary>
        private static string InFile(Setting s)
        {
            if (!VRConfig.DevHooks) return "";
            var file = VRConfig.FileValues();
            var sb = new StringBuilder(" (the file says");
            for (int i = 0; i < s.Entries.Length; i++)
            {
                var d = s.Entries[i].Definition;
                sb.Append(i > 0 ? ", '" : " '").Append(d.Key).Append(" = ")
                  .Append(file.TryGetValue(d.Section + "." + d.Key, out var v) ? v : "NOT IN THE FILE").Append('\'');
            }
            return sb.Append(')').ToString();
        }

        /// <summary>How far the HUD pieces float from the eyes now.</summary>
        private static string HudPieces()
        {
            var root = VRUI.Root;
            if (root == null) return "no overlay";
            float near = float.MaxValue, far = 0f;
            int n = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (!c.gameObject.activeSelf || !c.name.StartsWith("CloudpunkVR_HudPiece", StringComparison.Ordinal)) continue;
                float d = Vector3.Distance(VRUI.HudEye, c.position);
                near = Mathf.Min(near, d);
                far = Mathf.Max(far, d);
                n++;
            }
            return n == 0 ? "no HUD piece in view now (HudDistance applies when the HUD shows)" : $"{n} HUD piece(s) {near:0.00} to {far:0.00} m from the eyes";
        }

        // ---------------------------------------------------------------- opening and closing

        /// <summary>VREVIVAL pressed (the game's MenuButton ignores it while a menu routine runs).</summary>
        private static void OnEntry()
        {
            try { OpenWindow("the VREVIVAL entry"); }
            catch (Exception e) { Failed(Global.Instance, e); }
        }

        private static void OpenWindow(string how)
        {
            var g = Global.Instance;
            if (g == null || open || g.blockMenuInput || window == null || windowGroup == null) return;
            if (Is(g.options, groupPtr)) return; // still swapped in, a close is still running
            ShowAll();
            gameOptions = g.options;
            gameFirst = g.optionsFirst;
            g.options = windowGroup;
            g.optionsFirst = settings[0].Row != null ? settings[0].Row.gameObject : gameFirst;
            open = true;
            shown = broken = false;
            noteFor = null;
            if (note != null) note.text = "";
            openedAt = Time.unscaledTime;
            opens++;
            g.Options(); // the game's routine: shows it, fades it in over the menu, selects the first row
            try { PlaceNote(); }
            catch (Exception e) { if (errors++ < 5) Plugin.L.LogWarning("VRevival menu: the note could not be placed: " + e.Message); }
            Plugin.L.LogInfo($"VRevival menu: opened ({how}, #{opens}) through the game's options routine; {settings.Length} settings, {settings[0].Name} first.");
        }

        /// <summary>The game is done with it (Circle, pause button, an error): swap the game's window back.</summary>
        private static void Close(Global g, string why)
        {
            PutBack(g);
            open = broken = false;
            bool menu = false, inMenu = false;
            try
            {
                menu = g != null && g.menuCanvas != null && g.menuCanvas.gameObject.activeInHierarchy;
                inMenu = g != null && g.IsInMenu();
            }
            catch (Exception) { /* the log line only */ }
            Plugin.L.LogInfo($"VRevival menu: closed{(why != null ? " (" + why + ")" : menu ? ", back on the game's menu" : " with the game's menu")}; " +
                             $"the game's options window is its own again; in a menu {inMenu}.");
        }

        private static void PutBack(Global g)
        {
            if (g == null) return;
            if (!ReferenceEquals(gameOptions, null) && Is(g.options, groupPtr)) g.options = gameOptions;
            if (!ReferenceEquals(gameFirst, null) && Is(g.optionsFirst, firstPtr)) g.optionsFirst = gameFirst;
        }

        /// <summary>The note says what the selected row does.</summary>
        private static void ShowNote()
        {
            if (note == null) return;
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            Setting s = null;
            if (go != null)
            {
                var p = go.Pointer;
                for (int i = 0; i < settings.Length; i++)
                    if (settings[i].RowGo == p) { s = settings[i]; break; }
            }
            if (s == noteFor) return;
            noteFor = s;
            if (s != null) note.text = s.Note;
        }

        /// <summary>
        /// The note right of the values, level with the first header, 6 to 12 rows wide. Placed on opening, after a
        /// forced layout (the game's layout only runs on windows that show).
        /// </summary>
        private static void PlaceNote()
        {
            if (noteBox == null || listRt == null || firstLine == null || window == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(listRt);
            var root = window.GetComponent<RectTransform>();
            Rect area = root.rect;
            float right = float.MinValue, rowH = 0f;
            for (int i = 0; i < settings.Length; i++)
            {
                var row = settings[i].Row;
                var value = row != null ? row.value : null;
                if (value == null) continue;
                var r = value.rectTransform.rect;
                right = Mathf.Max(right, root.InverseTransformPoint(value.rectTransform.TransformPoint(new Vector3(r.xMax, r.center.y))).x);
                rowH = Mathf.Max(rowH, row.GetComponent<RectTransform>().rect.height);
            }
            if (right == float.MinValue || rowH <= 0f) return;
            var f = firstLine.rect;
            float top = root.InverseTransformPoint(firstLine.TransformPoint(new Vector3(f.xMin, f.yMax))).y;
            float x = right + rowH;
            float width = Mathf.Clamp(area.xMax - rowH * 0.5f - x, rowH * 6f, rowH * 12f);
            noteBox.anchorMin = noteBox.anchorMax = new Vector2(0.5f, 0.5f);
            noteBox.pivot = new Vector2(0f, 1f);
            noteBox.anchoredPosition = new Vector2(x, top) - area.center;
            noteBox.sizeDelta = new Vector2(width, rowH * 4f);
        }

        // ---------------------------------------------------------------- made from the game's own menu

        /// <summary>
        /// Built from the game's pieces once its menu shows with options closed. On failure the game's menu is left as
        /// it was and the cfg stays the way to change settings.
        /// </summary>
        private static void EnsureBuilt(Global g)
        {
            if (g.Pointer != builtFor && builtFor != IntPtr.Zero) Forget();
            if (builtFor != IntPtr.Zero && entry != null && window != null) return;
            if (g.Pointer == failedFor || Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 0.5f;
            var menu = g.menuCanvas;
            var options = g.options;
            var button = g.optionsButton;
            if (menu == null || options == null || button == null) return;
            if (!menu.gameObject.activeInHierarchy || options.gameObject.activeSelf || g.blockMenuInput) return;
            Discard();
            try
            {
                string win = BuildWindow(options);
                string ent = BuildEntry(button);
                ShowAll();
                groupPtr = windowGroup.Pointer;
                firstPtr = settings[0].RowGo;
                builtFor = g.Pointer;
                built = ent + "; " + win;
                Plugin.L.LogInfo("VRevival menu: " + built + ".");
            }
            catch (Exception e)
            {
                failedFor = g.Pointer;
                Discard();
                built = "not built: " + e.Message;
                Plugin.L.LogWarning("VRevival menu: could not be made from the game's menu, so it is not offered this session (the cfg still works): " + e);
            }
        }

        /// <summary>New Global: what we built went with the old menu.</summary>
        private static void Forget()
        {
            entry = window = null;
            windowGroup = null;
            groupPtr = firstPtr = IntPtr.Zero;
            note = null;
            noteBox = listRt = firstLine = null;
            for (int i = 0; settings != null && i < settings.Length; i++) { settings[i].Row = null; settings[i].RowGo = IntPtr.Zero; settings[i].Given = ""; }
            undo.Clear();
            builtFor = IntPtr.Zero;
            open = false;
        }

        // changes to the game's menu (buttons moved, navigation threaded), undone if building fails half way
        private static readonly List<Action> undo = new List<Action>();

        private static void Discard()
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try { undo[i](); }
                catch (Exception) { /* already gone */ }
            }
            if (entry != null) UnityEngine.Object.DestroyImmediate(entry);
            if (window != null) UnityEngine.Object.DestroyImmediate(window);
            Forget();
        }

        /// <summary>Copies drop the game's localisation, or it would write its own words back.</summary>
        private static void Unhook(GameObject go)
        {
            var texts = go.GetComponentsInChildren<LocalizedText>(true);
            for (int i = 0; i < texts.Length; i++) UnityEngine.Object.DestroyImmediate(texts[i]);
            var selects = go.GetComponentsInChildren<SelectObject>(true);
            for (int i = 0; i < selects.Length; i++) UnityEngine.Object.DestroyImmediate(selects[i]);
        }

        /// <summary>
        /// VREVIVAL under OPTIONS, a copy of that button. In a layout group it just slots in; placed by hand, the
        /// buttons below move down a step. Explicit navigation is threaded through it.
        /// </summary>
        private static string BuildEntry(GameObject options)
        {
            var parent = options.transform.parent;
            entry = UnityEngine.Object.Instantiate(options, parent);
            entry.name = "CloudpunkVR_VREVIVAL";
            Unhook(entry);
            var label = entry.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = "VREVIVAL";
            var click = (UnityAction)new Action(OnEntry);
            var menuButton = entry.GetComponent<MenuButton>();
            var button = entry.GetComponent<Button>();
            if (button != null) button.onClick = new Button.ButtonClickedEvent();
            if (menuButton != null)
            {
                menuButton.clickAction = new UnityEvent();
                menuButton.clickAction.AddListener(click);
            }
            else if (button != null) button.onClick.AddListener(click);
            else throw new InvalidOperationException("the OPTIONS button has neither the game's MenuButton nor a Button");
            entry.transform.SetSiblingIndex(options.transform.GetSiblingIndex() + 1);

            string place;
            if (parent.GetComponent<LayoutGroup>() != null)
            {
                // the game's menu list runs bottom up: after OPTIONS in order can mean above it
                var list = parent.GetComponent<RectTransform>();
                LayoutRebuilder.ForceRebuildLayoutImmediate(list);
                bool upward = Height(parent, entry.transform) > Height(parent, options.transform);
                if (upward)
                {
                    entry.transform.SetSiblingIndex(options.transform.GetSiblingIndex());
                    LayoutRebuilder.ForceRebuildLayoutImmediate(list);
                }
                place = upward ? "in the list's layout group, which runs bottom up" : "in the list's layout group";
            }
            else
            {
                var at = options.GetComponent<RectTransform>();
                float y = at.anchoredPosition.y, step = 0f;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c.Pointer == options.transform.Pointer || c.Pointer == entry.transform.Pointer || c.GetComponent<Selectable>() == null) continue;
                    var rt = c.GetComponent<RectTransform>();
                    float dy = rt != null ? Mathf.Abs(rt.anchoredPosition.y - y) : 0f;
                    if (dy > 1f && (step == 0f || dy < step)) step = dy;
                }
                if (step == 0f) step = at.rect.height;
                int moved = 0;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c.Pointer == entry.transform.Pointer) continue;
                    var rt = c.GetComponent<RectTransform>();
                    if (rt == null || rt.anchoredPosition.y >= y - 1f) continue;
                    var was = rt.anchoredPosition;
                    rt.anchoredPosition = was - new Vector2(0f, step);
                    undo.Add(() => rt.anchoredPosition = was);
                    moved++;
                }
                entry.GetComponent<RectTransform>().anchoredPosition = at.anchoredPosition - new Vector2(0f, step);
                place = $"placed by hand {step:0} px under it, {moved} piece(s) below moved down";
            }
            return $"'VREVIVAL' added under the game's OPTIONS ({Diagnostics.Path(parent)}, {place}; {Neighbours()}; navigation {Thread(options, entry)})";
        }

        /// <summary>Height in the list's own space.</summary>
        private static float Height(Transform list, Transform t) => list.InverseTransformPoint(t.position).y;

        /// <summary>The buttons drawn just above and below the entry.</summary>
        private static string Neighbours() => Neighbours(out _);

        private static string Neighbours(out Transform upper)
        {
            upper = null;
            if (entry == null) return "no entry";
            var list = entry.transform.parent;
            float y = Height(list, entry.transform), above = float.MaxValue, below = float.MinValue;
            string up = "nothing", down = "nothing";
            for (int i = 0; i < list.childCount; i++)
            {
                var c = list.GetChild(i);
                if (c.Pointer == entry.transform.Pointer || !c.gameObject.activeSelf || c.GetComponent<Selectable>() == null) continue;
                float h = Height(list, c);
                if (h > y && h < above) { above = h; up = c.name; upper = c; }
                if (h < y && h > below) { below = h; down = c.name; }
            }
            return $"between '{up}' and '{down}'";
        }

        /// <summary>Explicit navigation: OPTIONS, VREVIVAL, then what came after OPTIONS. Automatic finds it alone.</summary>
        private static string Thread(GameObject options, GameObject added)
        {
            var o = options.GetComponent<Selectable>();
            var a = added.GetComponent<Selectable>();
            if (o == null || a == null) return "none";
            var nav = o.navigation;
            if (nav.mode != Navigation.Mode.Explicit) return nav.mode.ToString().ToLowerInvariant();
            var below = nav.selectOnDown;
            var mine = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = o, selectOnDown = below, selectOnLeft = nav.selectOnLeft, selectOnRight = nav.selectOnRight };
            a.navigation = mine;
            nav.selectOnDown = a;
            o.navigation = nav;
            undo.Add(() => { var n = o.navigation; n.selectOnDown = below; o.navigation = n; });
            if (below != null)
            {
                var bn = below.navigation;
                if (bn.mode == Navigation.Mode.Explicit && bn.selectOnUp != null && bn.selectOnUp.Pointer == o.Pointer)
                {
                    bn.selectOnUp = a;
                    below.navigation = bn;
                    undo.Add(() => { var n = below.navigation; n.selectOnUp = o; below.navigation = n; });
                }
            }
            return $"explicit, OPTIONS -> VREVIVAL -> {(below != null ? below.name : "nothing")}";
        }

        /// <summary>
        /// A copy of the game's options window (closed, so nothing in it has started), all rows removed but one, the
        /// model for headers, rows and the note. Anything else clickable is switched off: it would act on the game's
        /// own options.
        /// </summary>
        private static string BuildWindow(CanvasGroup options)
        {
            window = UnityEngine.Object.Instantiate(options.gameObject, options.transform.parent);
            window.name = "CloudpunkVR_VRevival";
            window.SetActive(false);
            Unhook(window);
            windowGroup = window.GetComponent<CanvasGroup>();
            if (windowGroup == null) throw new InvalidOperationException("the options window has no CanvasGroup");

            var rows = window.GetComponentsInChildren<OptionsButton>(true);
            OptionsButton model = null;
            for (int i = 0; i < rows.Length && model == null; i++)
                if (rows[i] != null && rows[i].buttons != null && rows[i].value != null) model = rows[i];
            for (int i = 0; i < rows.Length && model == null; i++)
                if (rows[i] != null && rows[i].value != null) model = rows[i];
            if (model == null) throw new InvalidOperationException($"no option row with a value among {rows.Length} in the options window");
            var names = Words(model);
            if (names == null) throw new InvalidOperationException($"the option row '{model.name}' has no name text besides its value");
            string namesName = names.name;
            var list = model.transform.parent;
            var modelRt = model.GetComponent<RectTransform>();
            float rowH = Mathf.Max(8f, modelRt.rect.height);

            // for a hand-placed list: the top row's position and the step between rows
            float top = float.MinValue, second = float.MinValue;
            Vector2 topPos = modelRt.anchoredPosition;
            int inList = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null || rows[i].transform.parent.Pointer != list.Pointer) continue;
                inList++;
                var p = rows[i].GetComponent<RectTransform>().anchoredPosition;
                if (p.y > top) { second = top; top = p.y; topPos = p; }
                else if (p.y > second) second = p.y;
            }
            float step = second > float.MinValue && top - second > 1f ? top - second : rowH * 1.2f;
            bool layout = list.GetComponent<LayoutGroup>() != null;

            // game rows out, model set aside
            string modelName = model.name;
            model.transform.SetParent(window.transform, false);
            model.gameObject.SetActive(false);
            for (int i = 0; i < rows.Length; i++)
                if (rows[i] != null && rows[i].Pointer != model.Pointer) UnityEngine.Object.DestroyImmediate(rows[i].gameObject);

            var off = new StringBuilder();
            var selectables = window.GetComponentsInChildren<Selectable>(true);
            for (int i = 0; i < selectables.Length; i++)
            {
                var s = selectables[i];
                if (s == null || s.transform.IsChildOf(model.transform)) continue;
                var bar = s.TryCast<Scrollbar>();
                if (bar != null || list.IsChildOf(s.transform)) { s.navigation = new Navigation { mode = Navigation.Mode.None }; continue; }
                off.Append(off.Length > 0 ? ", " : "").Append(s.name);
                s.gameObject.SetActive(false);
            }

            // title = largest visible text outside the rows; other text left in the list goes
            TextMeshProUGUI title = null;
            var texts = window.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                var t = texts[i];
                if (t == null || t.transform.IsChildOf(model.transform) || !Showing(t.transform)) continue;
                if (title == null || t.fontSize > title.fontSize) title = t;
            }
            if (title != null) title.text = "VREVIVAL";
            for (int i = 0; i < texts.Length; i++)
            {
                var t = texts[i];
                if (t == null || t.transform.IsChildOf(model.transform) || !t.transform.IsChildOf(list) || (title != null && t.Pointer == title.Pointer)) continue;
                if (title != null && title.transform.IsChildOf(t.transform)) continue;
                off.Append(off.Length > 0 ? ", " : "").Append(t.name);
                t.gameObject.SetActive(false);
            }

            int line = 0;
            string group = null;
            for (int i = 0; i < settings.Length; i++)
            {
                var s = settings[i];
                if (s.Group != group)
                {
                    group = s.Group;
                    var header = Line(model, list, "VRevival " + group, out var words);
                    if (words != null) { words.text = group; words.color = Amber; }
                    Place(header, layout, topPos, step, rowH, line++, 1);
                    if (firstLine == null) firstLine = header;
                }
                Place(Row(model, list, s), layout, topPos, step, rowH, line++, 1);
            }
            // up/down skip the headers; left/right stay the row's own (its value)
            for (int i = 0; i < settings.Length; i++)
            {
                settings[i].Row.GetComponent<Selectable>().navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = i > 0 ? settings[i - 1].Row.GetComponent<Selectable>() : null,
                    selectOnDown = i + 1 < settings.Length ? settings[i + 1].Row.GetComponent<Selectable>() : null,
                };
            }
            // the note sits outside the list (it's as long as the game's already), placed in PlaceNote
            noteBox = Line(model, list, "VRevival note", out note);
            noteBox.SetParent(window.transform, false);
            var keepOut = noteBox.GetComponent<LayoutElement>();
            if (keepOut == null) keepOut = noteBox.gameObject.AddComponent<LayoutElement>();
            keepOut.ignoreLayout = true;
            if (note != null)
            {
                note.text = "";
                note.color = NoteColor;
                note.fontSize *= 0.9f;
                note.enableWordWrapping = true;
                note.overflowMode = TextOverflowModes.Overflow;
                note.alignment = TextAlignmentOptions.TopLeft;
                var nt = note.rectTransform;
                nt.anchorMin = Vector2.zero;
                nt.anchorMax = Vector2.one;
                nt.offsetMin = nt.offsetMax = Vector2.zero;
            }
            listRt = list.GetComponent<RectTransform>();
            UnityEngine.Object.DestroyImmediate(model.gameObject);

            return $"window made from the game's options window ({Diagnostics.Path(list)}, {inList} rows {(layout ? "in a layout group" : $"placed by hand, {step:0} px apart")}), " +
                   $"{settings.Length} settings under {Groups()} headers in copies of its row '{modelName}' (names in '{namesName}'), title {(title != null ? "'" + title.name + "'" : "not found")}" +
                   (off.Length > 0 ? $"; switched off in the copy: {off}" : "");
        }

        /// <summary>Visible when the window is: it and every parent up to the root are active.</summary>
        private static bool Showing(Transform t)
        {
            var root = window.transform.Pointer;
            for (; t != null && t.Pointer != root; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        private static int Groups()
        {
            int n = 0;
            string g = null;
            for (int i = 0; i < settings.Length; i++)
                if (settings[i].Group != g) { g = settings[i].Group; n++; }
            return n;
        }

        /// <summary>Next line of the list: appended to the layout, or a step below the last one by hand.</summary>
        private static void Place(RectTransform rt, bool layout, Vector2 top, float step, float rowH, float line, int lines)
        {
            if (lines > 1)
            {
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, rowH * lines);
                if (layout)
                {
                    var le = rt.GetComponent<LayoutElement>();
                    if (le == null) le = rt.gameObject.AddComponent<LayoutElement>();
                    le.minHeight = rowH * lines;
                    le.preferredHeight = rowH * lines;
                }
            }
            if (layout) { rt.SetAsLastSibling(); return; }
            // top aligned with where a row's top would be
            float pivot = rt.pivot.y;
            float lineTop = top.y - step * line + rowH * (1f - pivot);
            rt.anchoredPosition = new Vector2(top.x, lineTop - rt.rect.height * (1f - pivot));
        }

        /// <summary>A setting's row: a copy of the game's row, its values ours, none of the game's hooks.</summary>
        private static RectTransform Row(OptionsButton model, Transform list, Setting s)
        {
            var go = UnityEngine.Object.Instantiate(model.gameObject, list);
            go.name = "VRevival " + s.Name;
            go.SetActive(true);
            var row = go.GetComponent<OptionsButton>();
            // fresh events: the copied ones still call the game's setup and apply for the original row
            row.setupAction = new UnityEvent();
            row.applyAction = new UnityEvent();
            row.loopValue = s.Steps.Length == 2; // on/off wraps, a scale stops at its ends
            var self = go.GetComponent<Selectable>();
            if (self == null) throw new InvalidOperationException($"the option row '{model.name}' cannot be selected");
            var parts = go.GetComponentsInChildren<Selectable>(true);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p == null || p.Pointer == self.Pointer) continue;
                // arrows stay drawn but aren't buttons: the laser steps by the half it points at (MenuValues),
                // and the d-pad never stops on them
                UnityEngine.Object.DestroyImmediate(p);
            }
            self.enabled = true; // d-pad moves through it (OnMove); the laser takes the row itself (LaserPointer)
            var button = self.TryCast<Button>();
            if (button != null) button.onClick = new Button.ButtonClickedEvent();
            var words = Words(row);
            if (words != null) words.text = s.Name;
            s.Row = row;
            s.RowGo = go.Pointer;
            s.Given = "";
            return go.GetComponent<RectTransform>();
        }

        /// <summary>A text line (header, note) from a copy of the game's row, nothing selectable.</summary>
        private static RectTransform Line(OptionsButton model, Transform list, string name, out TextMeshProUGUI words)
        {
            var go = UnityEngine.Object.Instantiate(model.gameObject, list);
            go.name = name;
            go.SetActive(true);
            var row = go.GetComponent<OptionsButton>();
            var group = row.canvasGroup;
            var value = row.value;
            var arrows = row.buttons;
            words = Words(row);
            UnityEngine.Object.DestroyImmediate(row);
            var parts = go.GetComponentsInChildren<Selectable>(true);
            for (int i = 0; i < parts.Length; i++) UnityEngine.Object.DestroyImmediate(parts[i]);
            var sounds = go.GetComponentsInChildren<UIInteractionSounds>(true);
            for (int i = 0; i < sounds.Length; i++) UnityEngine.Object.DestroyImmediate(sounds[i]);
            if (value != null && (words == null || value.Pointer != words.Pointer)) value.text = "";
            if (group != null) group.alpha = 1f;
            if (arrows != null && arrows.Pointer != go.Pointer) UnityEngine.Object.DestroyImmediate(arrows);
            return go.GetComponent<RectTransform>();
        }

        /// <summary>A row's name: the text that's neither the value nor on the arrows.</summary>
        private static TextMeshProUGUI Words(OptionsButton row)
        {
            var value = row.value;
            var arrows = row.buttons;
            var texts = row.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                var t = texts[i];
                if (t == null || (value != null && t.Pointer == value.Pointer)) continue;
                if (arrows != null && t.transform.IsChildOf(arrows.transform)) continue;
                return t;
            }
            return null;
        }
    }
}
