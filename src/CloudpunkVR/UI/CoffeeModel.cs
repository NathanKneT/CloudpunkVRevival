using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Voxel meshes for the cup (after the game's coffee icon), holder, coffee and steam.
    /// Cup frame: origin at the foot's middle, +y up, meters. Coffee is voxel columns, so a tilt reads as terraces.
    /// </summary>
    internal static class CoffeeModel
    {
        /// <summary>Voxel edge, m (cup is 16 high, 15 across at the mouth).</summary>
        public const float Voxel = 0.005f;
        public const int Layers = 16;
        public const float Height = Layers * Voxel;           // rim top, from the foot
        public const float Floor = Voxel;                     // inside bottom, from the foot
        public const float Inside = Height - Floor;           // inner depth: 7.5 cm
        public const float FullLevel = 0.053f;                // full cup: 2.2 cm under the rim
        public const float GripHeight = 0.038f;               // palm centre, from the foot
        private const int Grid = 16;                          // voxels across

        // inner radius in voxels at floor and rim, wall thickness
        private const float InnerBottom = 4.7f, InnerTop = 6.1f, Wall = 1.3f;
        public const float Lip = 7.6f, FootRadius = 5.5f;             // voxels from the axis

        /// <summary>Inner radius at this height from the foot, m.</summary>
        public static float InnerRadius(float height)
        {
            float layer = Mathf.Clamp(height / Voxel, 1f, Layers);
            return (InnerBottom + (InnerTop - InnerBottom) * (layer - 1f) / (Layers - 2)) * Voxel;
        }

        private static float InnerLayer(int y) => InnerBottom + (InnerTop - InnerBottom) * (y - 1f) / (Layers - 2);
        private static float OuterLayer(int y) => y == 0 ? FootRadius : y == Layers - 1 ? Lip : InnerLayer(y) + Wall;

        /// <summary>Outer radius at this height, m (for the hand).</summary>
        public static float OuterRadius(float height) => InnerRadius(height) + Wall * Voxel;

        /// <summary>Radius gained per height (m/m).</summary>
        public const float Taper = (InnerTop - InnerBottom) / (Layers - 2);

        // ---------------------------------------------------------------- palette

        public const int PaletteSize = 16, Variants = 4;
        public const int Glaze = 0, Rib = 1, Rim = 2, InnerWall = 3, Coffee = 4, Crema = 5, Steam = 6, SteamDim = 7,
                         Holder = 8, HolderLip = 9, HolderShade = 10, Glyph = 11, Foot = 12;

        // Icon colours, greens lifted or the cup is a hole in the dark cockpit. Holder entries follow the console.
        private static readonly Color[] Colors =
        {
            new Color32(46, 92, 54, 255),    // Glaze
            new Color32(30, 64, 38, 255),    // Rib
            new Color32(96, 150, 92, 255),   // Rim
            new Color32(34, 58, 40, 255),    // InnerWall
            new Color32(70, 26, 14, 255),    // Coffee
            new Color32(92, 38, 18, 255),    // Crema
            new Color32(204, 182, 168, 255), // Steam
            new Color32(158, 136, 122, 255), // SteamDim
            new Color32(58, 64, 72, 255),    // Holder (until the console colour is read)
            new Color32(80, 88, 98, 255),    // HolderLip
            new Color32(32, 36, 42, 255),    // HolderShade
            new Color32(150, 200, 128, 255), // Glyph
            new Color32(22, 40, 26, 255),    // Foot
            new Color32(0, 0, 0, 255),
            new Color32(0, 0, 0, 255),
            new Color32(0, 0, 0, 255),
        };

        // emission per entry, so the cup reads in the dark cockpit
        private static readonly float[] Glow = { 0.22f, 0.16f, 0.26f, 0.12f, 0.10f, 0.14f, 0.45f, 0.35f, 0.05f, 0.06f, 0.03f, 0.40f, 0.10f, 0f, 0f, 0f };
        private static readonly float[] Shades = { 0.92f, 0.98f, 1.02f, 1.08f };

        public static Color Texel(int i, int v, bool glow)
        {
            var c = Colors[i] * Shades[v];
            if (glow) c *= Glow[i];
            return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
        }

        public static Vector2 Uv(int entry, int variant) => new Vector2((entry + 0.5f) / PaletteSize, (variant + 0.5f) / Variants);

        /// <summary>Holder in the console's colour, lighter lip, shaded inside.</summary>
        public static void SetHolderColor(Color console)
        {
            Colors[Holder] = new Color(console.r, console.g, console.b, 1f);
            Colors[HolderLip] = new Color(Mathf.Clamp01(console.r * 1.3f + 0.03f), Mathf.Clamp01(console.g * 1.3f + 0.03f), Mathf.Clamp01(console.b * 1.3f + 0.03f), 1f);
            Colors[HolderShade] = new Color(console.r * 0.55f, console.g * 0.55f, console.b * 0.55f, 1f);
        }

        // ---------------------------------------------------------------- the cup

        private static float Centre(int i) => i - (Grid - 1) * 0.5f; // cell centre from the axis, voxels

        private static bool CupSolid(int x, int y, int z)
        {
            if (x < 0 || z < 0 || x >= Grid || z >= Grid || y < 0 || y >= Layers) return false;
            float d = MathF.Sqrt(Centre(x) * Centre(x) + Centre(z) * Centre(z));
            if (y == 0) return d <= FootRadius;
            return d <= OuterLayer(y) && d > InnerLayer(y);
        }

        private static bool InCup(int x, int y, int z)
        {
            if (y < 1) return false;
            float cx = Centre(x), cz = Centre(z);
            return MathF.Sqrt(cx * cx + cz * cz) <= InnerLayer(Math.Min(y, Layers - 1));
        }

        /// <summary>The cup: ribbed body, glyph band, collar, light rim with a lip.</summary>
        public static Mesh BuildCup()
        {
            var b = new Builder();
            int seed = 41;
            for (int y = 0; y < Layers; y++)
            for (int x = 0; x < Grid; x++)
            for (int z = 0; z < Grid; z++)
            {
                if (!CupSolid(x, y, z)) continue;
                seed = seed * 1103515245 + 12345;
                int variant = (seed >> 16) & (Variants - 1);
                float cx = Centre(x), cz = Centre(z);
                int sector = (int)MathF.Floor((MathF.Atan2(cz, cx) + MathF.PI) / (2f * MathF.PI) * 16f) & 15;
                var min = new Vector3((cx - 0.5f) * Voxel, y * Voxel, (cz - 0.5f) * Voxel);
                var max = min + new Vector3(Voxel, Voxel, Voxel);
                for (int f = 0; f < 6; f++)
                {
                    int nx = x + (f == 0 ? 1 : f == 1 ? -1 : 0), ny = y + (f == 2 ? 1 : f == 3 ? -1 : 0), nz = z + (f == 4 ? 1 : f == 5 ? -1 : 0);
                    if (CupSolid(nx, ny, nz)) continue;
                    int entry;
                    if (f == 3) entry = Foot;                                   // undersides
                    else if (InCup(nx, ny, nz)) entry = InnerWall;               // inside, floor too
                    else if (f == 2 || y == Layers - 1) entry = Rim;
                    else if (y == 0) entry = Foot;
                    else if (y == 12 || y == 13) entry = (sector % 4 == 0 && y == 13) || (sector % 4 == 2 && y == 12) ? Glyph : Rib;
                    else if (y == 14) entry = Glaze;                            // collar
                    else entry = sector % 2 == 0 ? Glaze : Rib;                 // ribs
                    b.Face(min, max, f, Uv(entry, variant));
                }
            }
            return b.ToMesh("CloudpunkVR_CoffeeCup");
        }

        // ---------------------------------------------------------------- the holder

        /// <summary>Holder height above the console (2 cm).</summary>
        public const float HolderHeight = 4 * Voxel;
        public const float HolderInner = 6.5f, HolderOuter = 7.8f;  // voxels: clears the cup's bottom 4 layers (6.3)

        /// <summary>Open ring; the cup stands on the console inside it.</summary>
        public static Mesh BuildHolder()
        {
            const int n = 18;
            var b = new Builder();
            int seed = 17;
            bool Solid(int x, int y, int z)
            {
                if (x < 0 || z < 0 || x >= n || z >= n || y < 0 || y >= 4) return false;
                float cx = x - (n - 1) * 0.5f, cz = z - (n - 1) * 0.5f, d = MathF.Sqrt(cx * cx + cz * cz);
                return d > HolderInner && d <= HolderOuter;
            }
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < n; x++)
            for (int z = 0; z < n; z++)
            {
                if (!Solid(x, y, z)) continue;
                seed = seed * 1103515245 + 12345;
                int variant = (seed >> 16) & (Variants - 1);
                float cx = x - (n - 1) * 0.5f, cz = z - (n - 1) * 0.5f;
                var min = new Vector3((cx - 0.5f) * Voxel, y * Voxel, (cz - 0.5f) * Voxel);
                var max = min + new Vector3(Voxel, Voxel, Voxel);
                float d = MathF.Sqrt(cx * cx + cz * cz);
                for (int f = 0; f < 6; f++)
                {
                    int nx = x + (f == 0 ? 1 : f == 1 ? -1 : 0), ny = y + (f == 2 ? 1 : f == 3 ? -1 : 0), nz = z + (f == 4 ? 1 : f == 5 ? -1 : 0);
                    if (Solid(nx, ny, nz)) continue;
                    float ncx = nx - (n - 1) * 0.5f, ncz = nz - (n - 1) * 0.5f;
                    bool inward = MathF.Sqrt(ncx * ncx + ncz * ncz) < d;
                    int entry = f == 2 ? HolderLip : f == 3 || inward ? HolderShade : Holder;
                    b.Face(min, max, f, Uv(entry, variant));
                }
            }
            return b.ToMesh("CloudpunkVR_CoffeeHolder");
        }

        // ---------------------------------------------------------------- the coffee

        /// <summary>
        /// Coffee columns, one per cell, each starting where the cell is first inside the cup (none cuts the wall).
        /// Built once; only the tops move.
        /// </summary>
        public sealed class Surface
        {
            public int Columns;
            public float[] X, Z, Bottom;                // column centre (cup frame) and base height
            public float[] VertexX, VertexZ;            // per vertex
            public int[] VertexColumn;                  // column index, or -c-1 for a bottom vertex
            public Mesh Mesh;

            /// <summary>Column top, or NaN under 1 mm (would show as a dot on the wall step).</summary>
            public float Top(int c, float surface)
            {
                float top = Floor + surface;
                return top >= Bottom[c] + MinColumn ? top : float.NaN;
            }
        }

        private const float MinColumn = 0.001f;

        public static Surface BuildSurface()
        {
            var s = new Surface();
            var xs = new List<float>(); var zs = new List<float>(); var bottoms = new List<float>(); var entries = new List<int>();
            for (int x = 0; x < Grid; x++)
            for (int z = 0; z < Grid; z++)
            {
                float cx = Centre(x), cz = Centre(z), d = MathF.Sqrt(cx * cx + cz * cz);
                int first = -1;
                for (int y = 1; y < Layers; y++) if (d <= InnerLayer(y)) { first = y; break; }
                if (first < 0) continue;
                xs.Add(cx * Voxel); zs.Add(cz * Voxel); bottoms.Add(first * Voxel);
                // lighter ring at the wall (crema)
                entries.Add(d > InnerLayer(12) - 1.1f ? Crema : Coffee);
            }
            s.Columns = xs.Count;
            s.X = xs.ToArray(); s.Z = zs.ToArray(); s.Bottom = bottoms.ToArray();
            int verts = s.Columns * 20;
            var vx = new float[verts]; var vz = new float[verts]; var col = new int[verts];
            var normals = new Vector3[verts]; var uvs = new Vector2[verts]; var tris = new int[s.Columns * 30];
            var positions = new Vector3[verts];
            int seed = 5;
            for (int c = 0; c < s.Columns; c++)
            {
                seed = seed * 1103515245 + 12345;
                int variant = 1 + ((seed >> 16) % 3 + 3) % 3;
                float x0 = s.X[c] - Voxel * 0.5f, x1 = s.X[c] + Voxel * 0.5f;
                float z0 = s.Z[c] - Voxel * 0.5f, z1 = s.Z[c] + Voxel * 0.5f;
                // top and four sides, Builder.Face order; lower side corners stay at the bottom
                int[] faces = { 2, 0, 1, 4, 5 };
                for (int k = 0; k < faces.Length; k++)
                {
                    int f = faces[k], v = c * 20 + k * 4, t = c * 30 + k * 6;
                    Corners(f, x0, x1, z0, z1, positions, v);
                    for (int i = 0; i < 4; i++)
                    {
                        vx[v + i] = positions[v + i].x;
                        vz[v + i] = positions[v + i].z;
                        col[v + i] = positions[v + i].y > 0f ? c : -c - 1;
                        positions[v + i] = new Vector3(s.X[c], s.Bottom[c] - 0.5f * Voxel, s.Z[c]); // folded away until drawn
                        normals[v + i] = FaceNormals[f];
                        uvs[v + i] = Uv(entries[c], f == 2 ? variant : 0); // darker sides so terraces read
                    }
                    tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2; tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
                }
            }
            s.VertexX = vx; s.VertexZ = vz; s.VertexColumn = col;
            var mesh = new Mesh { name = "CloudpunkVR_Coffee" };
            mesh.MarkDynamic();
            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(new Vector3(0f, Height * 0.5f, 0f), new Vector3(0.1f, Height + 0.02f, 0.1f));
            s.Mesh = mesh;
            return s;
        }

        /// <summary>Column face corners (Builder.Face order), y = 1 marks the top ones.</summary>
        private static void Corners(int f, float x0, float x1, float z0, float z1, Vector3[] p, int v)
        {
            switch (f)
            {
                case 0: p[v] = new Vector3(x1, 0, z0); p[v + 1] = new Vector3(x1, 1, z0); p[v + 2] = new Vector3(x1, 1, z1); p[v + 3] = new Vector3(x1, 0, z1); break;
                case 1: p[v] = new Vector3(x0, 0, z1); p[v + 1] = new Vector3(x0, 1, z1); p[v + 2] = new Vector3(x0, 1, z0); p[v + 3] = new Vector3(x0, 0, z0); break;
                case 2: p[v] = new Vector3(x0, 1, z0); p[v + 1] = new Vector3(x0, 1, z1); p[v + 2] = new Vector3(x1, 1, z1); p[v + 3] = new Vector3(x1, 1, z0); break;
                case 4: p[v] = new Vector3(x1, 0, z1); p[v + 1] = new Vector3(x1, 1, z1); p[v + 2] = new Vector3(x0, 1, z1); p[v + 3] = new Vector3(x0, 0, z1); break;
                default: p[v] = new Vector3(x0, 0, z0); p[v + 1] = new Vector3(x0, 1, z0); p[v + 2] = new Vector3(x1, 1, z0); p[v + 3] = new Vector3(x1, 0, z0); break;
            }
        }

        // ---------------------------------------------------------------- steam and drops

        /// <summary>Small moving boxes (steam, drops) in one mesh. CockpitCoffee writes the corners.</summary>
        public static Mesh BuildBits(int boxes, Func<int, Vector2> uvOf)
        {
            int verts = boxes * 24;
            var uvs = new Vector2[verts];
            var normals = new Vector3[verts];
            var tris = new int[boxes * 36];
            for (int b = 0; b < boxes; b++)
            {
                var uv = uvOf(b);
                for (int f = 0; f < 6; f++)
                {
                    int v = b * 24 + f * 4, t = b * 36 + f * 6;
                    for (int k = 0; k < 4; k++) { uvs[v + k] = uv; normals[v + k] = FaceNormals[f]; }
                    tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2; tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
                }
            }
            var mesh = new Mesh { name = "CloudpunkVR_CoffeeBits" };
            mesh.MarkDynamic();
            mesh.vertices = new Vector3[verts];
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f)); // boxes move around, keep it big
            return mesh;
        }

        // ---------------------------------------------------------------- mesh building

        public static readonly Vector3[] FaceNormals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        public static int[][] BoxCorners => CharmModel.FaceCorners;

        /// <summary>Collects axis-aligned faces, same winding as the hands and the charm.</summary>
        private sealed class Builder
        {
            private readonly List<Vector3> verts = new List<Vector3>(), normals = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int> tris = new List<int>();

            /// <summary>Face f of a box: 0 +x, 1 -x, 2 +y, 3 -y, 4 +z, 5 -z.</summary>
            public void Face(Vector3 min, Vector3 max, int f, Vector2 uv)
            {
                Vector3 a, b, c, d;
                switch (f)
                {
                    case 0: a = new Vector3(max.x, min.y, min.z); b = new Vector3(max.x, max.y, min.z); c = new Vector3(max.x, max.y, max.z); d = new Vector3(max.x, min.y, max.z); break;
                    case 1: a = new Vector3(min.x, min.y, max.z); b = new Vector3(min.x, max.y, max.z); c = new Vector3(min.x, max.y, min.z); d = new Vector3(min.x, min.y, min.z); break;
                    case 2: a = new Vector3(min.x, max.y, min.z); b = new Vector3(min.x, max.y, max.z); c = new Vector3(max.x, max.y, max.z); d = new Vector3(max.x, max.y, min.z); break;
                    case 3: a = new Vector3(min.x, min.y, max.z); b = new Vector3(min.x, min.y, min.z); c = new Vector3(max.x, min.y, min.z); d = new Vector3(max.x, min.y, max.z); break;
                    case 4: a = new Vector3(max.x, min.y, max.z); b = new Vector3(max.x, max.y, max.z); c = new Vector3(min.x, max.y, max.z); d = new Vector3(min.x, min.y, max.z); break;
                    default: a = new Vector3(min.x, min.y, min.z); b = new Vector3(min.x, max.y, min.z); c = new Vector3(max.x, max.y, min.z); d = new Vector3(max.x, min.y, min.z); break;
                }
                int i = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                for (int k = 0; k < 4; k++) { normals.Add(FaceNormals[f]); uvs.Add(uv); }
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                mesh.vertices = verts.ToArray();
                mesh.normals = normals.ToArray();
                mesh.uv = uvs.ToArray();
                mesh.triangles = tris.ToArray();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
