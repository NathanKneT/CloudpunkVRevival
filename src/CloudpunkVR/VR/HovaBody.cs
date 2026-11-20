using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// The world camera keeps the horizon level while the HOVA banks, so the hull's edges would cross the cockpit
    /// windows. In the cockpit view it skips the hull (shadows kept) and draws the glass pinned to the view anchor.
    /// </summary>
    internal static class HovaBody
    {
        private struct Piece
        {
            public Renderer R;
            public string Name;
            public int Layer;
            public ShadowCastingMode Mode;
            public bool Off, Changed, Eligible;
        }

        private struct Root
        {
            public Transform T;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public bool Moved;
            public bool HasRef; // place vs the anchor, noted while the cockpit is aligned
            public Vector3 RefPos;
            public Quaternion RefRot;
        }

        /// <summary>How the world camera draws the windows (dev "hova glass").</summary>
        public enum GlassMode { Pin, Car, Pair, None }
        public static GlassMode Glass = GlassMode.Pin;

        private static Piece[] pieces = new Piece[0];
        private static int count;
        private static readonly Root[] roots = new Root[2];
        private static int rootCount;
        private static Transform builtFor;
        private static int builtChildren = -1, sceneSeen = -1;
        private static bool active, hidden, wasActive;
        private static int activeFrame = -1;
        private static Camera worldCam;
        private static IntPtr worldPtr, cockpitPtr, overlayPtr; // compared by pointer: no interop call per camera
        private static int hideFrame = -1;
        private static int hiddenRenders, alignedRenders, restoreErrors;
        private static float peakAngle, peakShift;

        // a logged turn starts at 8° of body vs cockpit, ends under half its peak
        private const float TurnStart = 8f;
        private const int MaxTurnLines = 30;
        private static bool inTurn;
        private static float turnPeak;
        private static int turns;

        /// <summary>Dev "hova show": world camera draws the body again.</summary>
        public static bool ShowForTest;

        /// <summary>Once per frame before any camera draws, after CameraDriver.</summary>
        public static void Update()
        {
            Restore();
            var g = Global.Instance;
            var car = g != null && g.playerCar != null ? g.playerCar.transform : null;
            worldCam = CameraDriver.ViewCamera;
            worldPtr = worldCam != null ? worldCam.Pointer : IntPtr.Zero;
            cockpitPtr = CameraDriver.CockpitCamera != null ? CameraDriver.CockpitCamera.Pointer : IntPtr.Zero;
            overlayPtr = CameraDriver.OverlayCamera != null ? CameraDriver.OverlayCamera.Pointer : IntPtr.Zero;
            active = !ShowForTest && car != null && worldCam != null && CameraDriver.Mode == ViewMode.Cockpit
                     && Global.IsInVehicleCockpit && CameraDriver.CockpitCamera != null;
            if (!active)
            {
                if (wasActive) Plugin.L.LogInfo("Cockpit: the HOVA's body is drawn by the world camera again" + (ShowForTest ? " (hova show)." : " (out of the cockpit view)."));
                wasActive = false;
                EndTurn();
                return;
            }
            // frames this didn't run for (VR session lost) hide nothing
            activeFrame = Time.frameCount;

            // rebuilt on entering the view, on child changes (upgrades) or a scene load
            int children = car.childCount;
            if (!wasActive || car != builtFor || children != builtChildren || SceneWatch.Changed(ref sceneSeen)) Build(car, children);

            if (CameraDriver.CockpitAligned) NoteReferences(car);
            LogTrace(car);

            float angle = CameraDriver.HullAngle, shift = CameraDriver.HullShift;
            if (angle > peakAngle) peakAngle = angle;
            if (shift > peakShift) peakShift = shift;
            if (!wasActive)
            {
                wasActive = true;
                Plugin.L.LogInfo($"Cockpit: the HOVA's own body ({Pieces()}) is left out of the world camera, {Roots()}; the interior camera draws the cockpit, shadows kept.");
            }
            if (!inTurn && angle >= TurnStart) { inTurn = true; turnPeak = angle; }
            else if (inTurn)
            {
                if (angle > turnPeak) turnPeak = angle;
                if (angle < turnPeak * 0.5f) EndTurn();
            }
        }

        /// <summary>Logs a finished turn's peak (up to MaxTurnLines a session).</summary>
        private static void EndTurn()
        {
            if (!inTurn) return;
            inTurn = false;
            turns++;
            if (turns > MaxTurnLines) return;
            Plugin.L.LogInfo($"Cockpit: the HOVA's body turned up to {turnPeak:0.0} deg against the cockpit (bank {turns}: bank, climb or dive since the seat was taken); " +
                             $"left out of the world camera ({count} mesh(es), {hiddenRenders} render(s) so far, windows lined up in {alignedRenders}), so its edges did not cross the windows." +
                             (turns == MaxTurnLines ? " Further banks are not logged." : ""));
        }

        /// <summary>From RenderHook: hide for the world camera, restore for the interior and overlay cameras.</summary>
        public static void OnPreCull(Camera cam)
        {
            if (!hidden && !active) return;
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            IntPtr p = cam.Pointer;
            if (active && activeFrame == Time.frameCount && p == worldPtr)
            {
                if (!hidden) hiddenRenders++;
                Hide();
            }
            else if (hidden && (p == cockpitPtr || p == overlayPtr)) Restore();
            else return;
            FrameTiming.Lap(t, "HovaBody.PreCull");
        }

        /// <summary>The rear mirror camera sits inside the HOVA. True if this hid the body, the mirror's post-render restores it.</summary>
        public static bool HideForMirror()
        {
            if (hidden || !active || activeFrame != Time.frameCount) return false;
            Hide(false);
            return true;
        }

        private static void Hide(bool windows = true)
        {
            if (hidden) return;
            hidden = true;
            // read once a frame: the second eye finds them restored
            bool read = hideFrame != Time.frameCount;
            hideFrame = Time.frameCount;
            for (int i = 0; i < count; i++)
            {
                var r = pieces[i].R;
                pieces[i].Changed = false;
                if (read)
                {
                    pieces[i].Eligible = false;
                    if (r == null || !r.enabled) continue;
                    var mode = r.shadowCastingMode;
                    if (mode == ShadowCastingMode.ShadowsOnly) continue; // already out of every camera's picture
                    pieces[i].Mode = mode;
                    pieces[i].Off = mode == ShadowCastingMode.Off;
                    pieces[i].Eligible = true;
                }
                if (!pieces[i].Eligible) continue;
                if (pieces[i].Off) r.enabled = false; // no shadow to keep: off while this camera draws
                else r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                pieces[i].Changed = true;
            }
            if (!windows || Glass == GlassMode.None) return;
            bool moved = false;
            for (int i = 0; i < rootCount; i++)
            {
                var t = roots[i].T;
                roots[i].Moved = false;
                if (t == null || !Placed(i, t.position, t.rotation, out var pos, out var rot)) continue;
                roots[i].LocalPos = t.localPosition;
                roots[i].LocalRot = t.localRotation;
                t.SetPositionAndRotation(pos, rot);
                roots[i].Moved = moved = true;
            }
            if (moved) alignedRenders++;
        }

        private static bool Placed(int i, Vector3 gamePos, Quaternion gameRot, out Vector3 pos, out Quaternion rot)
        {
            return Placed(Glass, i, gamePos, gameRot, out pos, out rot);
        }

        /// <summary>Where the world camera draws this "_Windshields" root this frame.</summary>
        public static void WindowPose(Transform t, out Vector3 pos, out Quaternion rot)
        {
            pos = t.position;
            rot = t.rotation;
            for (int i = 0; i < rootCount; i++)
                if (roots[i].T == t && Placed(i, t.position, t.rotation, out var p, out var r)) { pos = p; rot = r; return; }
        }

        private static bool Placed(GlassMode mode, int i, Vector3 gamePos, Quaternion gameRot, out Vector3 pos, out Quaternion rot)
        {
            pos = gamePos;
            rot = gameRot;
            Vector3 fp;
            Quaternion fr;
            switch (mode)
            {
                case GlassMode.Pin when roots[i].HasRef:
                    pos = CameraDriver.AnchorPosition + CameraDriver.AnchorRotation * roots[i].RefPos;
                    rot = CameraDriver.AnchorRotation * roots[i].RefRot;
                    return true;
                case GlassMode.Pin:
                case GlassMode.Car:
                    if (!CameraDriver.HullFix(out fp, out fr)) return false;
                    break;
                case GlassMode.Pair:
                    if (!CameraDriver.GameFix(out fp, out fr)) return false;
                    break;
                default:
                    return false;
            }
            pos = fp + fr * gamePos;
            rot = fr * gameRot;
            return true;
        }

        /// <summary>Notes each window's place vs the anchor while the cockpit is aligned.</summary>
        private static void NoteReferences(Transform car)
        {
            Quaternion toAnchor = Quaternion.Inverse(CameraDriver.AnchorRotation);
            for (int i = 0; i < rootCount; i++)
            {
                var t = roots[i].T;
                if (t == null) continue;
                roots[i].RefPos = toAnchor * (t.position - CameraDriver.AnchorPosition);
                roots[i].RefRot = toAnchor * t.rotation;
                roots[i].HasRef = true;
            }
            if (rootCount == 0 || roots[0].T == null) return;
            // for the trace
            var t0 = roots[0].T;
            traceRefRootInCar = car != null ? Quaternion.Inverse(car.rotation) * t0.rotation : t0.rotation;
            var frame = Global.Instance != null && Global.Instance.vehicleInterior != null ? Global.Instance.vehicleInterior.interiorLocal : null;
            traceRefRootInFrame = frame != null ? Quaternion.Inverse(frame.rotation) * t0.rotation : t0.rotation;
            if (frontGlass != null) frontPointLocal = t0.InverseTransformPoint(frontGlass.bounds.center);
            frontPointRef = CameraDriver.WorldToAnchorSpace(t0.TransformPoint(frontPointLocal));
            if (cockpitGlass != null) cockpitPointRef = CameraDriver.CockpitToAnchorSpace(cockpitGlass.bounds.center);
            traceHasRef = true;
        }

        // ---- Trace (dev "hova trace <s>"): front glass drift per draw mode, plus the cockpit windscreen.

        private static Renderer frontGlass, cockpitGlass;
        private static Vector3 frontPointLocal, frontPointRef, cockpitPointRef;
        private static Quaternion traceRefRootInCar = Quaternion.identity, traceRefRootInFrame = Quaternion.identity;
        private static bool traceHasRef, loggedSpaces;
        private static float traceUntil, nextTrace, traceStart;
        private static readonly float[] tracePeak = new float[5], traceNow = new float[5];
        private static readonly GlassMode[] TraceModes = { GlassMode.None, GlassMode.Car, GlassMode.Pair, GlassMode.Pin };

#if DEV
        public static void Trace(float seconds)
        {
            traceStart = Time.unscaledTime;
            traceUntil = traceStart + seconds;
            nextTrace = 0f;
            System.Array.Clear(tracePeak, 0, tracePeak.Length);
        }
#endif

        /// <summary>Degrees between a point seen now and at the reference (anchor frame).</summary>
        private static float Drift(Vector3 nowInAnchor, Vector3 refInAnchor)
        {
            return nowInAnchor.sqrMagnitude < 1e-8f || refInAnchor.sqrMagnitude < 1e-8f ? 0f : Vector3.Angle(nowInAnchor, refInAnchor);
        }

        private static void LogTrace(Transform car)
        {
            float now = Time.unscaledTime;
            if (now >= traceUntil || rootCount == 0 || roots[0].T == null || !traceHasRef) return;
            var t0 = roots[0].T;
            var d = traceNow;
            int k = 0;
            foreach (var mode in TraceModes)
            {
                Placed(mode, 0, t0.position, t0.rotation, out var pos, out var rot);
                Vector3 point = pos + rot * Vector3.Scale(t0.lossyScale, frontPointLocal);
                d[k++] = Drift(CameraDriver.WorldToAnchorSpace(point), frontPointRef);
            }
            d[4] = cockpitGlass != null ? Drift(CameraDriver.CockpitToAnchorSpace(cockpitGlass.bounds.center), cockpitPointRef) : 0f;
            for (int i = 0; i < 5; i++) tracePeak[i] = Mathf.Max(tracePeak[i], d[i]);
            if (now < nextTrace) return;
            nextTrace = now + 0.25f;
            var frame = Global.Instance != null && Global.Instance.vehicleInterior != null ? Global.Instance.vehicleInterior.interiorLocal : null;
            float turnedInCar = car != null ? Quaternion.Angle(Quaternion.Inverse(car.rotation) * t0.rotation, traceRefRootInCar) : -1f;
            float turnedInFrame = frame != null ? Quaternion.Angle(Quaternion.Inverse(frame.rotation) * t0.rotation, traceRefRootInFrame) : -1f;
            Plugin.L.LogInfo($"hova trace +{now - traceStart:0.00} s: front glass against the view, peak over the last quarter second: as the game leaves it {tracePeak[0]:0.00} deg, " +
                             $"moved with the body {tracePeak[1]:0.00}, by the game's camera pair {tracePeak[2]:0.00}, pinned {tracePeak[3]:0.00} (drawn: {Glass.ToString().ToLowerInvariant()}); " +
                             $"cockpit's own windscreen {(cockpitGlass != null ? tracePeak[4].ToString("0.00") : "-")} deg; the glass's root turned {turnedInCar:0.0} deg in the HOVA's frame, " +
                             $"{turnedInFrame:0.0} deg in the interior's frame; body {CameraDriver.HullAngle:0.0} deg, game pair {CameraDriver.GamePairAngle:0.0} deg, aligned now {CameraDriver.CockpitAligned}");
            System.Array.Clear(tracePeak, 0, tracePeak.Length);
        }

        /// <summary>Undoes the world camera's changes (also at frame start).</summary>
        public static void Restore()
        {
            if (!hidden) return;
            for (int i = 0; i < count; i++)
            {
                if (!pieces[i].Changed) continue;
                pieces[i].Changed = false;
                try
                {
                    var r = pieces[i].R;
                    if (r == null) continue;
                    if (pieces[i].Off) r.enabled = true;
                    else r.shadowCastingMode = pieces[i].Mode;
                }
                catch (Exception e) { if (restoreErrors++ < 5) Plugin.L.LogWarning($"HOVA body: '{pieces[i].Name}' not put back: {e.Message}"); }
            }
            for (int i = 0; i < rootCount; i++)
            {
                if (!roots[i].Moved) continue;
                roots[i].Moved = false;
                try
                {
                    var t = roots[i].T;
                    if (t != null) { t.localPosition = roots[i].LocalPos; t.localRotation = roots[i].LocalRot; }
                }
                catch (Exception e) { if (restoreErrors++ < 5) Plugin.L.LogWarning("HOVA body: a window not put back: " + e.Message); }
            }
            hidden = false; // only now: a failure above must not leave the rest out for good
        }

        /// <summary>Window glass and rain roots (pinned), then the HOVA's opaque meshes outside them (hidden).</summary>
        private static void Build(Transform car, int children)
        {
            Restore();
            builtFor = car;
            builtChildren = children;
            hideFrame = -1; // new pieces: read again
            count = 0;
            var kept = new Root[rootCount]; // keep pinned places of windows found again
            System.Array.Copy(roots, kept, rootCount);
            rootCount = 0;
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            var rain = vi != null && vi.interiorRainParent != null ? vi.interiorRainParent.transform : null;
            var all = car.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length && rootCount < roots.Length; i++)
                if (all[i] != null && all[i].name == "_Windshields") roots[rootCount++] = new Root { T = all[i] };
            if (rain != null && rain.IsChildOf(car) && rootCount < roots.Length && !UnderRoot(rain)) roots[rootCount++] = new Root { T = rain };
            for (int i = 0; i < rootCount; i++)
                for (int j = 0; j < kept.Length; j++)
                    if (kept[j].HasRef && kept[j].T == roots[i].T) { roots[i].HasRef = true; roots[i].RefPos = kept[j].RefPos; roots[i].RefRot = kept[j].RefRot; }
            traceHasRef = false;
            frontGlass = null;
            if (rootCount > 0 && roots[0].T != null)
            {
                var glass = roots[0].T.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < glass.Length && frontGlass == null; i++)
                    if (glass[i] != null && glass[i].name.StartsWith("Windshield_Front")) frontGlass = glass[i];
                if (frontGlass == null && glass.Length > 0) frontGlass = glass[0];
            }
            cockpitGlass = vi != null && vi.windshieldFrontMesh != null && vi.interiorLocal != null && vi.windshieldFrontMesh.transform.IsChildOf(vi.interiorLocal) ? vi.windshieldFrontMesh : null;
            if (!loggedSpaces) { loggedSpaces = true; Plugin.L.LogInfo("HOVA windows: " + DescribeWindows(vi)); }

            var renderers = car.GetComponentsInChildren<Renderer>(true);
            if (pieces.Length < renderers.Length) pieces = new Piece[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.name.StartsWith("CloudpunkVR") || UnderRoot(r.transform)) continue;
                if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                if (!AnyOpaque(r.sharedMaterials)) continue;
                pieces[count++] = new Piece { R = r, Name = r.name, Layer = r.gameObject.layer };
            }
        }

        private static bool AnyOpaque(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material> materials)
        {
            if (materials == null) return false;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != null && materials[i].renderQueue < 2500) return true;
            return false;
        }

        private static bool UnderRoot(Transform t)
        {
            for (int i = 0; i < rootCount; i++)
                if (roots[i].T != null && t.IsChildOf(roots[i].T)) return true;
            return false;
        }

        private static string Pieces()
        {
            if (count == 0) return "no opaque mesh found under the HOVA";
            int mask = worldCam != null ? worldCam.cullingMask : ~0;
            int drawn = 0, shadows = 0;
            var layers = new StringBuilder();
            var names = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var r = pieces[i].R;
                if (r == null) continue;
                if ((mask & (1 << pieces[i].Layer)) != 0) drawn++;
                if (r.shadowCastingMode != ShadowCastingMode.Off) shadows++;
                string layer = LayerMask.LayerToName(pieces[i].Layer);
                if (layers.ToString().IndexOf(layer, StringComparison.Ordinal) < 0) layers.Append(layers.Length > 0 ? ", " : "").Append(layer);
                if (i < 6) names.Append(i > 0 ? ", " : "").Append(pieces[i].Name);
            }
            return $"{count} opaque mesh(es), {drawn} on layers the world camera draws ({layers}), {shadows} casting shadows: {names}{(count > 6 ? ", ..." : "")}";
        }

        private static string DescribeWindows(VehicleInterior vi)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < rootCount; i++)
            {
                var t = roots[i].T;
                if (t == null) continue;
                sb.Append($"'{t.name}' under '{(t.parent != null ? t.parent.name : "-")}'");
                var renderers = t.GetComponentsInChildren<Renderer>(true);
                sb.Append($" ({renderers.Length} renderer(s):");
                for (int j = 0; j < renderers.Length && j < 12; j++) sb.Append($" {renderers[j].name} [{renderers[j].GetIl2CppType().Name}]");
                sb.Append("); ");
            }
            var front = vi != null ? vi.windshieldFrontMesh : null;
            sb.Append($"the interior's windshieldFrontMesh: {(front == null ? "none" : $"'{front.name}' under '{(front.transform.parent != null ? front.transform.parent.name : "-")}', {(vi.interiorLocal != null && front.transform.IsChildOf(vi.interiorLocal) ? "in the interior set" : "in the world")}")}");
            return sb.ToString();
        }

        private static string Roots()
        {
            if (rootCount == 0) return "no window glass found to line up";
            var sb = new StringBuilder("windows lined up with the cockpit:");
            for (int i = 0; i < rootCount; i++)
                if (roots[i].T != null) sb.Append(' ').Append(roots[i].T.name).Append($" ({roots[i].T.GetComponentsInChildren<Renderer>(true).Length} renderer(s))");
            return sb.ToString();
        }

        /// <summary>One line for the wheel and hova commands.</summary>
        public static string Summary()
        {
            if (ShowForTest) return "body drawn by the world camera (hova show)";
            if (!active) return "body as the game left it";
            return $"body left out of the world camera: {count} mesh(es), {hiddenRenders} render(s), windows {Glass.ToString().ToLowerInvariant()} in {alignedRenders}" +
                   $"{(rootCount > 0 && !roots[0].HasRef ? " (no pinned place noted yet)" : "")}";
        }

        /// <summary>Dev "hova": misalignment and its peak, body meshes, "near" lists drawn meshes within 3 m.</summary>
        public static string Describe(bool near)
        {
            var sb = new StringBuilder();
            sb.Append($"mode {CameraDriver.Mode}, {Summary()}; body against the cockpit {CameraDriver.HullAngle:0.0} deg now, peak {peakAngle:0.0} deg since the last check; " +
                      $"world eye {CameraDriver.HullShift * 100f:0.0} cm off now, peak {peakShift * 100f:0.0} cm; game's camera pair {CameraDriver.GamePairAngle:0.0} deg; " +
                      $"banks logged {turns}; {Pieces()}; {Roots()}");
            peakAngle = peakShift = 0f;
            if (near && worldCam != null)
            {
                Vector3 eye = worldCam.transform.position;
                int mask = worldCam.cullingMask, shown = 0;
                bool wasHidden = hidden;
                if (active) Hide(); // as the world camera sees them
                var all = UnityEngine.Object.FindObjectsOfType<Renderer>();
                sb.Append("; meshes within 3 m of the world eye that it draws:");
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i];
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || (mask & (1 << r.gameObject.layer)) == 0) continue;
                    if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    var b = r.bounds;
                    Vector3 lo = b.min, hi = b.max;
                    var closest = new Vector3(Mathf.Clamp(eye.x, lo.x, hi.x), Mathf.Clamp(eye.y, lo.y, hi.y), Mathf.Clamp(eye.z, lo.z, hi.z));
                    float d = Vector3.Distance(closest, eye);
                    if (d > 3f) continue;
                    var m = r.sharedMaterial;
                    sb.Append($" [{r.name} ({r.GetIl2CppType().Name}, {LayerMask.LayerToName(r.gameObject.layer)}, queue {(m != null ? m.renderQueue : -1)}, {d:0.00} m" +
                              $"{(r.transform.parent != null ? ", under " + r.transform.parent.name : "")}{(hidden && UnderRoot(r.transform) ? ", lined up with the cockpit" : "")})]");
                    shown++;
                }
                if (!wasHidden) Restore();
                if (shown == 0) sb.Append(" none");
            }
            return sb.ToString();
        }
    }
}
