using System.Collections.Generic;
using UnityEngine;

namespace HavocCave
{
    /// <summary>Raw mesh arrays for one chunk. Plain data so it can be produced on a worker thread.</summary>
    public sealed class CaveChunkMesh
    {
        public Vector3 origin;          // chunk corner in cave space; vertices are relative to it
        public Vector3[] vertices;
        public Vector3[] normals;
        public Color32[] colors;        // r = dirt path mask, g = baked ambient occlusion
        public Vector2[] uv2;           // lightmap UVs; null until the lightmapper has run
        public int[] triangles;
    }

    /// <summary>
    /// Naive Surface Nets over a cube of Cells x Cells x Cells voxels.
    /// Each chunk samples one voxel of padding on every side and only emits the faces that belong to it,
    /// so neighbouring chunks line up exactly with no gaps. Not thread safe: use one mesher per thread.
    /// </summary>
    public sealed class CaveMesher
    {
        public const int Cells = 32;
        const int P = Cells + 2;        // samples per axis: local index -1 .. Cells
        const int C = P - 1;            // cells per axis, including the padding layer

        static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
        static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };

        readonly float[] samples = new float[P * P * P];
        readonly int[] cellVertex = new int[C * C * C];
        readonly float[] corner = new float[8];
        readonly List<Vector3> verts = new List<Vector3>(4096);
        readonly List<bool> vertNearEdge = new List<bool>(4096);

        const int MinPieceTriangles = 40;     // a surface piece smaller than this that is not near a chunk edge is a speck, not terrain
        const int EdgeMargin = 3;             // cells
        readonly List<int> tris = new List<int>(16384);

        /// <summary>
        /// gridOrigin is the corner of the whole cave grid and (ox, oy, oz) is this chunk's offset from it in voxels.
        /// Every position is computed from those global integers, so the same vertex comes out bit-for-bit identical
        /// in two neighbouring chunks and the seam between them cannot crack.
        /// </summary>
        public CaveChunkMesh Build(CaveField field, CaveField.Region region, Vector3 gridOrigin, int ox, int oy, int oz, float vs, float normalEps,
                                   CaveLamp[] lamps, CaveField.Region[] lampRegions, float lampRange, float lampIntensity)
        {
            Vector3 origin = gridOrigin + new Vector3(ox, oy, oz) * vs;     // where the chunk object sits
            // 1. Sample the field on the grid.
            float lo = float.MaxValue;
            float hi = float.MinValue;
            int i = 0;
            for (int z = 0; z < P; z++)
            {
                float pz = gridOrigin.z + (oz + z - 1) * vs;
                for (int y = 0; y < P; y++)
                {
                    float py = gridOrigin.y + (oy + y - 1) * vs;
                    for (int x = 0; x < P; x++)
                    {
                        float v = field.Density(region, new Vector3(gridOrigin.x + (ox + x - 1) * vs, py, pz));
                        samples[i++] = v;
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                }
            }
            if (lo >= 0f || hi < 0f) return null;   // all rock or all air: nothing to draw

            // 2. One vertex in every cell the surface passes through.
            verts.Clear();
            vertNearEdge.Clear();
            tris.Clear();
            for (int c = 0; c < cellVertex.Length; c++) cellVertex[c] = -1;

            for (int cz = 0; cz < C; cz++)
            for (int cy = 0; cy < C; cy++)
            for (int cx = 0; cx < C; cx++)
            {
                int b = cx + P * (cy + P * cz);
                corner[0] = samples[b];
                corner[1] = samples[b + 1];
                corner[2] = samples[b + P];
                corner[3] = samples[b + P + 1];
                corner[4] = samples[b + P * P];
                corner[5] = samples[b + P * P + 1];
                corner[6] = samples[b + P * P + P];
                corner[7] = samples[b + P * P + P + 1];

                int mask = 0;
                for (int k = 0; k < 8; k++) if (corner[k] < 0f) mask |= 1 << k;
                if (mask == 0 || mask == 255) continue;

                float sx = 0f, sy = 0f, sz = 0f;
                int count = 0;
                for (int e = 0; e < 12; e++)
                {
                    int ca = EdgeA[e];
                    int cb = EdgeB[e];
                    float va = corner[ca];
                    float vb = corner[cb];
                    if ((va < 0f) == (vb < 0f)) continue;

                    float t = va / (va - vb);
                    sx += (ca & 1) + ((cb & 1) - (ca & 1)) * t;
                    sy += ((ca >> 1) & 1) + (((cb >> 1) & 1) - ((ca >> 1) & 1)) * t;
                    sz += ((ca >> 2) & 1) + (((cb >> 2) & 1) - ((ca >> 2) & 1)) * t;
                    count++;
                }

                float inv = 1f / count;
                cellVertex[cx + C * (cy + C * cz)] = verts.Count;
                vertNearEdge.Add(cx < EdgeMargin || cy < EdgeMargin || cz < EdgeMargin ||
                                 cx >= C - EdgeMargin || cy >= C - EdgeMargin || cz >= C - EdgeMargin);
                verts.Add(new Vector3(gridOrigin.x + (ox + cx - 1 + sx * inv) * vs,
                                      gridOrigin.y + (oy + cy - 1 + sy * inv) * vs,
                                      gridOrigin.z + (oz + cz - 1 + sz * inv) * vs));     // world (cave-space) position
            }

            // 3. A quad across every grid edge that crosses the surface. This chunk owns the edges
            //    that start inside it; the cells around the edge may live in the padding layer.
            for (int z = 1; z <= Cells; z++)
            for (int y = 1; y <= Cells; y++)
            for (int x = 1; x <= Cells; x++)
            {
                int b = x + P * (y + P * z);
                bool air = samples[b] < 0f;

                if (air != (samples[b + 1] < 0f))
                    Quad(x, y - 1, z - 1, x, y, z - 1, x, y, z, x, y - 1, z, air);
                if (air != (samples[b + P] < 0f))
                    Quad(x - 1, y, z - 1, x - 1, y, z, x, y, z, x, y, z - 1, air);
                if (air != (samples[b + P * P] < 0f))
                    Quad(x - 1, y - 1, z, x, y - 1, z, x, y, z, x - 1, y, z, air);
            }

            RemoveSpecks();
            if (tris.Count == 0) return null;

            // 4. Per-vertex shading data straight from the field, so seams between chunks match exactly.
            int vc = verts.Count;
            var result = new CaveChunkMesh
            {
                origin = origin,
                vertices = new Vector3[vc],
                normals = new Vector3[vc],
                colors = new Color32[vc],
                triangles = tris.ToArray()
            };

            float aoScale = Mathf.Max(1f, vs / 0.5f);

            // Only the lamps that can reach this chunk matter.
            var nearLamps = new List<int>();
            float chunkSize = Cells * vs;
            for (int q = 0; q < lamps.Length; q++)
            {
                Vector3 lp = lamps[q].position;
                if (lp.x < origin.x - lampRange || lp.x > origin.x + chunkSize + lampRange ||
                    lp.y < origin.y - lampRange || lp.y > origin.y + chunkSize + lampRange ||
                    lp.z < origin.z - lampRange || lp.z > origin.z + chunkSize + lampRange) continue;
                nearLamps.Add(q);
            }
            for (int v = 0; v < vc; v++)
            {
                // Pull the vertex onto the true wall so the mesh, the props and the shading all agree.
                Vector3 p = field.SnapToSurface(region, verts[v], vs * 0.5f);
                Vector3 local = p - origin;
                Vector3 n = field.Normal(region, p, normalEps);
                float dirt = field.DirtMask(region, p);
                float ao = field.Occlusion(region, p, n, aoScale);

                // Light from the lamps, baked into the vertex: soft falloff, facing the lamp, and only if the lamp can see it.
                float lamp = 0f;
                for (int q = 0; q < nearLamps.Count; q++)
                {
                    Vector3 toLamp = lamps[nearLamps[q]].position - p;
                    float dist = toLamp.magnitude;
                    if (dist > lampRange || dist < 0.05f) continue;
                    float facing = Vector3.Dot(n, toLamp / dist);
                    if (facing <= 0.03f) continue;
                    float falloff = 1f - dist / lampRange;
                    float contribution = falloff * falloff * facing * lampIntensity;
                    if (contribution < 0.01f) continue;
                    if (!field.Visible(lampRegions[nearLamps[q]], p + n * 0.15f, lamps[nearLamps[q]].position)) continue;
                    lamp += contribution;
                }

                result.vertices[v] = local;
                result.normals[v] = n;
                result.colors[v] = new Color32((byte)Mathf.RoundToInt(dirt * 255f), (byte)Mathf.RoundToInt(ao * 255f),
                                               (byte)Mathf.RoundToInt(Mathf.Clamp01(lamp / 4f) * 255f), 255);
            }
            return result;
        }

        /// <summary>
        /// Noise can leave tiny closed bubbles or islands of surface that float free of the cave. When such a piece
        /// is small and lies wholly inside this chunk, drop its triangles.
        /// </summary>
        void RemoveSpecks()
        {
            int vc = verts.Count;
            var parent = new int[vc];
            for (int i = 0; i < vc; i++) parent[i] = i;

            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = Find(parent, tris[i]);
                int b = Find(parent, tris[i + 1]);
                int c = Find(parent, tris[i + 2]);
                parent[b] = a;
                parent[Find(parent, c)] = a;
            }

            var triCount = new int[vc];
            var touchesEdge = new bool[vc];
            for (int i = 0; i < tris.Count; i += 3)
            {
                int root = Find(parent, tris[i]);
                triCount[root]++;
                if (vertNearEdge[tris[i]] || vertNearEdge[tris[i + 1]] || vertNearEdge[tris[i + 2]]) touchesEdge[root] = true;
            }

            int write = 0;
            for (int i = 0; i < tris.Count; i += 3)
            {
                int root = Find(parent, tris[i]);
                if (triCount[root] < MinPieceTriangles && !touchesEdge[root]) continue;   // a speck: leave it out
                tris[write++] = tris[i];
                tris[write++] = tris[i + 1];
                tris[write++] = tris[i + 2];
            }
            tris.RemoveRange(write, tris.Count - write);
        }

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Quad(int ax, int ay, int az, int bx, int by, int bz, int cx, int cy, int cz, int dx, int dy, int dz, bool flip)
        {
            int q0 = cellVertex[ax + C * (ay + C * az)];
            int q1 = cellVertex[bx + C * (by + C * bz)];
            int q2 = cellVertex[cx + C * (cy + C * cz)];
            int q3 = cellVertex[dx + C * (dy + C * dz)];
            if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0) return;

            if (!flip)
            {
                tris.Add(q0); tris.Add(q1); tris.Add(q2);
                tris.Add(q0); tris.Add(q2); tris.Add(q3);
            }
            else
            {
                tris.Add(q0); tris.Add(q3); tris.Add(q2);
                tris.Add(q0); tris.Add(q2); tris.Add(q1);
            }
        }
    }
}
