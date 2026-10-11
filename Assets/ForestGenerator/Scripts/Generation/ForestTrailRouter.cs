using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Finds natural-looking routes for the trails with A* on a cost grid. Steep ground costs more (trails follow the
    /// contours), a noise field makes them wander, and in some regions the noise is switched off so a trail runs straight for
    /// a long stretch (a long sightline). Trails that were routed earlier are cheap to walk along (later trails join them at
    /// junctions) and the ground just beside them is expensive (no two trails running side by side).
    /// </summary>
    public sealed class ForestTrailRouter
    {
        const float ReuseFactor = 0.35f;

        readonly int w, h;
        readonly float cell;
        readonly Vector2 origin;          // centre of cell (0, 0)
        readonly float[] baseCost;        // per cell, float.PositiveInfinity = blocked
        readonly bool[] onTrail;
        readonly float[] nearPenalty;
        readonly bool[] exempt;           // around clearings, where trails are supposed to meet

        // A* buffers, reused for every route
        readonly float[] g;
        readonly int[] cameFrom;
        readonly byte[] state;            // 0 = untouched, 1 = open, 2 = closed
        readonly List<int> touched = new List<int>();
        readonly MinHeap heap = new MinHeap(4096);

        public ForestTrailRouter(ForestShape shape, ForestGrid heights, ForestTrailSettings s, int seed)
        {
            cell = Mathf.Max(0.5f, s.routingCell);
            w = Mathf.CeilToInt(shape.terrainSize.x / cell);
            h = Mathf.CeilToInt(shape.terrainSize.y / cell);
            origin = shape.terrainMin + new Vector2(cell, cell) * 0.5f;

            int n = w * h;
            baseCost = new float[n];
            onTrail = new bool[n];
            nearPenalty = new float[n];
            exempt = new bool[n];
            g = new float[n];
            cameFrom = new int[n];
            state = new byte[n];

            ForestGrid smooth = heights.Blurred(4f);
            int wanderSeed = seed ^ 0x3A7E1;
            int straightSeed = seed ^ 0x51D7A;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector2 p = Center(x, y);
                    float edge = shape.EdgeDistance(p);
                    int i = y * w + x;
                    if (edge < 6f)
                    {
                        baseCost[i] = float.PositiveInfinity;
                        continue;
                    }

                    // steepness of the smoothed ground (rise over run)
                    float hx = smooth.Sample(p + new Vector2(cell, 0f)) - smooth.Sample(p - new Vector2(cell, 0f));
                    float hz = smooth.Sample(p + new Vector2(0f, cell)) - smooth.Sample(p - new Vector2(0f, cell));
                    float slope = Mathf.Sqrt(hx * hx + hz * hz) / (2f * cell);
                    float slopeCost = s.slopeAvoidance * 0.5f * (slope / 0.15f) * (slope / 0.15f);

                    // Cheap channels follow the curvy zero lines of a noise field, so the cheapest route meanders along
                    // them. Where the "straight" mask is low the field is flat and the route runs straight.
                    float wanderNoise = ForestNoise.Fbm(p.x / s.wanderScale, p.y / s.wanderScale, wanderSeed, 3);
                    float winding = ForestNoise.Smooth(0.42f, 0.58f, ForestNoise.Fbm(p.x / 160f, p.y / 160f, straightSeed, 2) * 0.5f + 0.5f);
                    winding = Mathf.Lerp(1f, winding, s.straightRuns);
                    float wanderCost = s.wander * 7f * ForestNoise.Smooth(0f, 0.45f, Mathf.Abs(wanderNoise)) * winding;

                    float edgeCost = edge < 26f ? 4f * (1f - edge / 26f) * (1f - edge / 26f) : 0f;
                    baseCost[i] = 1f + slopeCost + wanderCost + edgeCost;
                }
            }
        }

        Vector2 Center(int x, int y)
        {
            return new Vector2(origin.x + x * cell, origin.y + y * cell);
        }

        int CellOf(Vector2 p)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt((p.x - origin.x) / cell), 0, w - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt((p.y - origin.y) / cell), 0, h - 1);
            return y * w + x;
        }

        float Cost(int i)
        {
            float c = baseCost[i];
            if (onTrail[i]) return c * ReuseFactor;
            return exempt[i] ? c : c * (1f + nearPenalty[i]);
        }

        /// <summary>Clearings are where trails are meant to meet: no penalty for running next to another trail there.</summary>
        public void MarkMeetingPoint(Vector2 center, float radius)
        {
            Stamp(center, center, radius, (i, d) => exempt[i] = true);
        }

        /// <summary>A route from a to b, as cell centres (with a and b themselves at the ends). Null if there is none.</summary>
        public List<Vector2> Route(Vector2 a, Vector2 b)
        {
            int start = CellOf(a), goal = CellOf(b);
            for (int k = 0; k < touched.Count; k++)
            {
                int t = touched[k];
                state[t] = 0;
            }
            touched.Clear();
            heap.Clear();

            // ends are allowed even if they sit in a blocked cell
            g[start] = 0f;
            cameFrom[start] = -1;
            state[start] = 1;
            touched.Add(start);
            Vector2 goalPos = Center(goal % w, goal / w);
            heap.Push(Heuristic(start, goalPos), start);

            bool found = false;
            while (heap.Count > 0)
            {
                int cur = heap.Pop();
                if (state[cur] == 2) continue;
                state[cur] = 2;
                if (cur == goal) { found = true; break; }

                int cx = cur % w, cy = cur / w;
                float cc = Cost(cur);
                if (float.IsInfinity(cc)) cc = 1f;     // only the start can be in a blocked cell
                for (int m = 0; m < MoveX.Length; m++)
                {
                    int dx = MoveX[m], dy = MoveY[m];
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (state[ni] == 2) continue;
                    float nc = ni == goal ? 1f : Cost(ni);
                    if (float.IsInfinity(nc)) continue;
                    float avg = 0.5f * (cc + nc);
                    if (Mathf.Abs(dx) + Mathf.Abs(dy) == 3)
                    {
                        // a knight move crosses two more cells: count them too
                        int mx = cx + (Mathf.Abs(dx) == 2 ? dx / 2 : 0), my = cy + (Mathf.Abs(dy) == 2 ? dy / 2 : 0);
                        float m1 = Cost(my * w + mx), m2 = Cost((my + (Mathf.Abs(dy) == 1 ? dy : 0)) * w + mx + (Mathf.Abs(dx) == 1 ? dx : 0));
                        if (float.IsInfinity(m1) || float.IsInfinity(m2)) continue;
                        avg = 0.25f * (cc + nc + m1 + m2);
                    }
                    float cand = g[cur] + MoveLength[m] * cell * avg;
                    if (state[ni] == 0 || cand < g[ni])
                    {
                        if (state[ni] == 0) touched.Add(ni);
                        state[ni] = 1;
                        g[ni] = cand;
                        cameFrom[ni] = cur;
                        heap.Push(cand + Heuristic(ni, goalPos), ni);
                    }
                }
            }
            if (!found) return null;

            var cells = new List<int>();
            for (int c = goal; c != -1; c = cameFrom[c]) cells.Add(c);
            cells.Reverse();
            List<int> corners = Simplify(cells);

            var pts = new List<Vector2>(corners.Count + 2);
            pts.Add(a);
            for (int k = 1; k < corners.Count - 1; k++) pts.Add(Center(corners[k] % w, corners[k] / w));
            pts.Add(b);
            return pts;
        }

        // 16 directions: the 8 neighbours plus knight moves, so routes are not limited to 45-degree steps.
        static readonly int[] MoveX = { 1, -1, 0, 0, 1, 1, -1, -1, 2, 2, -2, -2, 1, 1, -1, -1 };
        static readonly int[] MoveY = { 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 2, -2, 2, -2 };
        static readonly float[] MoveLength = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f,
                                               2.23606798f, 2.23606798f, 2.23606798f, 2.23606798f, 2.23606798f, 2.23606798f, 2.23606798f, 2.23606798f };

        /// <summary>
        /// Line-of-sight simplification: keeps only the corners of a cell route. A stretch is replaced by a straight line
        /// when walking that line costs no more than the route did (so straight runs come out truly straight at any angle,
        /// while bends forced by the cost field are kept).
        /// </summary>
        List<int> Simplify(List<int> cells)
        {
            var result = new List<int>();
            if (cells.Count < 3)
            {
                result.AddRange(cells);
                return result;
            }
            var along = new float[cells.Count];
            for (int k = 1; k < cells.Count; k++)
            {
                Vector2 p = Center(cells[k - 1] % w, cells[k - 1] / w), q = Center(cells[k] % w, cells[k] / w);
                float c1 = Cost(cells[k - 1]), c2 = Cost(cells[k]);
                if (float.IsInfinity(c1)) c1 = 1f;
                if (float.IsInfinity(c2)) c2 = 1f;
                along[k] = along[k - 1] + Vector2.Distance(p, q) * 0.5f * (c1 + c2);
            }

            int anchor = 0;
            result.Add(cells[0]);
            int j = 2;
            while (j < cells.Count)
            {
                float routeCost = along[j] - along[anchor];
                float lineCost = LineCost(cells[anchor], cells[j]);
                if (lineCost <= routeCost * 1.02f + 0.01f)
                {
                    j++;
                    continue;
                }
                anchor = j - 1;
                result.Add(cells[anchor]);
                j = anchor + 2;
            }
            result.Add(cells[cells.Count - 1]);
            return result;
        }

        float LineCost(int from, int to)
        {
            Vector2 a = Center(from % w, from / w), b = Center(to % w, to / w);
            float len = Vector2.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / (cell * 0.5f)));
            float sum = 0f;
            for (int s = 0; s < steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, (s + 0.5f) / steps);
                float c = Cost(CellOf(p));
                if (float.IsInfinity(c)) return float.PositiveInfinity;
                sum += c;
            }
            return sum / steps * len;
        }

        float Heuristic(int i, Vector2 goal)
        {
            return Vector2.Distance(Center(i % w, i / w), goal) * ReuseFactor * 1.4f;
        }

        /// <summary>Records a finished trail: walking along it gets cheap, walking next to it (closer than the separation) expensive.</summary>
        public void MarkTrail(List<Vector2> pts, float separation)
        {
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                Stamp(pts[k], pts[k + 1], separation, (i, d) =>
                {
                    if (d < cell * 0.75f) onTrail[i] = true;
                    else nearPenalty[i] = Mathf.Max(nearPenalty[i], 1.6f * (1f - d / separation));
                });
            }
        }

        void Stamp(Vector2 a, Vector2 b, float radius, System.Action<int, float> visit)
        {
            Vector2 lo = Vector2.Min(a, b) - Vector2.one * radius;
            Vector2 hi = Vector2.Max(a, b) + Vector2.one * radius;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((lo.x - origin.x) / cell));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt((hi.x - origin.x) / cell));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((lo.y - origin.y) / cell));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt((hi.y - origin.y) / cell));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float d = DistanceToSegment(Center(x, y), a, b);
                    if (d <= radius) visit(y * w + x, d);
                }
            }
        }

        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        // ---------------------------------------------------------------- polyline helpers

        /// <summary>Chaikin corner cutting: rounds the staircase of grid cells into a smooth curve. The ends stay put.</summary>
        public static List<Vector2> Smooth(List<Vector2> pts, int iterations)
        {
            List<Vector2> cur = pts;
            for (int it = 0; it < iterations; it++)
            {
                if (cur.Count < 3) return cur;
                var next = new List<Vector2>(cur.Count * 2);
                next.Add(cur[0]);
                for (int i = 0; i + 1 < cur.Count; i++)
                {
                    Vector2 p = cur[i], q = cur[i + 1];
                    if (i > 0) next.Add(Vector2.Lerp(p, q, 0.25f));
                    if (i + 2 < cur.Count) next.Add(Vector2.Lerp(p, q, 0.75f));
                }
                next.Add(cur[cur.Count - 1]);
                cur = next;
            }
            return cur;
        }

        /// <summary>Evenly spaced points along a polyline.</summary>
        public static List<Vector2> Resample(List<Vector2> pts, float spacing, out float length)
        {
            length = 0f;
            for (int i = 0; i + 1 < pts.Count; i++) length += Vector2.Distance(pts[i], pts[i + 1]);
            int count = Mathf.Max(2, Mathf.CeilToInt(length / spacing) + 1);
            float step = length / (count - 1);
            var outPts = new List<Vector2>(count);
            outPts.Add(pts[0]);
            int seg = 0;
            float segStart = 0f;
            float segLen = pts.Count > 1 ? Vector2.Distance(pts[0], pts[1]) : 0f;
            for (int k = 1; k < count - 1; k++)
            {
                float target = k * step;
                while (seg < pts.Count - 2 && segStart + segLen < target)
                {
                    segStart += segLen;
                    seg++;
                    segLen = Vector2.Distance(pts[seg], pts[seg + 1]);
                }
                float t = segLen > 1e-6f ? (target - segStart) / segLen : 0f;
                outPts.Add(Vector2.Lerp(pts[seg], pts[seg + 1], Mathf.Clamp01(t)));
            }
            outPts.Add(pts[pts.Count - 1]);
            return outPts;
        }

        /// <summary>Simple binary min-heap of (priority, cell). Duplicates are allowed; stale entries are skipped by the caller.</summary>
        sealed class MinHeap
        {
            float[] keys;
            int[] vals;
            public int Count { get; private set; }

            public MinHeap(int capacity)
            {
                keys = new float[capacity];
                vals = new int[capacity];
            }

            public void Clear()
            {
                Count = 0;
            }

            public void Push(float key, int val)
            {
                if (Count == keys.Length)
                {
                    System.Array.Resize(ref keys, keys.Length * 2);
                    System.Array.Resize(ref vals, vals.Length * 2);
                }
                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (keys[parent] <= key) break;
                    keys[i] = keys[parent];
                    vals[i] = vals[parent];
                    i = parent;
                }
                keys[i] = key;
                vals[i] = val;
            }

            public int Pop()
            {
                int result = vals[0];
                Count--;
                if (Count > 0)
                {
                    float key = keys[Count];
                    int val = vals[Count];
                    int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1;
                        if (l >= Count) break;
                        int r = l + 1;
                        int m = r < Count && keys[r] < keys[l] ? r : l;
                        if (keys[m] >= key) break;
                        keys[i] = keys[m];
                        vals[i] = vals[m];
                        i = m;
                    }
                    keys[i] = key;
                    vals[i] = val;
                }
                return result;
            }
        }
    }
}
