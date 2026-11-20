using System;
using System.Collections.Generic;
using System.Globalization;
using N = System.Numerics;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The cockpit as solid spans in 1 cm columns, for the thrown cup. Level faces close spans (the interior is voxels),
    /// walls and window glass are Thick slabs. Eye frame, meters, x right, y up, z ahead.
    /// </summary>
    internal static class CabinShape
    {
        public const float X0 = -0.9f, Z0 = -0.9f, Cell = 0.01f, Low = -1.6f, High = 0.8f;
        public const int Wide = 220, Deep = 220;
        private const float Far = 10f;
        private const float Shelf = 0.03f;   // a top over a top with no bottom between: shelf thickness
        private const float Same = 0.0005f;  // same piece, same facing, this close: a seam
        /// <summary>Wall and glass depth, more than a column's diagonal.</summary>
        public const float Thick = 0.015f;
        private const float Overlap = 0.001f;   // walls reach past their edges, no cracks
        private const float PaneMargin = 0.02f; // panes reach past their box, no gap at the frame
        private const float PaneClear = 0.03f;  // cabin points stay this far in front of a pane
        private const float HungClear = 0.01f;  // hung things stay this far in front of bent glass
        private const float MaxBend = 0.3f;     // max push of bent glass past the ceiling edge
        private const float PastGlass = 0.3f;   // farther past the glass than this: didn't go through

        /// <summary>Span tag for the set itself; panes use their index.</summary>
        public const int Set = -1;

        private struct Face
        {
            public float Ax, Az, Bx, Bz, Cx, Cz, Ay, By, Cy;
            public bool Up;
            public int Mesh;
        }

        /// <summary>Window glass: centre, outward normal, in-plane axes and half sizes.</summary>
        public struct Pane
        {
            public string Name, Where, How;
            public N.Vector3 Centre, Normal, U, V;
            public float HalfU, HalfV;
            public bool Read;          // triangles read, else the box's plane
            public bool Bent;          // box not flat: fitted to the ceiling at Finish
            public N.Vector3 BoxLo, BoxHi; // eye frame
            public string Dropped;     // why it was left out, or null
        }

        /// <summary>A point surely inside the cabin, to validate panes.</summary>
        public struct Inside
        {
            public string Name;
            public N.Vector3 At;
        }

        private static readonly List<Face> faces = new List<Face>();
        private static readonly List<Pane> panes = new List<Pane>();
        private struct GlassTriangle { public N.Vector3 A, B, C; public short Pane; }
        private static readonly List<GlassTriangle> glass = new List<GlassTriangle>();
        // small set objects (under 40 cm) that bent glass must stay clear of
        private struct Thing { public string Name; public int From, Count; }
        private static readonly List<Thing> things = new List<Thing>();
        private static readonly List<N.Vector3> thingPoints = new List<N.Vector3>();
        private static int[] crossStart, cursor, spanStart, wallStart;
        private static float[] crossY = new float[0], spans = new float[0];
        private static bool[] crossUp = new bool[0];
        private static int[] crossMesh = new int[0];
        private static short[] spanTag = new short[0];
        private static float[] spanGlass = new float[0]; // glass part of a span (not the frame it meets)
        private static int spanCount, usedColumns, wallFaces, faceCount;

        // wall and glass spans, one column each, before Join
        private static int[] wallCol = new int[0];
        private static float[] wallLo = new float[0], wallHi = new float[0];
        private static short[] wallTag = new short[0];
        private static int wallCount;
        private static float[] sortedLo = new float[0], sortedHi = new float[0];
        private static short[] sortedTag = new short[0];
        private static float[] mergeLo = new float[64], mergeHi = new float[64];
        private static short[] mergeTag = new short[64];

        public static bool Ready { get; private set; }
        public static float BuildMs { get; private set; }
        private static string refused;

        public static string Describe(bool eachPane = true) => refused != null ? "not used: " + refused : !Ready ? "no cabin shape" :
            $"{faceCount} faces and {wallFaces} walls in {usedColumns} of {Wide * Deep} columns ({Cell * 100f:0} cm), {spanCount} solid spans, built in {BuildMs:0.0} ms; " +
            (eachPane ? "glass: " + Panes() : $"{panes.Count} pane(s) of glass");

        public static string Panes()
        {
            if (panes.Count == 0) return "none found";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < panes.Count; i++)
            {
                var p = panes[i];
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0} '{1}' ({2}, {3:0.00} x {4:0.00} m, middle {5:0.00} right {6:0.00} up {7:0.00} ahead, facing out {8:0.00} {9:0.00} {10:0.00}){11}",
                    p.Where, p.Name, p.How, 2f * p.HalfU, 2f * p.HalfV, p.Centre.X, p.Centre.Y, p.Centre.Z, p.Normal.X, p.Normal.Y, p.Normal.Z,
                    p.Dropped != null ? " LEFT OUT: " + p.Dropped : ""));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Sanity check: air at the eyes and over the holder, holder on a solid (unless placed by hand).
        /// If it fails the faces were misread and the cup just floats back to the holder.
        /// </summary>
        public static void Check(N.Vector3 holder, bool byHand)
        {
            if (!Ready) return;
            int col = Column(holder.X, holder.Z);
            float ground = Ground(col, holder.Y + 0.01f);
            if (faceCount == 0 || spanCount == 0) refused = "no surface of the cockpit read";
            else if (Solid(Column(0f, 0f), 0f)) refused = "solid where the eyes rest";
            else if (Solid(col, holder.Y + 0.05f)) refused = "solid over the holder";
            else if (!byHand && (float.IsNaN(ground) || MathF.Abs(ground - holder.Y) > 0.012f))
                refused = float.IsNaN(ground) ? "nothing under the holder" : $"the holder's foot {(holder.Y - ground) * 100f:0.0} cm off the solid under it";
            if (refused == null) return;
            Ready = false;
            Plugin.L.LogWarning($"Coffee: the cabin's shape is not used ({refused}): a cup let go away from the holder floats back to it.");
        }

        public static void Begin()
        {
            faces.Clear();
            panes.Clear();
            glass.Clear();
            things.Clear();
            thingPoints.Clear();
            wallCount = wallFaces = 0;
            Ready = false;
            refused = null;
        }

        /// <summary>A level face (eye frame) and its piece; kept if it's over the grid.</summary>
        public static void Add(N.Vector3 a, N.Vector3 b, N.Vector3 c, bool up, int mesh)
        {
            float x0 = MathF.Min(a.X, MathF.Min(b.X, c.X)), x1 = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float z0 = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), z1 = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            if (x1 < X0 || x0 > X0 + Wide * Cell || z1 < Z0 || z0 > Z0 + Deep * Cell) return;
            faces.Add(new Face { Ax = a.X, Az = a.Z, Bx = b.X, Bz = b.Z, Cx = c.X, Cz = c.Z, Ay = a.Y, By = b.Y, Cy = c.Y, Up = up, Mesh = mesh });
        }

        /// <summary>An upright face: solid Thick deep toward <paramref name="into"/>.</summary>
        public static void AddWall(N.Vector3 a, N.Vector3 b, N.Vector3 c, N.Vector3 into)
        {
            if (Prism(a, b, c, into, Set)) wallFaces++;
        }

        /// <summary>A small set object's points (vertices or box corners).</summary>
        public static void AddThing(string name, N.Vector3[] points, int count)
        {
            things.Add(new Thing { Name = name, From = thingPoints.Count, Count = count });
            for (int i = 0; i < count; i++) thingPoints.Add(points[i]);
        }

        /// <summary>Adds a pane; its index is its tag. Unread panes become slabs at Finish.</summary>
        public static int AddPane(Pane pane)
        {
            panes.Add(pane);
            return panes.Count - 1;
        }

        /// <summary>A glass triangle, made solid Thick deep outward at Finish.</summary>
        public static void AddGlass(N.Vector3 a, N.Vector3 b, N.Vector3 c, int pane) =>
            glass.Add(new GlassTriangle { A = a, B = b, C = c, Pane = (short)pane });

        /// <summary>Builds the spans. Fits bent panes first and drops any pane with a cabin point behind it.</summary>
        public static void Finish(List<Inside> inside)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int fitted = panes.Count;
            for (int i = 0; i < panes.Count; i++)
            {
                var p = panes[i];
                // fit bent windscreen / rear window (may add a lid); other bent panes keep the box plane
                if (i < fitted && p.Bent && (p.Where == "the windscreen" || p.Where == "the rear window") && !FitBent(ref p, inside, out string why)) p.Dropped = why;
                if (p.Dropped == null && p.Where == "the windscreen" && p.Normal.Y < -0.05f) p.Dropped = "it would face down out of the cabin";
                for (int k = 0; k < inside.Count && p.Dropped == null; k++)
                    if (Behind(p, inside[k].At, PaneClear, 0.1f)) p.Dropped = string.Format(CultureInfo.InvariantCulture, "the {0} ({1:0.00}, {2:0.00}, {3:0.00}) behind it", inside[k].Name, inside[k].At.X, inside[k].At.Y, inside[k].At.Z);
                panes[i] = p;
                if (p.Dropped != null) { Plugin.L.LogWarning($"Coffee: {p.Where} ('{p.Name}') is left out of the cabin's shape: {p.Dropped}."); continue; }
                if (!p.Read) Slab(p, (short)i);
            }
            for (int i = 0; i < glass.Count; i++)
            {
                var t = glass[i];
                if (panes[t.Pane].Dropped != null) continue;
                var n = N.Vector3.Cross(t.B - t.A, t.C - t.A);
                if (N.Vector3.Dot(n, (t.A + t.B + t.C) / 3f) < 0f) n = -n;
                Prism(t.A, t.B, t.C, n, t.Pane);
            }

            int columns = Wide * Deep;
            if (crossStart == null)
            {
                crossStart = new int[columns + 1];
                cursor = new int[columns];
                spanStart = new int[columns + 1];
                wallStart = new int[columns + 1];
            }
            Array.Clear(cursor, 0, columns);
            for (int f = 0; f < faces.Count; f++) Cut(faces[f], false);
            int total = 0;
            for (int c = 0; c < columns; c++) { crossStart[c] = total; total += cursor[c]; cursor[c] = 0; }
            crossStart[columns] = total;
            if (crossY.Length < total)
            {
                crossY = new float[total + total / 4];
                crossUp = new bool[crossY.Length];
                crossMesh = new int[crossY.Length];
            }
            for (int f = 0; f < faces.Count; f++) Cut(faces[f], true);
            SortWalls(columns);

            spanCount = 0;
            usedColumns = 0;
            for (int c = 0; c < columns; c++)
            {
                spanStart[c] = spanCount;
                int s = crossStart[c], e = crossStart[c + 1];
                if (e > s || wallStart[c + 1] > wallStart[c]) usedColumns++;
                int first = spanCount;
                Sort(s, e);
                Spans(s, e);
                Join(c, first);
            }
            spanStart[columns] = spanCount;
            faceCount = faces.Count;
            Release();
            Ready = true;
            BuildMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>Frees build buffers, keeps only the spans.</summary>
        private static void Release()
        {
            faces.Clear();
            faces.TrimExcess();
            glass.Clear();
            glass.TrimExcess();
            things.Clear();
            thingPoints.Clear();
            thingPoints.TrimExcess();
            crossStart = cursor = wallStart = null;
            crossY = new float[0];
            crossUp = new bool[0];
            crossMesh = wallCol = new int[0];
            wallLo = wallHi = sortedLo = sortedHi = new float[0];
            wallTag = sortedTag = new short[0];
            mergeLo = new float[64];
            mergeHi = new float[64];
            mergeTag = new short[64];
            wallCount = 0;
        }

        /// <summary>
        /// Bent windscreen or rear window (mesh unreadable): a plane from the box's far bottom edge to its top,
        /// pushed out from the ceiling edge until cabin points and hung things are in front. Adds a lid if pushed.
        /// </summary>
        private static bool FitBent(ref Pane p, List<Inside> inside, out string why)
        {
            why = null;
            if (MathF.Abs(p.U.X) < 0.9f) { why = "a bent pane that is not across the cabin: where its glass is is not known"; return false; }
            var lo = p.BoxLo;
            var hi = p.BoxHi;
            float sign = p.Centre.Z >= 0f ? 1f : -1f;    // ahead of the eyes or behind
            float width = hi.X - lo.X, height = hi.Y - lo.Y;
            float near = sign > 0f ? lo.Z : -hi.Z, far = sign > 0f ? hi.Z : -lo.Z; // along the way out
            float edge = float.NaN, edgeY = hi.Y;
            for (int f = 0; f < faces.Count; f++)
            {
                var t = faces[f];
                if (t.Up) continue;
                for (int k = 0; k < 3; k++)
                {
                    float x = k == 0 ? t.Ax : k == 1 ? t.Bx : t.Cx, y = k == 0 ? t.Ay : k == 1 ? t.By : t.Cy, z = (k == 0 ? t.Az : k == 1 ? t.Bz : t.Cz) * sign;
                    if (x < lo.X + 0.1f * width || x > hi.X - 0.1f * width || y < hi.Y - 0.35f * height || y > hi.Y + 0.03f) continue;
                    if (z < near - 0.05f || z > far) continue;
                    if (float.IsNaN(edge) || z > edge) { edge = z; edgeY = y; }
                }
            }
            // small things wholly inside the box and clear of the dashboard: the glass goes past them
            var hung = new List<Inside>();
            int inBox = 0;
            for (int i = 0; i < things.Count; i++)
            {
                var th = things[i];
                bool within = th.Count > 0;
                for (int k = 0; k < th.Count && within; k++)
                {
                    var q = thingPoints[th.From + k];
                    within = q.X >= lo.X - 0.02f && q.X <= hi.X + 0.02f && q.Y >= lo.Y + 0.1f && q.Y <= hi.Y + 0.02f && q.Z >= lo.Z - 0.02f && q.Z <= hi.Z + 0.02f;
                }
                if (!within) continue;
                inBox++;
                for (int k = 0; k < th.Count; k++) hung.Add(new Inside { Name = "'" + th.Name + "'", At = thingPoints[th.From + k] });
            }
            float last = MathF.Max(near, far - 0.02f);
            float start = float.IsNaN(edge) ? near : MathF.Min(MathF.Max(edge, near), last);
            float stop = MathF.Min(last, start + MaxBend);
            string blocking = null, pushedBy = null, ignored = null;
            float cabinTop = float.NaN, top = start;
            for (; top <= stop + 1e-4f; top += 0.005f)
            {
                Place(ref p, lo, hi, sign, top, far);
                blocking = null;
                for (int k = 0; k < inside.Count && blocking == null; k++)
                    if (Behind(p, inside[k].At, PaneClear, 0.1f)) blocking = "the " + inside[k].Name;
                if (blocking == null && float.IsNaN(cabinTop)) cabinTop = top;
                for (int k = 0; k < hung.Count && blocking == null; k++)
                    if (Behind(p, hung[k].At, HungClear)) blocking = hung[k].Name;
                if (blocking == null) break;
                pushedBy = blocking;
            }
            if (blocking != null)
            {
                // still blocked: settle for clearing the cabin points and log the hung thing left behind
                if (float.IsNaN(cabinTop)) { why = $"no glass from its far bottom edge leaves {blocking} in front of it"; return false; }
                top = cabinTop;
                Place(ref p, lo, hi, sign, top, far);
                ignored = blocking;
                Plugin.L.LogInfo($"Coffee: {p.Where} ('{p.Name}'): no glass within {MaxBend * 100f:0} cm past the ceiling's edge leaves {blocking} in front of it; that is left past the glass.");
            }
            p.How += string.Format(CultureInfo.InvariantCulture, ", its glass from its far bottom edge up to {0:0.00} ahead at its top ({1}; {2} small thing(s) of the set hung inside its box kept in front{3}{4})",
                top * sign, float.IsNaN(edge) ? "no ceiling's edge found over it" : string.Format(CultureInfo.InvariantCulture, "the ceiling ends at {0:0.00} ahead, {1:0.00} up", edge * sign, edgeY),
                inBox, pushedBy != null && top > start + 1e-4f ? string.Format(CultureInfo.InvariantCulture, ", pushed out {0:0.00} m past the ceiling's edge by {1}", top - start, ignored != null ? "the cabin's own points" : pushedBy) : "",
                ignored != null ? ", " + ignored + " left past it" : "");
            // lid from the ceiling edge to the glass top
            if (!float.IsNaN(edge) && top > start + 0.01f)
            {
                var a = new N.Vector3(0.5f * (lo.X + hi.X), edgeY, start * sign);
                var b = new N.Vector3(a.X, hi.Y, top * sign);
                var along = (b - a) * 0.5f;
                var lid = new Pane
                {
                    Name = p.Name, Where = p.Where, How = "its lid: from the ceiling's edge up to the top of its glass",
                    Centre = (a + b) * 0.5f, U = N.Vector3.UnitX, HalfU = 0.5f * width, HalfV = along.Length(),
                };
                lid.V = along / lid.HalfV;
                lid.Normal = N.Vector3.Normalize(N.Vector3.Cross(lid.U, lid.V));
                if (N.Vector3.Dot(lid.Normal, lid.Centre) < 0f) lid.Normal = -lid.Normal; // away from the eyes
                panes.Add(lid);
            }
            return true;
        }

        /// <summary>Sets a bent pane's plane, far bottom edge to top at this distance out.</summary>
        private static void Place(ref Pane p, N.Vector3 lo, N.Vector3 hi, float sign, float top, float far)
        {
            float mid = 0.5f * (lo.X + hi.X);
            var a = new N.Vector3(mid, hi.Y, top * sign);
            var b = new N.Vector3(mid, lo.Y, far * sign);
            var across = (a - b) * 0.5f;
            p.Centre = (a + b) * 0.5f;
            p.U = N.Vector3.UnitX;
            p.HalfU = 0.5f * (hi.X - lo.X);
            p.HalfV = across.Length();
            p.V = across / p.HalfV;
            p.Normal = N.Vector3.Normalize(N.Vector3.Cross(p.U, p.V));
            if (p.Normal.Z * sign < 0f) p.Normal = -p.Normal;
        }

        /// <summary>Columns whose centre a face covers: count pass, then write pass.</summary>
        private static void Cut(Face t, bool write)
        {
            float x0 = MathF.Min(t.Ax, MathF.Min(t.Bx, t.Cx)), x1 = MathF.Max(t.Ax, MathF.Max(t.Bx, t.Cx));
            float z0 = MathF.Min(t.Az, MathF.Min(t.Bz, t.Cz)), z1 = MathF.Max(t.Az, MathF.Max(t.Bz, t.Cz));
            int i0 = Math.Max(0, (int)MathF.Ceiling((x0 - X0) / Cell - 0.5f)), i1 = Math.Min(Wide - 1, (int)MathF.Floor((x1 - X0) / Cell - 0.5f));
            int j0 = Math.Max(0, (int)MathF.Ceiling((z0 - Z0) / Cell - 0.5f)), j1 = Math.Min(Deep - 1, (int)MathF.Floor((z1 - Z0) / Cell - 0.5f));
            if (i1 < i0 || j1 < j0) return;
            float d = (t.Bz - t.Cz) * (t.Ax - t.Cx) + (t.Cx - t.Bx) * (t.Az - t.Cz);
            if (MathF.Abs(d) < 1e-12f) return;
            for (int i = i0; i <= i1; i++)
            {
                float x = X0 + (i + 0.5f) * Cell;
                for (int j = j0; j <= j1; j++)
                {
                    float z = Z0 + (j + 0.5f) * Cell;
                    float wa = ((t.Bz - t.Cz) * (x - t.Cx) + (t.Cx - t.Bx) * (z - t.Cz)) / d;
                    float wb = ((t.Cz - t.Az) * (x - t.Cx) + (t.Ax - t.Cx) * (z - t.Cz)) / d;
                    float wc = 1f - wa - wb;
                    if (wa < -1e-6f || wb < -1e-6f || wc < -1e-6f) continue;
                    int col = i * Deep + j;
                    if (!write) { cursor[col]++; continue; }
                    int k = crossStart[col] + cursor[col]++;
                    crossY[k] = wa * t.Ay + wb * t.By + wc * t.Cy;
                    crossUp[k] = t.Up;
                    crossMesh[k] = t.Mesh;
                }
            }
        }

        /// <summary>Sort upward; at equal height, tops first (stacked pieces).</summary>
        private static void Sort(int s, int e)
        {
            for (int i = s + 1; i < e; i++)
            {
                float y = crossY[i];
                bool up = crossUp[i];
                int mesh = crossMesh[i];
                int j = i - 1;
                while (j >= s && (crossY[j] > y || (crossY[j] == y && !crossUp[j] && up)))
                {
                    crossY[j + 1] = crossY[j];
                    crossUp[j + 1] = crossUp[j];
                    crossMesh[j + 1] = crossMesh[j];
                    j--;
                }
                crossY[j + 1] = y;
                crossUp[j + 1] = up;
                crossMesh[j + 1] = mesh;
            }
        }

        /// <summary>Crossings to spans by nesting depth (overlapping pieces are solid throughout).</summary>
        private static void Spans(int s, int e)
        {
            int first = spanCount, depth = 0;
            float from = 0f;
            for (int k = s; k < e; k++)
            {
                float y = crossY[k];
                bool up = crossUp[k];
                if (Seam(s, k)) continue;
                if (!up)
                {
                    if (depth++ == 0) from = y;
                }
                else if (depth > 0)
                {
                    if (--depth == 0) Emit(first, from, y, Set);
                }
                else if (spanCount == first) Emit(first, -Far, y, Set);                         // nothing under: floor
                else Emit(first, MathF.Max(spans[2 * (spanCount - 1) + 1], y - Shelf), y, Set); // top over a top: shelf
            }
            if (depth > 0) Emit(first, from, Far, Set); // nothing over: roof
        }

        /// <summary>Same piece, same facing, just below: a seam between its triangles, counted once.</summary>
        private static bool Seam(int s, int k)
        {
            for (int j = k - 1; j >= s && crossY[k] - crossY[j] < Same; j--)
                if (crossMesh[j] == crossMesh[k] && crossUp[j] == crossUp[k]) return true;
            return false;
        }

        private static void Emit(int first, float lo, float hi, short tag)
        {
            // touching the span below: merge
            if (spanCount > first && lo <= spans[2 * (spanCount - 1) + 1] + Same)
            {
                int last = spanCount - 1;
                spans[2 * last + 1] = MathF.Max(hi, spans[2 * last + 1]);
                if (tag < 0) return;
                // glass merged with its frame: tag only the glass part
                if (spanTag[last] < 0) { spanTag[last] = tag; spanGlass[2 * last] = lo; spanGlass[2 * last + 1] = hi; }
                else { spanGlass[2 * last] = MathF.Min(spanGlass[2 * last], lo); spanGlass[2 * last + 1] = MathF.Max(spanGlass[2 * last + 1], hi); }
                return;
            }
            if (spans.Length < 2 * (spanCount + 1))
            {
                Array.Resize(ref spans, Math.Max(2048, spans.Length * 2));
                Array.Resize(ref spanGlass, spans.Length);
                Array.Resize(ref spanTag, spans.Length / 2);
            }
            spans[2 * spanCount] = spanGlass[2 * spanCount] = lo;
            spans[2 * spanCount + 1] = spanGlass[2 * spanCount + 1] = hi;
            spanTag[spanCount] = tag;
            spanCount++;
        }

        // ---------------------------------------------------------------- walls and glass

        // convex solid as planes (k.p <= w)
        private static readonly N.Vector4[] planes = new N.Vector4[6];
        private static int planeCount;

        private static void Plane(N.Vector3 k, float w) => planes[planeCount++] = new N.Vector4(k, w);

        /// <summary>Triangle extruded Thick toward a side, into the columns. False for a sliver.</summary>
        private static bool Prism(N.Vector3 a, N.Vector3 b, N.Vector3 c, N.Vector3 toward, short tag)
        {
            var n = N.Vector3.Cross(b - a, c - a);
            float area = n.Length();
            if (area < 1e-8f) return false;
            n /= area;
            if (N.Vector3.Dot(n, toward) < 0f) n = -n;
            planeCount = 0;
            Plane(-n, -N.Vector3.Dot(n, a) + Overlap);
            Plane(n, N.Vector3.Dot(n, a) + Thick);
            Side(a, b, c, n);
            Side(b, c, a, n);
            Side(c, a, b, n);
            var d = n * Thick;
            float x0 = MathF.Min(MathF.Min(a.X, b.X), c.X) + MathF.Min(d.X, 0f), x1 = MathF.Max(MathF.Max(a.X, b.X), c.X) + MathF.Max(d.X, 0f);
            float z0 = MathF.Min(MathF.Min(a.Z, b.Z), c.Z) + MathF.Min(d.Z, 0f), z1 = MathF.Max(MathF.Max(a.Z, b.Z), c.Z) + MathF.Max(d.Z, 0f);
            Fill(x0 - Overlap, x1 + Overlap, z0 - Overlap, z1 + Overlap, tag);
            return true;
        }

        /// <summary>Prism side along edge p-q, facing away from r.</summary>
        private static void Side(N.Vector3 p, N.Vector3 q, N.Vector3 r, N.Vector3 n)
        {
            var s = N.Vector3.Cross(n, q - p);
            float len = s.Length();
            if (len < 1e-9f) return;
            s /= len;
            if (N.Vector3.Dot(s, r - p) > 0f) s = -s;
            Plane(s, N.Vector3.Dot(s, p) + Overlap);
        }

        /// <summary>Unread pane: its plane, Thick deep outward, a bit past its box.</summary>
        private static void Slab(Pane p, short tag)
        {
            planeCount = 0;
            float c = N.Vector3.Dot(p.Normal, p.Centre), u = N.Vector3.Dot(p.U, p.Centre), v = N.Vector3.Dot(p.V, p.Centre);
            Plane(-p.Normal, -c + Overlap);
            Plane(p.Normal, c + Thick);
            Plane(p.U, u + p.HalfU + PaneMargin);
            Plane(-p.U, -u + p.HalfU + PaneMargin);
            Plane(p.V, v + p.HalfV + PaneMargin);
            Plane(-p.V, -v + p.HalfV + PaneMargin);
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            for (int k = 0; k < 8; k++)
            {
                var q = p.Centre + p.U * ((k & 1) != 0 ? p.HalfU + PaneMargin : -p.HalfU - PaneMargin)
                                 + p.V * ((k & 2) != 0 ? p.HalfV + PaneMargin : -p.HalfV - PaneMargin) + p.Normal * ((k & 4) != 0 ? Thick : 0f);
                x0 = MathF.Min(x0, q.X); x1 = MathF.Max(x1, q.X);
                z0 = MathF.Min(z0, q.Z); z1 = MathF.Max(z1, q.Z);
            }
            Fill(x0, x1, z0, z1, tag);
        }

        /// <summary>Clips each column's centre line in the box against the planes, a wall span each.</summary>
        private static void Fill(float x0, float x1, float z0, float z1, short tag)
        {
            int i0 = Math.Max(0, (int)MathF.Ceiling((x0 - X0) / Cell - 0.5f)), i1 = Math.Min(Wide - 1, (int)MathF.Floor((x1 - X0) / Cell - 0.5f));
            int j0 = Math.Max(0, (int)MathF.Ceiling((z0 - Z0) / Cell - 0.5f)), j1 = Math.Min(Deep - 1, (int)MathF.Floor((z1 - Z0) / Cell - 0.5f));
            for (int i = i0; i <= i1; i++)
            {
                float x = X0 + (i + 0.5f) * Cell;
                for (int j = j0; j <= j1; j++)
                {
                    float z = Z0 + (j + 0.5f) * Cell, lo = Low - 1f, hi = High + 1f;
                    bool open = false;
                    for (int k = 0; k < planeCount && !open; k++)
                    {
                        var p = planes[k];
                        float rest = p.W - p.X * x - p.Z * z;
                        if (MathF.Abs(p.Y) < 1e-6f) open = rest < 0f;
                        else if (p.Y > 0f) hi = MathF.Min(hi, rest / p.Y);
                        else lo = MathF.Max(lo, rest / p.Y);
                        if (lo > hi) open = true;
                    }
                    if (!open) Wall(i * Deep + j, lo, hi, tag);
                }
            }
        }

        private static void Wall(int col, float lo, float hi, short tag)
        {
            if (wallCount == wallCol.Length)
            {
                int size = Math.Max(4096, wallCol.Length * 2);
                Array.Resize(ref wallCol, size);
                Array.Resize(ref wallLo, size);
                Array.Resize(ref wallHi, size);
                Array.Resize(ref wallTag, size);
            }
            wallCol[wallCount] = col;
            wallLo[wallCount] = lo;
            wallHi[wallCount] = hi;
            wallTag[wallCount] = tag;
            wallCount++;
        }

        private static void SortWalls(int columns)
        {
            Array.Clear(wallStart, 0, columns + 1);
            for (int k = 0; k < wallCount; k++) wallStart[wallCol[k] + 1]++;
            for (int c = 0; c < columns; c++) wallStart[c + 1] += wallStart[c];
            if (sortedLo.Length < wallCount)
            {
                sortedLo = new float[wallCol.Length];
                sortedHi = new float[wallCol.Length];
                sortedTag = new short[wallCol.Length];
            }
            Array.Copy(wallStart, cursor, columns);
            for (int k = 0; k < wallCount; k++)
            {
                int at = cursor[wallCol[k]]++;
                sortedLo[at] = wallLo[k];
                sortedHi[at] = wallHi[k];
                sortedTag[at] = wallTag[k];
            }
        }

        /// <summary>Merges a column's wall spans into its piece spans.</summary>
        private static void Join(int col, int first)
        {
            int ws = wallStart[col], we = wallStart[col + 1];
            if (we == ws) return;
            int n = spanCount - first, total = n + we - ws;
            if (mergeLo.Length < total)
            {
                mergeLo = new float[total * 2];
                mergeHi = new float[total * 2];
                mergeTag = new short[total * 2];
            }
            for (int k = 0; k < n; k++) { mergeLo[k] = spans[2 * (first + k)]; mergeHi[k] = spans[2 * (first + k) + 1]; mergeTag[k] = Set; } // piece spans, no glass yet
            for (int k = ws; k < we; k++, n++) { mergeLo[n] = sortedLo[k]; mergeHi[n] = sortedHi[k]; mergeTag[n] = sortedTag[k]; }
            for (int i = 1; i < n; i++)
            {
                float lo = mergeLo[i], hi = mergeHi[i];
                short tag = mergeTag[i];
                int j = i - 1;
                while (j >= 0 && mergeLo[j] > lo)
                {
                    mergeLo[j + 1] = mergeLo[j];
                    mergeHi[j + 1] = mergeHi[j];
                    mergeTag[j + 1] = mergeTag[j];
                    j--;
                }
                mergeLo[j + 1] = lo;
                mergeHi[j + 1] = hi;
                mergeTag[j + 1] = tag;
            }
            spanCount = first;
            for (int k = 0; k < n; k++) Emit(first, mergeLo[k], mergeHi[k], mergeTag[k]);
        }

        // ---------------------------------------------------------------- queries

        /// <summary>Column under a point, or -1 off the grid.</summary>
        public static int Column(float x, float z)
        {
            float fi = (x - X0) / Cell, fj = (z - Z0) / Cell;
            if (!(fi >= 0f && fi < Wide && fj >= 0f && fj < Deep)) return -1;
            return (int)fi * Deep + (int)fj;
        }

        public static bool InBox(N.Vector3 e) => Ready && e.Y > Low && e.Y < High && Column(e.X, e.Z) >= 0;

        /// <summary>Solid at this height? Returns the span and its tag (Set or pane index).</summary>
        public static bool Solid(int col, float y, out float lo, out float hi, out int tag)
        {
            lo = hi = 0f;
            tag = Set;
            if (col < 0 || !Ready) return false;
            for (int s = spanStart[col], e = spanStart[col + 1]; s < e; s++)
            {
                float a = spans[2 * s], b = spans[2 * s + 1];
                if (y < a) return false;
                if (y <= b)
                {
                    lo = a;
                    hi = b;
                    if (spanTag[s] >= 0 && y >= spanGlass[2 * s] - 0.01f && y <= spanGlass[2 * s + 1] + 0.01f) tag = spanTag[s];
                    return true;
                }
            }
            return false;
        }

        public static bool Solid(int col, float y) => Solid(col, y, out _, out _, out _);

        /// <summary>Top of the highest span starting at or below y, or NaN.</summary>
        public static float Ground(int col, float y)
        {
            float best = float.NaN;
            if (col < 0 || !Ready) return best;
            for (int s = spanStart[col], e = spanStart[col + 1]; s < e; s++)
            {
                if (spans[2 * s] > y) break;
                best = spans[2 * s + 1];
            }
            return best;
        }

        public static float EdgeX(int col, bool upper) => X0 + (col / Deep + (upper ? 1 : 0)) * Cell;
        public static float EdgeZ(int col, bool upper) => Z0 + (col % Deep + (upper ? 1 : 0)) * Cell;

        public static string PaneName(int tag) => tag >= 0 && tag < panes.Count ? $"{panes[tag].Where} ('{panes[tag].Name}')" : "the cockpit";

        /// <summary>Pane crossed between two points (within its outline, up to PastGlass), or -1.</summary>
        public static int Crossed(N.Vector3 from, N.Vector3 to)
        {
            if (!Ready) return -1;
            for (int i = 0; i < panes.Count; i++)
            {
                var p = panes[i];
                if (p.Dropped != null) continue;
                float d0 = N.Vector3.Dot(from - p.Centre, p.Normal), d1 = N.Vector3.Dot(to - p.Centre, p.Normal);
                if (d0 > Thick || d1 <= Thick || d1 > PastGlass) continue;
                var at = d0 >= 0f ? from : from + (to - from) * (-d0 / (d1 - d0));
                var a = at - p.Centre;
                if (MathF.Abs(N.Vector3.Dot(a, p.U)) <= p.HalfU && MathF.Abs(N.Vector3.Dot(a, p.V)) <= p.HalfV) return i;
            }
            return -1;
        }

        /// <summary>Way back in from inside a pane's slab. False in front of the glass (the frame, handled as faces).</summary>
        public static bool GlassExit(int tag, N.Vector3 e, out N.Vector3 normal, out float depth)
        {
            var p = panes[tag];
            normal = -p.Normal;
            depth = N.Vector3.Dot(e - p.Centre, p.Normal);
            return depth > 0f;
        }

        /// <summary>Point within the pane's outline and less than <paramref name="clear"/> in front of it (or behind).</summary>
        private static bool Behind(Pane p, N.Vector3 e, float clear, float margin = 0f)
        {
            var d = e - p.Centre;
            return N.Vector3.Dot(d, p.Normal) > -clear && MathF.Abs(N.Vector3.Dot(d, p.U)) <= p.HalfU + margin && MathF.Abs(N.Vector3.Dot(d, p.V)) <= p.HalfV + margin;
        }

        /// <summary>Pane by location ("left", "right", "ahead") for the dev command, or -1.</summary>
        public static int FindPane(string where)
        {
            for (int i = 0; i < panes.Count; i++)
                if (panes[i].Dropped == null && panes[i].Where.IndexOf(where, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return -1;
        }

        public static Pane GetPane(int i) => panes[i];

        /// <summary>Spans under a point, for the dev command.</summary>
        public static string At(float x, float z)
        {
            int col = Column(x, z);
            if (col < 0 || !Ready) return "off the cabin";
            var sb = new System.Text.StringBuilder();
            for (int s = spanStart[col], e = spanStart[col + 1]; s < e; s++)
            {
                if (sb.Length > 0) sb.Append(", ");
                float a = spans[2 * s], b = spans[2 * s + 1];
                sb.Append(a <= -Far + 1f ? "below" : a.ToString("0.00", CultureInfo.InvariantCulture));
                sb.Append("..");
                sb.Append(b >= Far - 1f ? "above" : b.ToString("0.00", CultureInfo.InvariantCulture));
                if (spanTag[s] >= 0) sb.Append(" (").Append(panes[spanTag[s]].Where).Append(')');
            }
            return sb.Length == 0 ? "open" : sb.ToString();
        }
    }
}
