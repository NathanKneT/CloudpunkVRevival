using System.Collections.Generic;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Menu sign letters cut from the game's own neon L, P, K and U textures (2048 px, y down). Pieces back to front.
    /// V is the U pinched, R is P plus K's leg, E is L plus two feet, I is two stems, A is an upside-down V.
    /// </summary>
    internal static class SignLetters
    {
        public enum Source { L, P, K, U }

        public readonly struct Box
        {
            public readonly float X0, Y0, X1, Y1;
            public Box(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        }

        /// <summary>x' = centre + (x - centre) * (1 - Amount * t^Power), t 0 at the free end, 1 at the pinched one.</summary>
        public sealed class Pinch
        {
            public float Centre, Amount, Power, Top, Bottom;
            public bool AtTop;
        }

        public sealed class Piece
        {
            public Source Source;
            public Box Src, Dst;
            public bool FlipY;
            public Pinch Pinch;
            public Color Tint = Color.white;
            public string Note;
        }

        public sealed class Glyph
        {
            public char Letter;
            public float Left, Right; // glyph extent in px, for spacing
            public Piece[] Pieces;
        }

        private const float Px = 2048f;
        private const float LayerStep = 0.08f; // each piece this much nearer the camera

        private static readonly Box Full = new Box(0, 0, 2048, 2048);
        private static readonly Box URect = new Box(598, 134, 1500, 1968); // the U glyph plus a small margin

        private static Pinch PinchFoot(bool atTop) =>
            new Pinch { Centre = 1049, Amount = 0.58f, Power = 1.35f, Top = 134, Bottom = 1968, AtTop = atTop };

        private static Piece Whole(Source s, string note = null) => new Piece { Source = s, Src = Full, Dst = Full, Note = note };

        private static Piece Cut(Source s, Box src, Box dst, string note) => new Piece { Source = s, Src = src, Dst = dst, Note = note };

        public static Glyph For(char c)
        {
            switch (c)
            {
                case 'V':
                    // whole texture, so the V can share the U quad's Flicker material
                    return new Glyph { Letter = c, Left = 606, Right = 1490, Pieces = new[] {
                        new Piece { Source = Source.U, Src = Full, Dst = Full, Pinch = PinchFoot(false), Note = "pinched toward its foot" } } };
                case 'R':
                    return new Glyph { Letter = c, Left = 692, Right = 1584, Pieces = new[] {
                        Cut(Source.K, new Box(962, 1180, 1600, 1990), new Box(962, 1105, 1600, 1915), "its leg"),
                        Whole(Source.P) } };
                case 'E':
                    return new Glyph { Letter = c, Left = 750, Right = 1528, Pieces = new[] {
                        Cut(Source.L, new Box(1100, 1545, 1535, 1935), new Box(1100, 113, 1535, 503), "its foot as the top bar"),
                        Cut(Source.L, new Box(1200, 1545, 1535, 1935), new Box(1100, 815, 1435, 1205), "its foot, shorter, as the middle bar"),
                        Whole(Source.L) } };
                case 'I':
                    return new Glyph { Letter = c, Left = 750, Right = 1106, Pieces = new[] {
                        Cut(Source.L, new Box(740, 80, 1112, 1420), new Box(740, 80, 1112, 1420), "top of its stem"),
                        new Piece { Source = Source.P, Src = new Box(682, 1330, 1054, 1910), Dst = new Box(740, 1330, 1112, 1910),
                                    Tint = new Color(0.88f, 1.02f, 1.03f, 1f), Note = "foot of its stem" } } };
                case 'A':
                    return new Glyph { Letter = c, Left = 606, Right = 1490, Pieces = new[] {
                        Cut(Source.L, new Box(1150, 1530, 1440, 1935), new Box(900, 1000, 1190, 1405), "a piece of its foot as the bar"),
                        new Piece { Source = Source.U, Src = URect, Dst = URect, FlipY = true, Pinch = PinchFoot(true), Note = "upside down, pinched toward its top" } } };
                case 'L':
                    return new Glyph { Letter = c, Left = 750, Right = 1528, Pieces = new[] { Whole(Source.L) } };
                default:
                    return null;
            }
        }

        /// <summary>Square mesh facing -z like the game's quads, one submesh per (source, tint) in materials order.</summary>
        public static Mesh Build(Glyph g, float size, List<(Source Source, Color Tint)> materials)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<List<int>>();
            materials.Clear();
            for (int p = 0; p < g.Pieces.Length; p++)
            {
                var piece = g.Pieces[p];
                var source = piece.Source;
                int sub = -1;
                for (int m = 0; m < materials.Count && sub < 0; m++)
                {
                    var t = materials[m].Tint; // field by field: no Il2Cpp equality on Unity structs
                    if (materials[m].Source == source && t.r == piece.Tint.r && t.g == piece.Tint.g && t.b == piece.Tint.b) sub = m;
                }
                if (sub < 0) { sub = materials.Count; materials.Add((source, piece.Tint)); tris.Add(new List<int>()); }
                AddPiece(piece, size, -p * LayerStep, verts, uvs, tris[sub]);
            }
            var normals = new Vector3[verts.Count];
            var tangents = new Vector4[verts.Count];
            for (int i = 0; i < normals.Length; i++) { normals[i] = new Vector3(0f, 0f, -1f); tangents[i] = new Vector4(1f, 0f, 0f, 1f); }
            var mesh = new Mesh { name = "CloudpunkVR_Sign" + g.Letter, hideFlags = HideFlags.DontUnloadUnusedAsset };
            mesh.vertices = verts.ToArray();
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uvs.ToArray();
            mesh.subMeshCount = tris.Count;
            for (int i = 0; i < tris.Count; i++) mesh.SetTriangles(tris[i].ToArray(), i);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>One quad, or a grid when pinched so it bends without seams.</summary>
        private static void AddPiece(Piece piece, float size, float z, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            int cols = piece.Pinch != null ? 4 : 1, rows = piece.Pinch != null ? 32 : 1;
            int first = verts.Count;
            var s = piece.Src; var d = piece.Dst;
            for (int j = 0; j <= rows; j++)
            {
                float fy = (float)j / rows;
                float sy = piece.FlipY ? s.Y1 - fy * (s.Y1 - s.Y0) : s.Y0 + fy * (s.Y1 - s.Y0);
                float dy = d.Y0 + fy * (d.Y1 - d.Y0);
                float scale = 1f;
                if (piece.Pinch != null)
                {
                    var k = piece.Pinch;
                    float t = Mathf.Clamp01(k.AtTop ? (k.Bottom - dy) / (k.Bottom - k.Top) : (dy - k.Top) / (k.Bottom - k.Top));
                    scale = 1f - k.Amount * Mathf.Pow(t, k.Power);
                }
                for (int i = 0; i <= cols; i++)
                {
                    float fx = (float)i / cols;
                    float sx = s.X0 + fx * (s.X1 - s.X0);
                    float dx = d.X0 + fx * (d.X1 - d.X0);
                    if (piece.Pinch != null) dx = piece.Pinch.Centre + (dx - piece.Pinch.Centre) * scale;
                    verts.Add(new Vector3((dx / Px - 0.5f) * size, (0.5f - dy / Px) * size, z));
                    uvs.Add(new Vector2(sx / Px, 1f - sy / Px));
                }
            }
            // clockwise seen from -z
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    int tl = first + j * (cols + 1) + i, tr = tl + 1, bl = tl + cols + 1, br = bl + 1;
                    tris.Add(bl); tris.Add(tl); tris.Add(tr);
                    tris.Add(bl); tris.Add(tr); tris.Add(br);
                }
        }

        /// <summary>UV rect for the log.</summary>
        public static string Uv(Piece p) =>
            $"u {p.Src.X0 / Px:0.00}-{p.Src.X1 / Px:0.00} v {1f - p.Src.Y1 / Px:0.00}-{1f - p.Src.Y0 / Px:0.00}" + (p.FlipY ? " flipped" : "");
    }
}
