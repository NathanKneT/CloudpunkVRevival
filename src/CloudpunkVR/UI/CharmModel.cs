using System;
using System.Collections.Generic;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The charm's mesh: Camus as a voxel head with a ring on top, on a bead chain from a small plate.
    /// A voxel is solid where all three drawings have paint, each face colored by the drawing it faces.
    /// </summary>
    internal static class CharmModel
    {
        /// <summary>Voxel edge, m (12 across = 6 cm head).</summary>
        public const float Voxel = 0.005f;

        /// <summary>Nose-down tilt at rest, degrees, so the seat sees the face.</summary>
        private const float NoseDown = 15f;
        public const int PaletteSize = 8, Variants = 4;
        public const int Fur = 0, White = 1, Tan = 2, Eye = 3, Nose = 4, Tongue = 5, Steel = 6, Gunmetal = 7;

        // Front view, rows top down, x left to right. White cheeks read as a mask, a centred tongue as an open mouth.
        private static readonly string[] Front =
        {
            "...KKWWKK...",
            ".KKKKWWKKKK.",
            "KKKKKWWKKKKK",
            "KKKTKWWKTKKK",
            "K.KEKWWKEK.K",
            "..KKWNNWKK..",
            "..KTWNNWTK..",
            "..KTWWWWTK..",
            "...KWWWWK...",
            "....WPWW....",
        };

        // Side view, rows top down, z from nose to back of head.
        private static readonly string[] Side =
        {
            "....KKKK..",
            "...KKKKKKK",
            "...KKKKKKK",
            "...KKKKKKK",
            "...KKKKKKK",
            "NWWKKKKKKK",
            "NWWTKKKKK.",
            "WWWTKKKKK.",
            ".WWKKKKK..",
            ".WWKKK....",
        };

        // Top view, rows from back of head to nose, x as in Front.
        private static readonly string[] Top =
        {
            "...KKKKKK...",
            "..KKKKKKKK..",
            "..KKKKKKKK..",
            "..KKKKKKKK..",
            "KKKKKKKKKKKK",
            "KKKKKWWKKKKK",
            "..KKKWWKKK..",
            "....WWWW....",
            "....WWWW....",
            ".....NN.....",
        };

        // from Camus's portrait; charcoal, not black, or the coat vanishes against the ceiling
        private static readonly Color[] Colors =
        {
            new Color32(65, 58, 62, 255),    // Fur
            new Color32(200, 195, 182, 255), // White
            new Color32(170, 108, 60, 255),  // Tan
            new Color32(70, 190, 225, 255),  // Eye
            new Color32(22, 20, 22, 255),    // Nose
            new Color32(214, 104, 118, 255), // Tongue
            new Color32(176, 178, 184, 255), // Steel
            new Color32(70, 74, 82, 255),    // Gunmetal
        };

        // Emission per entry so it reads in the dark cockpit. Coat glows more or it looks like a hole.
        private static readonly float[] Glow = { 0.28f, 0.04f, 0.16f, 0.95f, 0.10f, 0.14f, 0.10f, 0.12f };

        private static readonly float[] Shades = { 0.93f, 0.98f, 1.02f, 1.07f };

        /// <summary>Head mesh and physics points, relative to its centre of mass.</summary>
        public struct Head
        {
            public Mesh Mesh;
            public Vector3 Ring;              // chain attach point (top bar of the ring)
            public Vector3 BoxCenter, BoxHalf; // head as a box, for hand contact
        }

        /// <summary>One color of the palette (variant v of entry i), or its glow.</summary>
        public static Color Texel(int i, int v, bool glow)
        {
            var c = Colors[i] * Shades[v];
            if (glow) c *= Glow[i];
            return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
        }

        public static Vector2 Uv(int entry, int variant) => new Vector2((entry + 0.5f) / PaletteSize, (variant + 0.5f) / Variants);

        // ---------------------------------------------------------------- the head

        private static int Entry(char c) => c switch
        {
            'W' => White,
            'T' => Tan,
            'E' => Eye,
            'N' => Nose,
            'P' => Tongue,
            _ => Fur,
        };

        /// <summary>Head and ring as one mesh, face toward +z. The drawings are turned 180 degrees about y.</summary>
        public static Head BuildHead()
        {
            int nx = Front[0].Length, ny = Front.Length, nz = Side[0].Length;
            char F(int x, int y) => Front[ny - 1 - y][x];
            char S(int z, int y) => Side[ny - 1 - y][z];
            char T(int x, int z) => Top[nz - 1 - z][x];

            // solid voxels in mesh axes (x and z flipped from the drawings)
            var solid = new bool[nx, ny, nz];
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++)
            {
                if (F(x, y) == '.' || S(z, y) == '.' || T(x, z) == '.') continue;
                solid[nx - 1 - x, y, nz - 1 - z] = true;
                sum += new Vector3(nx - 1 - x + 0.5f, y + 0.5f, nz - 1 - z + 0.5f);
                count++;
            }
            Vector3 centre = sum / count;
            bool On(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < nx && y < ny && z < nz && solid[x, y, z];

            var b = new Builder();
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            var cube = new Vector3(Voxel, Voxel, Voxel);
            int seed = 29;
            for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++)
            {
                if (!solid[x, y, z]) continue;
                seed = seed * 1103515245 + 12345;
                int variant = (seed >> 16) & (Variants - 1);
                Vector3 min = (new Vector3(x, y, z) - centre) * Voxel;
                Vector3 max = min + cube;
                lo = new Vector3(Math.Min(lo.x, min.x), Math.Min(lo.y, min.y), Math.Min(lo.z, min.z));
                hi = new Vector3(Math.Max(hi.x, max.x), Math.Max(hi.y, max.y), Math.Max(hi.z, max.z));
                int ax = nx - 1 - x, az = nz - 1 - z; // back in the drawings' axes
                for (int f = 0; f < 6; f++)
                {
                    bool open = f switch
                    {
                        0 => !On(x + 1, y, z), 1 => !On(x - 1, y, z), 2 => !On(x, y + 1, z),
                        3 => !On(x, y - 1, z), 4 => !On(x, y, z + 1), _ => !On(x, y, z - 1),
                    };
                    if (!open) continue;
                    // underside is fur: the seat sees it most
                    char c = f == 4 ? F(ax, y) : f == 2 ? T(ax, az) : f == 3 ? 'K' : S(az, y);
                    b.Face(min, max, f, Uv(Entry(c), variant));
                }
            }

            // Ring: bail plus a 6 mm steel loop. Placed back on the top so the hanging head tips NoseDown.
            const float mm = 0.001f, ringHeight = 6.85f * mm;
            int top = 0;
            for (int x = 0; x < nx; x++) for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) if (solid[x, y, z]) top = Math.Max(top, y);
            float midX = 0f, back = float.MaxValue, front = float.MinValue;
            int n = 0;
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                    if (solid[x, top, z]) { midX += x + 0.5f; back = Math.Min(back, z + 0.5f); front = Math.Max(front, z + 0.5f); n++; }
            midX /= n;
            float rise = (top + 1 - centre.y) + ringHeight / Voxel;
            float ringZ = Math.Clamp(centre.z - MathF.Tan(NoseDown * MathF.PI / 180f) * rise, back, front);
            Vector3 baseOf = (new Vector3(midX, top + 1, ringZ) - centre) * Voxel;
            b.Box(baseOf + new Vector3(-1.6f, 0f, -1.6f) * mm, baseOf + new Vector3(1.6f, 1.6f, 1.6f) * mm, Gunmetal, 1);
            b.Box(baseOf + new Vector3(-3f, 1.6f, -0.8f) * mm, baseOf + new Vector3(-1.5f, 7.6f, 0.8f) * mm, Steel, 2);
            b.Box(baseOf + new Vector3(1.5f, 1.6f, -0.8f) * mm, baseOf + new Vector3(3f, 7.6f, 0.8f) * mm, Steel, 1);
            b.Box(baseOf + new Vector3(-1.5f, 6.1f, -0.8f) * mm, baseOf + new Vector3(1.5f, 7.6f, 0.8f) * mm, Steel, 3);
            b.Box(baseOf + new Vector3(-1.5f, 1.6f, -0.8f) * mm, baseOf + new Vector3(1.5f, 3.1f, 0.8f) * mm, Steel, 0);

            return new Head
            {
                Mesh = b.ToMesh("CloudpunkVR_CharmHead"),
                Ring = baseOf + new Vector3(0f, ringHeight, 0f),
                BoxCenter = (lo + hi) * 0.5f,
                BoxHalf = (hi - lo) * 0.5f,
            };
        }

        // ---------------------------------------------------------------- the plate it hangs from

        /// <summary>Mount height above the chain's pivot, m.</summary>
        public const float MountHeight = 0.0045f;

        /// <summary>1 cm gunmetal plate with a steel boss the chain hangs from.</summary>
        public static Mesh BuildMount()
        {
            const float mm = 0.001f;
            var b = new Builder();
            b.Box(new Vector3(-5f, 2f, -5f) * mm, new Vector3(5f, 4.5f, 5f) * mm, Gunmetal, 2);
            b.Box(new Vector3(-1.6f, 0f, -1.6f) * mm, new Vector3(1.6f, 2f, 1.6f) * mm, Steel, 1);
            return b.ToMesh("CloudpunkVR_CharmMount");
        }

        // ---------------------------------------------------------------- mesh building

        private static readonly Vector3[] FaceNormals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        /// <summary>Collects axis-aligned faces, same winding as the hands.</summary>
        internal sealed class Builder
        {
            private readonly List<Vector3> verts = new List<Vector3>(), normals = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int> tris = new List<int>();

            public void Box(Vector3 min, Vector3 max, int entry, int variant)
            {
                for (int f = 0; f < 6; f++) Face(min, max, f, Uv(entry, variant));
            }

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

        /// <summary>
        /// Box corners per face in Builder.Face order, to move beads per frame without rebuilding.
        /// Corner bits: x 1, y 2, z 4, set on the + side.
        /// </summary>
        public static readonly int[][] FaceCorners =
        {
            new[] { 1, 3, 7, 5 }, // +x: (max,min,min) (max,max,min) (max,max,max) (max,min,max)
            new[] { 4, 6, 2, 0 }, // -x
            new[] { 2, 6, 7, 3 }, // +y
            new[] { 4, 0, 1, 5 }, // -y
            new[] { 5, 7, 6, 4 }, // +z
            new[] { 0, 2, 3, 1 }, // -z
        };
    }
}
