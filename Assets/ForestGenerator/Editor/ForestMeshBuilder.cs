using System.Collections.Generic;
using UnityEngine;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// A tiny mesh-building kit for the placeholder models: vertex-coloured, low-poly, built from tubes (trunks, branches,
    /// logs), jagged cone tiers (conifer foliage), lumpy blobs (leaf clumps, bushes, rocks) and thin strips (grass, fronds).
    /// Vertex colour alpha is the wind weight read by the Havoc/Forest Vegetation shader (0 = rigid).
    /// Faces are wound so that cross(b - a, c - a) points out of the surface (Unity's front face).
    /// </summary>
    public sealed class ForestMeshBuilder
    {
        public readonly List<Vector3> pos = new List<Vector3>();
        public readonly List<Color> col = new List<Color>();
        public readonly List<int> tri = new List<int>();

        public int V(Vector3 p, Color c)
        {
            pos.Add(p);
            col.Add(c);
            return pos.Count - 1;
        }

        public void T(int a, int b, int c)
        {
            tri.Add(a);
            tri.Add(b);
            tri.Add(c);
        }

        /// <summary>A flat-shaded triangle (its own three vertices).</summary>
        public void FlatT(Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
        {
            int i = V(a, ca);
            V(b, cb);
            V(c, cc);
            T(i, i + 1, i + 2);
        }

        /// <summary>Copies the triangles added since <paramref name="fromTri"/> with the other side facing out (for thin leaves and blades).</summary>
        public void BackFaces(int fromTri)
        {
            int end = tri.Count;
            for (int t = fromTri; t < end; t += 3)
            {
                int a = V(pos[tri[t]], col[tri[t]]);
                int b = V(pos[tri[t + 2]], col[tri[t + 2]]);
                int c = V(pos[tri[t + 1]], col[tri[t + 1]]);
                T(a, b, c);
            }
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            if (pos.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(pos);
            m.SetColors(col);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ---------------------------------------------------------------- shapes

        /// <summary>
        /// A tube along a path (trunk, branch, log, root). radius(t) and colour(t, angle) are given per ring (t = 0..1 along the
        /// path). Smooth sides; the far end can be closed with a jagged broken cap.
        /// </summary>
        public void Tube(IList<Vector3> path, System.Func<float, float> radius, int sides, System.Func<float, float, Color> color,
                         bool capStart, bool capEnd, float jagged = 0f, int seed = 0)
        {
            int rings = path.Count;
            if (rings < 2) return;
            var tangents = new Vector3[rings];
            for (int i = 0; i < rings; i++)
                tangents[i] = (path[Mathf.Min(i + 1, rings - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;

            // parallel-transported frames, so the tube does not twist
            Vector3 n = Vector3.Cross(tangents[0], Mathf.Abs(tangents[0].y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var frames = new Vector3[rings];
            frames[0] = n;
            for (int i = 1; i < rings; i++)
            {
                n = Quaternion.FromToRotation(tangents[i - 1], tangents[i]) * n;
                frames[i] = n;
            }

            int first = pos.Count;
            for (int i = 0; i < rings; i++)
            {
                float t = (float)i / (rings - 1);
                float r = radius(t);
                Vector3 b = Vector3.Cross(tangents[i], frames[i]);
                for (int j = 0; j < sides; j++)
                {
                    float a = j * Mathf.PI * 2f / sides;
                    Vector3 dir = frames[i] * Mathf.Cos(a) + b * Mathf.Sin(a);
                    V(path[i] + dir * r, color(t, a));
                }
            }
            for (int i = 0; i + 1 < rings; i++)
            {
                for (int j = 0; j < sides; j++)
                {
                    int j1 = (j + 1) % sides;
                    int a = first + i * sides + j, bb = first + i * sides + j1;
                    int c = first + (i + 1) * sides + j1, d = first + (i + 1) * sides + j;
                    // outward: (a, d, c) and (a, c, b), see the class comment
                    if (Vector3.Dot(Vector3.Cross(pos[d] - pos[a], pos[c] - pos[a]), pos[a] - path[i]) >= 0f)
                    {
                        T(a, d, c);
                        T(a, c, bb);
                    }
                    else
                    {
                        T(a, c, d);
                        T(a, bb, c);
                    }
                }
            }
            if (capStart) Cap(first, sides, path[0], -tangents[0], color(0f, 0f), 0f, seed);
            if (capEnd) Cap(first + (rings - 1) * sides, sides, path[rings - 1], tangents[rings - 1], color(1f, 0f), jagged, seed + 1);
        }

        void Cap(int ring, int sides, Vector3 center, Vector3 outward, Color c, float jagged, int seed)
        {
            Vector3 tip = center + outward * jagged * 0.5f;
            for (int j = 0; j < sides; j++)
            {
                Vector3 a = pos[ring + j], b = pos[ring + (j + 1) % sides];
                Vector3 mid = tip + outward * (jagged * ForestNoise.Hash01(j, seed, 77));
                if (Vector3.Dot(Vector3.Cross(b - a, mid - a), outward) >= 0f) FlatT(a, b, mid, c, c, c);
                else FlatT(a, mid, b, c, c, c);
            }
        }

        /// <summary>
        /// One tier of conifer foliage: a jagged, drooping star-shaped skirt (points alternate long and short) closed
        /// underneath. Flat-shaded for a crisp low-poly look.
        /// </summary>
        public void ConeTier(Vector3 apex, float baseY, float radius, int points, float droop, float twist,
                             Color top, Color tip, Color under, float wind, int seed)
        {
            var ring = new Vector3[points];
            for (int k = 0; k < points; k++)
            {
                float a = twist + k * Mathf.PI * 2f / points;
                bool lng = (k & 1) == 0;
                float r = radius * (lng ? 1f : 0.62f) * (0.88f + 0.24f * ForestNoise.Hash01(k, seed, 5));
                float y = baseY - droop * (lng ? 1f : 0.45f);
                ring[k] = new Vector3(apex.x + Mathf.Cos(a) * r, y, apex.z + Mathf.Sin(a) * r);
            }
            Vector3 underside = new Vector3(apex.x, baseY + (apex.y - baseY) * 0.2f, apex.z);
            Color cTop = WithAlpha(top, wind * 0.6f), cTip = WithAlpha(tip, wind), cUnder = WithAlpha(under, wind * 0.4f);
            for (int k = 0; k < points; k++)
            {
                Vector3 p0 = ring[k], p1 = ring[(k + 1) % points];
                Color c0 = (k & 1) == 0 ? cTip : cTop, c1 = ((k + 1) & 1) == 0 ? cTip : cTop;
                FlatT(apex, p1, p0, cTop, c1, c0);
                FlatT(underside, p0, p1, cUnder, cUnder, cUnder);
            }
        }

        /// <summary>A lumpy, flat-shaded blob: an icosphere pushed in and out by noise. squash scales it per axis.</summary>
        public void Blob(Vector3 center, float radius, Vector3 squash, int subdivisions, float lumpiness, int seed,
                         System.Func<Vector3, Color> color)
        {
            List<Vector3> verts;
            List<int> faces;
            Icosphere(subdivisions, out verts, out faces);
            var p = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                Vector3 d = verts[i];
                float n = ForestNoise.Gradient(d.x * 1.7f + d.y * 3.1f + seed * 0.37f, d.z * 1.7f - d.y * 2.3f, seed)
                        + 0.5f * ForestNoise.Gradient(d.x * 3.9f - d.y * 1.3f, d.z * 3.9f + d.y * 4.7f, seed + 9);
                float r = radius * (1f + lumpiness * n);
                p[i] = center + Vector3.Scale(d * r, squash);
            }
            for (int f = 0; f < faces.Count; f += 3)
            {
                Vector3 a = p[faces[f]], b = p[faces[f + 1]], c = p[faces[f + 2]];
                Vector3 mid = (a + b + c) / 3f;
                Color col = color(mid);
                // keep faces pointing away from the centre
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), mid - center) >= 0f) FlatT(a, b, c, col, col, col);
                else FlatT(a, c, b, col, col, col);
            }
        }

        /// <summary>A thin, tapering, curved strip (grass blade, fern frond), double sided.</summary>
        public void Strip(IList<Vector3> spine, Vector3 side, System.Func<float, float> width, System.Func<float, Color> color)
        {
            int start = tri.Count;
            int first = pos.Count;
            for (int i = 0; i < spine.Count; i++)
            {
                float t = (float)i / (spine.Count - 1);
                float w = width(t) * 0.5f;
                Color c = color(t);
                V(spine[i] - side * w, c);
                V(spine[i] + side * w, c);
            }
            for (int i = 0; i + 1 < spine.Count; i++)
            {
                int a = first + i * 2, b = a + 1, c = a + 3, d = a + 2;
                T(a, b, c);
                T(a, c, d);
            }
            BackFaces(start);
        }

        public static Color WithAlpha(Color c, float a)
        {
            c.a = Mathf.Clamp01(a);
            return c;
        }

        /// <summary>sRGB colour as linear vertex colour (the shader works in linear), with a wind weight.</summary>
        public static Color Lin(float r, float g, float b, float wind = 0f)
        {
            Color c = new Color(r, g, b, 1f).linear;
            c.a = wind;
            return c;
        }

        static readonly Dictionary<int, KeyValuePair<List<Vector3>, List<int>>> icoCache = new Dictionary<int, KeyValuePair<List<Vector3>, List<int>>>();

        static void Icosphere(int subdivisions, out List<Vector3> verts, out List<int> faces)
        {
            KeyValuePair<List<Vector3>, List<int>> cached;
            if (icoCache.TryGetValue(subdivisions, out cached))
            {
                verts = cached.Key;
                faces = cached.Value;
                return;
            }
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            verts = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1)
            };
            for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;
            faces = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };
            for (int s = 0; s < subdivisions; s++)
            {
                var mids = new Dictionary<long, int>();
                var next = new List<int>();
                for (int f = 0; f < faces.Count; f += 3)
                {
                    int a = faces[f], b = faces[f + 1], c = faces[f + 2];
                    int ab = Mid(verts, mids, a, b), bc = Mid(verts, mids, b, c), ca = Mid(verts, mids, c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                faces = next;
            }
            icoCache[subdivisions] = new KeyValuePair<List<Vector3>, List<int>>(verts, faces);
        }

        static int Mid(List<Vector3> verts, Dictionary<long, int> mids, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int i;
            if (mids.TryGetValue(key, out i)) return i;
            verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
            mids[key] = verts.Count - 1;
            return verts.Count - 1;
        }
    }
}
