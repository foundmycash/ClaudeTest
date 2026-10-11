using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace HavocCave.Lightmapping
{
    /// <summary>One patch of surface that is flattened onto a plane and gets its own rectangle in the atlas.</summary>
    internal sealed class LmChart
    {
        public int chunk;
        public int first, count;                // range in LmChunkCharts.order
        public float tx, ty, tz, bx, by, bz;    // the plane's two axes, in the chunk's local space
        public float cosR, sinR, minA, minB;    // rotation that tightens the bounding rectangle, and its corner
        public float w, h;                      // size in metres after rotating
        public bool swapped;                    // turned a further quarter turn so the rectangle is wider than tall
        public int rectW, rectH;                // texels, padding included
        public int x, y;                        // where the rectangle sits in the atlas
        public float area;                      // real surface area in square metres
    }

    internal sealed class LmChunkCharts
    {
        public int[] order;                     // triangle indices, grouped by chart
        public readonly List<LmChart> charts = new List<LmChart>();
    }

    /// <summary>
    /// Gives the cave mesh lightmap UVs. The mesh comes out of marching voxels, so it has no UVs and no flat faces to
    /// unfold. Instead the surface is cut into charts: a chart grows triangle by triangle while every triangle still
    /// faces roughly the way the chart's first one did, which makes it a height field over its plane, so flattening
    /// it by projection cannot fold it over itself. Charts are rotated to a tight rectangle and packed into one atlas
    /// that every chunk shares, so the whole cave is one lightmap texture.
    /// </summary>
    internal static class LmUnwrap
    {
        // ------------------------------------------------------------------ chart building

        /// <summary>
        /// cosMax: a triangle may join a chart while its (smoothed) normal is within this cosine of the chart's normal.
        /// maxExtent: the longest a chart may be along either side of its plane, in metres.
        /// </summary>
        public static LmChunkCharts BuildCharts(CaveChunkMesh mesh, int chunkIndex, float cosMax, float maxExtent, CancellationToken token)
        {
            return new Grower(mesh, chunkIndex, cosMax, maxExtent).Run(token);
        }

        /// <summary>Grows the charts of one chunk. Holds the per-chart scratch state so the hot loop has no long argument lists.</summary>
        sealed class Grower
        {
            readonly CaveChunkMesh mesh;
            readonly int chunkIndex;
            readonly float cosMax, maxExtent;
            readonly Vector3[] v;
            readonly int[] tris;
            readonly int T, V;
            readonly float[] fn;            // face normals
            readonly float[] sn;            // smoothed normals (average of the corner normals), much less noisy than the face normals
            readonly int[] vStart, vTri;    // vertex -> triangles
            readonly int[] triChart, rejected, tested;
            readonly int[] vertSeen;
            readonly float[] pu, pv;        // corner positions on the current chart's plane
            readonly int[] pStamp;

            // spatial hash of the triangles already in the chart, in plane coordinates
            readonly Dictionary<int, int> cellHead = new Dictionary<int, int>();
            int[] entTri = new int[1024], entNext = new int[1024];
            int entCount;
            readonly float cellSize, invCell;

            // the chart being grown
            Vector3 n0, tAxis, bAxis;
            float originU, originV;
            float minU, maxU, minV, maxV;
            int chartId, serial;

            public Grower(CaveChunkMesh mesh, int chunkIndex, float cosMax, float maxExtent)
            {
                this.mesh = mesh;
                this.chunkIndex = chunkIndex;
                this.cosMax = cosMax;
                this.maxExtent = maxExtent;
                v = mesh.vertices;
                tris = mesh.triangles;
                T = tris.Length / 3;
                V = v.Length;
                Vector3[] vn = mesh.normals;

                fn = new float[T * 3];
                sn = new float[T * 3];
                double edgeSum = 0;
                for (int t = 0; t < T; t++)
                {
                    int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                    Vector3 a = v[i0], b = v[i1], c = v[i2];
                    Vector3 s = vn[i0] + vn[i1] + vn[i2];
                    float sl = s.magnitude;
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    float len = n.magnitude;
                    if (sl < 1e-9f) s = len > 1e-12f ? n / len : Vector3.up; else s /= sl;
                    n = len < 1e-12f ? s : n / len;
                    fn[t * 3] = n.x; fn[t * 3 + 1] = n.y; fn[t * 3 + 2] = n.z;
                    sn[t * 3] = s.x; sn[t * 3 + 1] = s.y; sn[t * 3 + 2] = s.z;
                    edgeSum += (b - a).magnitude + (c - b).magnitude + (a - c).magnitude;
                }
                float meanEdge = T > 0 ? (float)(edgeSum / (3.0 * T)) : 0.5f;
                cellSize = Math.Max(0.05f, meanEdge * 1.5f);
                invCell = 1f / cellSize;

                vStart = new int[V + 1];
                for (int i = 0; i < tris.Length; i++) vStart[tris[i] + 1]++;
                for (int i = 0; i < V; i++) vStart[i + 1] += vStart[i];
                vTri = new int[tris.Length];
                var fill = new int[V];
                for (int t = 0; t < T; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = tris[t * 3 + k];
                        vTri[vStart[vi] + fill[vi]++] = t;
                    }

                triChart = new int[T];
                rejected = new int[T];
                tested = new int[T];
                for (int t = 0; t < T; t++) { triChart[t] = -1; rejected[t] = -1; tested[t] = -1; }
                vertSeen = new int[V];
                pStamp = new int[V];
                pu = new float[V];
                pv = new float[V];
                for (int i = 0; i < V; i++) { vertSeen[i] = -1; pStamp[i] = -1; }
            }

            public LmChunkCharts Run(CancellationToken token)
            {
                var result = new LmChunkCharts();
                result.order = new int[T];
                var queue = new int[T];
                var chartU = new List<float>();
                var chartV = new List<float>();
                int orderCount = 0;

                for (int seed = 0; seed < T; seed++)
                {
                    if (triChart[seed] != -1) continue;
                    if ((seed & 0xFF) == 0 && token.IsCancellationRequested) throw new OperationCanceledException(token);

                    chartId = result.charts.Count;

                    // the plane is perpendicular to the seed's smoothed normal
                    n0 = new Vector3(sn[seed * 3], sn[seed * 3 + 1], sn[seed * 3 + 2]);
                    Vector3 helper = Mathf.Abs(n0.y) < 0.9f ? Vector3.up : Vector3.right;
                    tAxis = Vector3.Cross(helper, n0).normalized;
                    bAxis = Vector3.Cross(n0, tAxis);
                    Vector3 origin = v[tris[seed * 3]];
                    originU = Vector3.Dot(origin, tAxis);
                    originV = Vector3.Dot(origin, bAxis);
                    minU = minV = float.MaxValue;
                    maxU = maxV = float.MinValue;
                    cellHead.Clear();
                    entCount = 0;

                    var chart = new LmChart
                    {
                        chunk = chunkIndex,
                        first = orderCount,
                        tx = tAxis.x, ty = tAxis.y, tz = tAxis.z,
                        bx = bAxis.x, by = bAxis.y, bz = bAxis.z
                    };

                    int head = 0, tail = 0;
                    Accept(seed, true);
                    triChart[seed] = chartId;
                    result.order[orderCount++] = seed;
                    queue[tail++] = seed;

                    while (head < tail)
                    {
                        int t = queue[head++];
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = tris[t * 3 + k];
                            for (int j = vStart[vi]; j < vStart[vi + 1]; j++)
                            {
                                int n = vTri[j];
                                if (triChart[n] != -1 || rejected[n] == chartId) continue;
                                if (!Accept(n, false)) { rejected[n] = chartId; continue; }
                                triChart[n] = chartId;
                                result.order[orderCount++] = n;
                                queue[tail++] = n;
                            }
                        }
                    }

                    chart.count = orderCount - chart.first;
                    FinishChart(chart, result.order, tris, v, vertSeen, chartId, tAxis, bAxis, chartU, chartV);
                    result.charts.Add(chart);
                }
                return result;
            }

            void Project(int vi)
            {
                if (pStamp[vi] == chartId) return;
                pStamp[vi] = chartId;
                Vector3 p = v[vi];
                pu[vi] = Vector3.Dot(p, tAxis) - originU;
                pv[vi] = Vector3.Dot(p, bAxis) - originV;
            }

            /// <summary>
            /// Tests a triangle against the chart and, if it passes, adds it. It must face about the way the chart does, keep the
            /// chart small enough, and not land on top of any triangle of the chart that it does not touch.
            /// </summary>
            bool Accept(int t, bool force)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                if (!force)
                {
                    float ds = sn[t * 3] * n0.x + sn[t * 3 + 1] * n0.y + sn[t * 3 + 2] * n0.z;
                    float df = fn[t * 3] * n0.x + fn[t * 3 + 1] * n0.y + fn[t * 3 + 2] * n0.z;
                    if (ds < cosMax || df < 0.15f) return false;      // the second test keeps the projection from flipping a triangle
                }

                Project(i0); Project(i1); Project(i2);
                float u0 = pu[i0], w0 = pv[i0], u1 = pu[i1], w1 = pv[i1], u2 = pu[i2], w2 = pv[i2];
                float bx0 = Math.Min(u0, Math.Min(u1, u2)), bx1 = Math.Max(u0, Math.Max(u1, u2));
                float by0 = Math.Min(w0, Math.Min(w1, w2)), by1 = Math.Max(w0, Math.Max(w1, w2));

                float nminU = Math.Min(minU, bx0), nmaxU = Math.Max(maxU, bx1);
                float nminV = Math.Min(minV, by0), nmaxV = Math.Max(maxV, by1);
                if (!force && (nmaxU - nminU > maxExtent || nmaxV - nminV > maxExtent)) return false;

                int cx0 = (int)Math.Floor(bx0 * invCell), cx1 = (int)Math.Floor(bx1 * invCell);
                int cy0 = (int)Math.Floor(by0 * invCell), cy1 = (int)Math.Floor(by1 * invCell);

                if (!force)
                {
                    serial++;
                    for (int cy = cy0; cy <= cy1; cy++)
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            int e;
                            if (!cellHead.TryGetValue(CellKey(cx, cy), out e)) continue;
                            for (; e >= 0; e = entNext[e])
                            {
                                int q = entTri[e];
                                if (tested[q] == serial) continue;
                                tested[q] = serial;
                                if (Touches(tris, t, q)) continue;
                                if (Overlaps(t, q)) return false;
                            }
                        }
                }

                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int key = CellKey(cx, cy);
                        int prev;
                        if (!cellHead.TryGetValue(key, out prev)) prev = -1;
                        if (entCount == entTri.Length)
                        {
                            Array.Resize(ref entTri, entCount * 2);
                            Array.Resize(ref entNext, entCount * 2);
                        }
                        entTri[entCount] = t;
                        entNext[entCount] = prev;
                        cellHead[key] = entCount++;
                    }

                minU = nminU; maxU = nmaxU; minV = nminV; maxV = nmaxV;
                return true;
            }

            const float OverlapTolerance = 0.004f;      // metres of overlap that are let through: far below a texel

            /// <summary>Do two triangles overlap on the chart's plane by more than a sliver? Separating axis test.</summary>
            bool Overlaps(int a, int b)
            {
                int a0 = tris[a * 3], a1 = tris[a * 3 + 1], a2 = tris[a * 3 + 2];
                int b0 = tris[b * 3], b1 = tris[b * 3 + 1], b2 = tris[b * 3 + 2];
                float ax0 = pu[a0], ay0 = pv[a0], ax1 = pu[a1], ay1 = pv[a1], ax2 = pu[a2], ay2 = pv[a2];
                // b may not have been projected for this chart yet only if it is not in the chart, but all entries are
                float bx0 = pu[b0], by0 = pv[b0], bx1 = pu[b1], by1 = pv[b1], bx2 = pu[b2], by2 = pv[b2];

                return !Separated(ax0, ay0, ax1, ay1, ax2, ay2, bx0, by0, bx1, by1, bx2, by2) &&
                       !Separated(bx0, by0, bx1, by1, bx2, by2, ax0, ay0, ax1, ay1, ax2, ay2);
            }

            /// <summary>True if one of the edges of triangle A is a separating axis for A and B.</summary>
            static bool Separated(float ax0, float ay0, float ax1, float ay1, float ax2, float ay2,
                                  float bx0, float by0, float bx1, float by1, float bx2, float by2)
            {
                for (int e = 0; e < 3; e++)
                {
                    float sx, sy, ex, ey;
                    if (e == 0) { sx = ax0; sy = ay0; ex = ax1; ey = ay1; }
                    else if (e == 1) { sx = ax1; sy = ay1; ex = ax2; ey = ay2; }
                    else { sx = ax2; sy = ay2; ex = ax0; ey = ay0; }
                    float nx = -(ey - sy), ny = ex - sx;
                    float len = (float)Math.Sqrt(nx * nx + ny * ny);
                    if (len < 1e-9f) continue;
                    nx /= len; ny /= len;

                    float a0 = ax0 * nx + ay0 * ny, a1 = ax1 * nx + ay1 * ny, a2 = ax2 * nx + ay2 * ny;
                    float b0 = bx0 * nx + by0 * ny, b1 = bx1 * nx + by1 * ny, b2 = bx2 * nx + by2 * ny;
                    float amin = Math.Min(a0, Math.Min(a1, a2)), amax = Math.Max(a0, Math.Max(a1, a2));
                    float bmin = Math.Min(b0, Math.Min(b1, b2)), bmax = Math.Max(b0, Math.Max(b1, b2));
                    if (amax <= bmin + OverlapTolerance || bmax <= amin + OverlapTolerance) return true;
                }
                return false;
            }

            static int CellKey(int x, int y)
            {
                return (x + 8192) * 16384 + (y + 8192);
            }
        }

        static bool Touches(int[] tris, int a, int b)
        {
            int a0 = tris[a * 3], a1 = tris[a * 3 + 1], a2 = tris[a * 3 + 2];
            int b0 = tris[b * 3], b1 = tris[b * 3 + 1], b2 = tris[b * 3 + 2];
            return a0 == b0 || a0 == b1 || a0 == b2 || a1 == b0 || a1 == b1 || a1 == b2 || a2 == b0 || a2 == b1 || a2 == b2;
        }

        /// <summary>Rotates the chart to the tightest bounding rectangle and measures it.</summary>
        static void FinishChart(LmChart chart, int[] order, int[] tris, Vector3[] v, int[] vertSeen, int chartId,
                                Vector3 tAxis, Vector3 bAxis, List<float> us, List<float> vs)
        {
            us.Clear();
            vs.Clear();
            float area = 0f;
            for (int i = chart.first; i < chart.first + chart.count; i++)
            {
                int t = order[i];
                Vector3 a = v[tris[t * 3]], b = v[tris[t * 3 + 1]], c = v[tris[t * 3 + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    if (vertSeen[vi] == chartId) continue;
                    vertSeen[vi] = chartId;
                    us.Add(Vector3.Dot(v[vi], tAxis));
                    vs.Add(Vector3.Dot(v[vi], bAxis));
                }
            }
            chart.area = area;

            const int steps = 15;
            float bestArea = float.MaxValue;
            for (int s = 0; s < steps; s++)
            {
                float ang = s * (Mathf.PI * 0.5f / steps);
                float cs = (float)Math.Cos(ang), sn = (float)Math.Sin(ang);
                float lo0 = float.MaxValue, hi0 = float.MinValue, lo1 = float.MaxValue, hi1 = float.MinValue;
                for (int i = 0; i < us.Count; i++)
                {
                    float a = us[i] * cs + vs[i] * sn;
                    float b = -us[i] * sn + vs[i] * cs;
                    if (a < lo0) lo0 = a;
                    if (a > hi0) hi0 = a;
                    if (b < lo1) lo1 = b;
                    if (b > hi1) hi1 = b;
                }
                float rectArea = (hi0 - lo0) * (hi1 - lo1);
                if (s == 0 || rectArea < bestArea * 0.97f)
                {
                    bestArea = rectArea;
                    chart.cosR = cs; chart.sinR = sn;
                    chart.minA = lo0; chart.minB = lo1;
                    chart.w = hi0 - lo0; chart.h = hi1 - lo1;
                }
            }
        }

        // ------------------------------------------------------------------ packing

        static readonly int[] WidthChoices = { 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096 };

        /// <summary>
        /// Packs every chart into one atlas. Starts at texelsPerMeter and backs off until everything fits in maxSize x maxSize.
        /// Fills in each chart's rectangle and position and returns the atlas size and the density that was used.
        /// </summary>
        public static bool Pack(List<LmChart> charts, float texelsPerMeter, int pad, int maxSize, out int atlasW, out int atlasH, out float usedDensity)
        {
            atlasW = atlasH = 0;
            usedDensity = texelsPerMeter;
            int n = charts.Count;
            if (n == 0) return false;

            var order = new int[n];
            var heights = new int[n];
            float d = texelsPerMeter;

            for (int attempt = 0; attempt < 60; attempt++)
            {
                int widest = 0;
                for (int i = 0; i < n; i++)
                {
                    LmChart c = charts[i];
                    int rw = (int)Math.Ceiling(c.w * d) + 2 * pad;
                    int rh = (int)Math.Ceiling(c.h * d) + 2 * pad;
                    c.swapped = rh > rw;
                    if (c.swapped) { int tmp = rw; rw = rh; rh = tmp; }
                    c.rectW = rw; c.rectH = rh;
                    order[i] = i;
                    heights[i] = rh;
                    if (rw > widest) widest = rw;
                }
                // tall first, then wide first
                Array.Sort(order, (a, b) =>
                {
                    int c = charts[b].rectH.CompareTo(charts[a].rectH);
                    return c != 0 ? c : charts[b].rectW.CompareTo(charts[a].rectW);
                });

                long bestArea = long.MaxValue;
                int bestW = 0, bestH = 0;
                for (int wi = 0; wi < WidthChoices.Length; wi++)
                {
                    int w = WidthChoices[wi];
                    if (w > maxSize || w < widest) continue;
                    int h;
                    if (!TryShelves(charts, order, w, maxSize, out h, false)) continue;
                    h = (h + 3) & ~3;
                    long area = (long)w * h;
                    if (area < bestArea || (area == bestArea && Math.Max(w, h) < Math.Max(bestW, bestH)))
                    {
                        bestArea = area;
                        bestW = w;
                        bestH = h;
                    }
                }

                if (bestW > 0)
                {
                    int h;
                    TryShelves(charts, order, bestW, maxSize, out h, true);
                    atlasW = bestW;
                    atlasH = bestH;
                    usedDensity = d;
                    return true;
                }
                d *= 0.92f;
                if (d < 0.25f) break;
            }
            return false;
        }

        sealed class Shelf
        {
            public int y, h, used;
        }

        /// <summary>First-fit shelf packing of charts that are sorted tallest first. Writes positions only when commit is true.</summary>
        static bool TryShelves(List<LmChart> charts, int[] order, int width, int maxHeight, out int usedHeight, bool commit)
        {
            var shelves = new List<Shelf>();
            int top = 0;
            usedHeight = 0;
            for (int oi = 0; oi < order.Length; oi++)
            {
                LmChart c = charts[order[oi]];
                if (c.rectW > width) return false;
                Shelf target = null;
                for (int s = 0; s < shelves.Count; s++)
                {
                    Shelf sh = shelves[s];
                    if (sh.h >= c.rectH && sh.used + c.rectW <= width) { target = sh; break; }
                }
                if (target == null)
                {
                    if (top + c.rectH > maxHeight) return false;
                    target = new Shelf { y = top, h = c.rectH, used = 0 };
                    shelves.Add(target);
                    top += c.rectH;
                }
                if (commit) { c.x = target.used; c.y = target.y; }
                target.used += c.rectW;
            }
            usedHeight = top;
            return true;
        }

        // ------------------------------------------------------------------ writing the UVs

        /// <summary>
        /// Rebuilds the chunk's mesh arrays with the UV2 set. A vertex that belongs to triangles of two charts has to exist
        /// twice (once per chart), so the vertex arrays grow a little at the chart borders.
        /// </summary>
        public static void Emit(CaveChunkMesh mesh, LmChunkCharts cc, int atlasW, int atlasH, float density, int pad)
        {
            Vector3[] v = mesh.vertices;
            Vector3[] vn = mesh.normals;
            Color32[] vc = mesh.colors;
            int[] tris = mesh.triangles;
            int cap = tris.Length;

            var nv = new Vector3[cap];
            var nn = new Vector3[cap];
            var nc = new Color32[cap];
            var uv = new Vector2[cap];
            var nt = new int[tris.Length];
            var seen = new int[v.Length];
            var remap = new int[v.Length];
            for (int i = 0; i < seen.Length; i++) seen[i] = -1;

            int count = 0, ti = 0;
            float invW = 1f / atlasW, invH = 1f / atlasH;
            for (int ci = 0; ci < cc.charts.Count; ci++)
            {
                LmChart ch = cc.charts[ci];
                for (int o = ch.first; o < ch.first + ch.count; o++)
                {
                    int t = cc.order[o];
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = tris[t * 3 + k];
                        if (seen[vi] != ci)
                        {
                            seen[vi] = ci;
                            remap[vi] = count;
                            Vector3 p = v[vi];
                            float u = p.x * ch.tx + p.y * ch.ty + p.z * ch.tz;
                            float w = p.x * ch.bx + p.y * ch.by + p.z * ch.bz;
                            float a = u * ch.cosR + w * ch.sinR - ch.minA;
                            float b = -u * ch.sinR + w * ch.cosR - ch.minB;
                            if (ch.swapped) { float na = b; b = ch.w - a; a = na; }
                            uv[count] = new Vector2((ch.x + pad + a * density) * invW, (ch.y + pad + b * density) * invH);
                            nv[count] = p;
                            nn[count] = vn[vi];
                            nc[count] = vc[vi];
                            count++;
                        }
                        nt[ti++] = remap[vi];
                    }
                }
            }

            mesh.vertices = Trim(nv, count);
            mesh.normals = Trim(nn, count);
            mesh.colors = Trim(nc, count);
            mesh.uv2 = Trim(uv, count);
            mesh.triangles = nt;
        }

        static T[] Trim<T>(T[] src, int count)
        {
            var dst = new T[count];
            Array.Copy(src, dst, count);
            return dst;
        }
    }
}
