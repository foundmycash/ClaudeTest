using System;
using System.Threading;

namespace HavocCave.Lightmapping
{
    /// <summary>
    /// Bounding volume hierarchy over a triangle soup, built with binned SAH and traversed with plain float math
    /// (no Vector3 in the hot loops: it is much faster on Mono and IL2CPP). Triangles are two sided.
    /// Read only after Build, so any number of threads can trace against it at once, each with its own stack.
    /// </summary>
    internal sealed class LmBvh
    {
        public const int StackSize = 128;

        /// <summary>
        /// Pass as an ignore id to ignore nothing. The cave itself is object -1, so -1 would make a ray skip the cave.
        /// </summary>
        public const int NoObject = -2;

        const int MaxLeaf = 6;
        const int Bins = 16;

        float[] bounds;     // 6 per node: min xyz, max xyz
        int[] meta;         // 3 per node: leaf = first slot, interior = left child (right = left + 1); count (0 for interior); split axis
        float[] tri;        // 9 per slot: v0, e1 = v1 - v0, e2 = v2 - v0
        int[] slotTri;      // slot -> triangle index the caller passed in
        int[] slotObj;      // slot -> object id (lets a light ignore the fixture it hangs in)
        int nodeCount;

        public int TriangleCount { get; private set; }

        public int TriangleOfSlot(int slot) { return slotTri[slot]; }

        /// <summary>pos holds 9 floats per triangle (three corners). obj may be null (every triangle is object -1).</summary>
        public static LmBvh Build(float[] pos, int[] obj, int n, CancellationToken token)
        {
            var bvh = new LmBvh();
            bvh.TriangleCount = n;
            bvh.tri = new float[n * 9];
            bvh.slotTri = new int[n];
            bvh.slotObj = new int[n];
            if (n == 0)
            {
                bvh.bounds = new float[6];
                bvh.meta = new int[3];
                bvh.nodeCount = 0;
                return bvh;
            }

            var tmin = new float[n * 3];
            var tmax = new float[n * 3];
            var cen = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                int o = i * 9;
                for (int a = 0; a < 3; a++)
                {
                    float p0 = pos[o + a], p1 = pos[o + 3 + a], p2 = pos[o + 6 + a];
                    float lo = Math.Min(p0, Math.Min(p1, p2));
                    float hi = Math.Max(p0, Math.Max(p1, p2));
                    tmin[i * 3 + a] = lo;
                    tmax[i * 3 + a] = hi;
                    cen[i * 3 + a] = (lo + hi) * 0.5f;
                }
            }

            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;

            int maxNodes = 2 * n + 1;
            float[] b = bvh.bounds = new float[maxNodes * 6];
            int[] m = bvh.meta = new int[maxNodes * 3];
            int nodes = 1;

            var binMin = new float[Bins * 3];
            var binMax = new float[Bins * 3];
            var binCnt = new int[Bins];
            var leftArea = new float[Bins];
            var leftCnt = new int[Bins];

            // pending work: node, start, count, depth
            int[] stack = new int[4 * 256];
            int sp = 0;
            stack[sp++] = 0; stack[sp++] = 0; stack[sp++] = n; stack[sp++] = 0;

            int guard = 0;
            while (sp > 0)
            {
                int depth = stack[--sp];
                int cnt = stack[--sp];
                int start = stack[--sp];
                int node = stack[--sp];
                if ((++guard & 0x3FFF) == 0 && token.IsCancellationRequested) throw new OperationCanceledException(token);

                // bounds of this node
                float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
                float x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
                float cx0 = float.MaxValue, cy0 = float.MaxValue, cz0 = float.MaxValue;
                float cx1 = float.MinValue, cy1 = float.MinValue, cz1 = float.MinValue;
                for (int i = start; i < start + cnt; i++)
                {
                    int t = idx[i] * 3;
                    if (tmin[t] < x0) x0 = tmin[t];
                    if (tmin[t + 1] < y0) y0 = tmin[t + 1];
                    if (tmin[t + 2] < z0) z0 = tmin[t + 2];
                    if (tmax[t] > x1) x1 = tmax[t];
                    if (tmax[t + 1] > y1) y1 = tmax[t + 1];
                    if (tmax[t + 2] > z1) z1 = tmax[t + 2];
                    float cx = cen[t], cy = cen[t + 1], cz = cen[t + 2];
                    if (cx < cx0) cx0 = cx;
                    if (cy < cy0) cy0 = cy;
                    if (cz < cz0) cz0 = cz;
                    if (cx > cx1) cx1 = cx;
                    if (cy > cy1) cy1 = cy;
                    if (cz > cz1) cz1 = cz;
                }
                int bo = node * 6;
                b[bo] = x0; b[bo + 1] = y0; b[bo + 2] = z0;
                b[bo + 3] = x1; b[bo + 4] = y1; b[bo + 5] = z1;
                int mo = node * 3;

                if (cnt <= 2)
                {
                    m[mo] = start; m[mo + 1] = cnt; m[mo + 2] = 0;
                    continue;
                }

                // split axis: the longest side of the centroid box
                float ex = cx1 - cx0, ey = cy1 - cy0, ez = cz1 - cz0;
                int axis = 0;
                float ext = ex;
                float cmin = cx0;
                if (ey > ext) { axis = 1; ext = ey; cmin = cy0; }
                if (ez > ext) { axis = 2; ext = ez; cmin = cz0; }

                int leftCount;
                bool median = depth >= 48;          // too deep: stop being clever
                if (ext <= 1e-7f)
                {
                    // Every centroid is in the same spot: nothing to sort by.
                    if (cnt <= 16)
                    {
                        m[mo] = start; m[mo + 1] = cnt; m[mo + 2] = 0;
                        continue;
                    }
                    leftCount = cnt / 2;
                    median = true;
                }
                else
                {
                    float scale = Bins / ext;
                    Array.Clear(binCnt, 0, Bins);
                    for (int k = 0; k < Bins; k++)
                    {
                        binMin[k * 3] = binMin[k * 3 + 1] = binMin[k * 3 + 2] = float.MaxValue;
                        binMax[k * 3] = binMax[k * 3 + 1] = binMax[k * 3 + 2] = float.MinValue;
                    }
                    for (int i = start; i < start + cnt; i++)
                    {
                        int t = idx[i] * 3;
                        int k = (int)((cen[t + axis] - cmin) * scale);
                        if (k >= Bins) k = Bins - 1;
                        if (k < 0) k = 0;
                        binCnt[k]++;
                        for (int a = 0; a < 3; a++)
                        {
                            if (tmin[t + a] < binMin[k * 3 + a]) binMin[k * 3 + a] = tmin[t + a];
                            if (tmax[t + a] > binMax[k * 3 + a]) binMax[k * 3 + a] = tmax[t + a];
                        }
                    }

                    // sweep: area and count of everything left of each split
                    float ax0 = float.MaxValue, ay0 = float.MaxValue, az0 = float.MaxValue;
                    float ax1 = float.MinValue, ay1 = float.MinValue, az1 = float.MinValue;
                    int acc = 0;
                    for (int k = 0; k < Bins - 1; k++)
                    {
                        if (binCnt[k] > 0)
                        {
                            ax0 = Math.Min(ax0, binMin[k * 3]); ay0 = Math.Min(ay0, binMin[k * 3 + 1]); az0 = Math.Min(az0, binMin[k * 3 + 2]);
                            ax1 = Math.Max(ax1, binMax[k * 3]); ay1 = Math.Max(ay1, binMax[k * 3 + 1]); az1 = Math.Max(az1, binMax[k * 3 + 2]);
                            acc += binCnt[k];
                        }
                        leftCnt[k] = acc;
                        leftArea[k] = acc > 0 ? Area(ax1 - ax0, ay1 - ay0, az1 - az0) : 0f;
                    }

                    float bestCost = float.MaxValue;
                    int bestSplit = -1;
                    ax0 = ay0 = az0 = float.MaxValue;
                    ax1 = ay1 = az1 = float.MinValue;
                    acc = 0;
                    for (int k = Bins - 1; k >= 1; k--)
                    {
                        if (binCnt[k] > 0)
                        {
                            ax0 = Math.Min(ax0, binMin[k * 3]); ay0 = Math.Min(ay0, binMin[k * 3 + 1]); az0 = Math.Min(az0, binMin[k * 3 + 2]);
                            ax1 = Math.Max(ax1, binMax[k * 3]); ay1 = Math.Max(ay1, binMax[k * 3 + 1]); az1 = Math.Max(az1, binMax[k * 3 + 2]);
                            acc += binCnt[k];
                        }
                        int l = leftCnt[k - 1];
                        if (l == 0 || acc == 0) continue;
                        float cost = l * leftArea[k - 1] + acc * Area(ax1 - ax0, ay1 - ay0, az1 - az0);
                        if (cost < bestCost) { bestCost = cost; bestSplit = k - 1; }
                    }

                    float nodeArea = Area(x1 - x0, y1 - y0, z1 - z0);
                    if (bestSplit < 0)
                    {
                        leftCount = cnt / 2;
                        median = true;
                    }
                    else
                    {
                        float splitCost = 1f + bestCost / Math.Max(nodeArea, 1e-12f);
                        if (cnt <= MaxLeaf && splitCost >= cnt)
                        {
                            m[mo] = start; m[mo + 1] = cnt; m[mo + 2] = 0;
                            continue;
                        }

                        // partition around the chosen bin boundary
                        int lo = start, hi = start + cnt - 1;
                        while (lo <= hi)
                        {
                            int k = (int)((cen[idx[lo] * 3 + axis] - cmin) * scale);
                            if (k >= Bins) k = Bins - 1;
                            if (k <= bestSplit) lo++;
                            else { int tmp = idx[lo]; idx[lo] = idx[hi]; idx[hi] = tmp; hi--; }
                        }
                        leftCount = lo - start;
                    }
                }

                if (median || leftCount <= 0 || leftCount >= cnt)
                {
                    // Fall back to a median split: sort the range along the axis and cut it in half.
                    var keys = new float[cnt];
                    var items = new int[cnt];
                    for (int i = 0; i < cnt; i++) { items[i] = idx[start + i]; keys[i] = cen[items[i] * 3 + axis]; }
                    Array.Sort(keys, items);
                    Array.Copy(items, 0, idx, start, cnt);
                    leftCount = cnt / 2;
                }

                int left = nodes;
                nodes += 2;
                m[mo] = left; m[mo + 1] = 0; m[mo + 2] = axis;

                if (sp + 8 > stack.Length) Array.Resize(ref stack, stack.Length * 2);
                stack[sp++] = left + 1; stack[sp++] = start + leftCount; stack[sp++] = cnt - leftCount; stack[sp++] = depth + 1;
                stack[sp++] = left; stack[sp++] = start; stack[sp++] = leftCount; stack[sp++] = depth + 1;
            }
            bvh.nodeCount = nodes;

            // Triangles in leaf order, stored as origin + two edges for the intersection test.
            float[] tr = bvh.tri;
            for (int s = 0; s < n; s++)
            {
                int t = idx[s];
                int o = t * 9;
                int d = s * 9;
                tr[d] = pos[o]; tr[d + 1] = pos[o + 1]; tr[d + 2] = pos[o + 2];
                tr[d + 3] = pos[o + 3] - pos[o]; tr[d + 4] = pos[o + 4] - pos[o + 1]; tr[d + 5] = pos[o + 5] - pos[o + 2];
                tr[d + 6] = pos[o + 6] - pos[o]; tr[d + 7] = pos[o + 7] - pos[o + 1]; tr[d + 8] = pos[o + 8] - pos[o + 2];
                bvh.slotTri[s] = t;
                bvh.slotObj[s] = obj != null ? obj[t] : -1;
            }
            return bvh;
        }

        static float Area(float dx, float dy, float dz)
        {
            return dx * dy + dy * dz + dz * dx;
        }

        static float Inv(float d)
        {
            if (d > -1e-20f && d < 1e-20f) return d < 0f ? -1e20f : 1e20f;
            return 1f / d;
        }

        /// <summary>
        /// Nearest hit along the ray with tMin &lt; t &lt; tMax. On a hit, slot is the BVH slot (use TriangleOfSlot to get the
        /// caller's triangle) and (hu, hv) are the barycentric weights of corners 1 and 2.
        /// </summary>
        public bool Closest(float ox, float oy, float oz, float dx, float dy, float dz, float tMin, float tMax, int[] stack,
                            out float tHit, out int slot, out float hu, out float hv)
        {
            return Closest(ox, oy, oz, dx, dy, dz, tMin, tMax, NoObject, stack, out tHit, out slot, out hu, out hv);
        }

        /// <summary>Same, but triangles of object ignoreObj are skipped (a probe inside a prop must not hit that prop).</summary>
        public bool Closest(float ox, float oy, float oz, float dx, float dy, float dz, float tMin, float tMax, int ignoreObj, int[] stack,
                            out float tHit, out int slot, out float hu, out float hv)
        {
            tHit = tMax;
            slot = -1;
            hu = 0f;
            hv = 0f;
            if (nodeCount == 0) return false;

            float ix = Inv(dx), iy = Inv(dy), iz = Inv(dz);
            bool negX = dx < 0f, negY = dy < 0f, negZ = dz < 0f;
            float[] bd = bounds;
            int[] mt = meta;
            float[] tr = tri;
            int[] so = slotObj;

            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int node = stack[--sp];
                int bo = node * 6;

                float t0 = (bd[bo] - ox) * ix, t1 = (bd[bo + 3] - ox) * ix;
                float tn = t0 < t1 ? t0 : t1;
                float tf = t0 < t1 ? t1 : t0;
                t0 = (bd[bo + 1] - oy) * iy; t1 = (bd[bo + 4] - oy) * iy;
                float a = t0 < t1 ? t0 : t1, c = t0 < t1 ? t1 : t0;
                if (a > tn) tn = a;
                if (c < tf) tf = c;
                t0 = (bd[bo + 2] - oz) * iz; t1 = (bd[bo + 5] - oz) * iz;
                a = t0 < t1 ? t0 : t1; c = t0 < t1 ? t1 : t0;
                if (a > tn) tn = a;
                if (c < tf) tf = c;
                if (tn > tf || tf < tMin || tn > tHit) continue;

                int mo = node * 3;
                int cnt = mt[mo + 1];
                if (cnt > 0)
                {
                    int first = mt[mo];
                    for (int s = first; s < first + cnt; s++)
                    {
                        if (so[s] == ignoreObj) continue;
                        int to = s * 9;
                        float e1x = tr[to + 3], e1y = tr[to + 4], e1z = tr[to + 5];
                        float e2x = tr[to + 6], e2y = tr[to + 7], e2z = tr[to + 8];
                        float px = dy * e2z - dz * e2y;
                        float py = dz * e2x - dx * e2z;
                        float pz = dx * e2y - dy * e2x;
                        float det = e1x * px + e1y * py + e1z * pz;
                        if (det > -1e-12f && det < 1e-12f) continue;
                        float inv = 1f / det;
                        float sx = ox - tr[to], sy = oy - tr[to + 1], sz = oz - tr[to + 2];
                        float u = (sx * px + sy * py + sz * pz) * inv;
                        if (u < -1e-5f || u > 1.00001f) continue;
                        float qx = sy * e1z - sz * e1y;
                        float qy = sz * e1x - sx * e1z;
                        float qz = sx * e1y - sy * e1x;
                        float v = (dx * qx + dy * qy + dz * qz) * inv;
                        if (v < -1e-5f || u + v > 1.00001f) continue;
                        float t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                        if (t > tMin && t < tHit)
                        {
                            tHit = t;
                            slot = s;
                            hu = u;
                            hv = v;
                        }
                    }
                }
                else
                {
                    int left = mt[mo];
                    int axis = mt[mo + 2];
                    bool neg = axis == 0 ? negX : (axis == 1 ? negY : negZ);
                    if (sp + 2 > StackSize) continue;
                    if (neg) { stack[sp++] = left; stack[sp++] = left + 1; }
                    else { stack[sp++] = left + 1; stack[sp++] = left; }
                }
            }
            return slot >= 0;
        }

        /// <summary>True if anything blocks the ray before tMax. Triangles of object ignoreObj don't count.</summary>
        public bool Occluded(float ox, float oy, float oz, float dx, float dy, float dz, float tMin, float tMax, int ignoreObj, int[] stack)
        {
            return Occluded(ox, oy, oz, dx, dy, dz, tMin, tMax, ignoreObj, NoObject, stack);
        }

        /// <summary>Same, ignoring two objects (a lamp's fixture and the prop a probe sits in).</summary>
        public bool Occluded(float ox, float oy, float oz, float dx, float dy, float dz, float tMin, float tMax, int ignoreObj, int ignoreObj2, int[] stack)
        {
            if (nodeCount == 0) return false;

            float ix = Inv(dx), iy = Inv(dy), iz = Inv(dz);
            bool negX = dx < 0f, negY = dy < 0f, negZ = dz < 0f;
            float[] bd = bounds;
            int[] mt = meta;
            float[] tr = tri;
            int[] so = slotObj;

            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int node = stack[--sp];
                int bo = node * 6;

                float t0 = (bd[bo] - ox) * ix, t1 = (bd[bo + 3] - ox) * ix;
                float tn = t0 < t1 ? t0 : t1;
                float tf = t0 < t1 ? t1 : t0;
                t0 = (bd[bo + 1] - oy) * iy; t1 = (bd[bo + 4] - oy) * iy;
                float a = t0 < t1 ? t0 : t1, c = t0 < t1 ? t1 : t0;
                if (a > tn) tn = a;
                if (c < tf) tf = c;
                t0 = (bd[bo + 2] - oz) * iz; t1 = (bd[bo + 5] - oz) * iz;
                a = t0 < t1 ? t0 : t1; c = t0 < t1 ? t1 : t0;
                if (a > tn) tn = a;
                if (c < tf) tf = c;
                if (tn > tf || tf < tMin || tn > tMax) continue;

                int mo = node * 3;
                int cnt = mt[mo + 1];
                if (cnt > 0)
                {
                    int first = mt[mo];
                    for (int s = first; s < first + cnt; s++)
                    {
                        int obj = so[s];
                        if (obj == ignoreObj || obj == ignoreObj2) continue;
                        int to = s * 9;
                        float e1x = tr[to + 3], e1y = tr[to + 4], e1z = tr[to + 5];
                        float e2x = tr[to + 6], e2y = tr[to + 7], e2z = tr[to + 8];
                        float px = dy * e2z - dz * e2y;
                        float py = dz * e2x - dx * e2z;
                        float pz = dx * e2y - dy * e2x;
                        float det = e1x * px + e1y * py + e1z * pz;
                        if (det > -1e-12f && det < 1e-12f) continue;
                        float inv = 1f / det;
                        float sx = ox - tr[to], sy = oy - tr[to + 1], sz = oz - tr[to + 2];
                        float u = (sx * px + sy * py + sz * pz) * inv;
                        if (u < -1e-5f || u > 1.00001f) continue;
                        float qx = sy * e1z - sz * e1y;
                        float qy = sz * e1x - sx * e1z;
                        float qz = sx * e1y - sy * e1x;
                        float v = (dx * qx + dy * qy + dz * qz) * inv;
                        if (v < -1e-5f || u + v > 1.00001f) continue;
                        float t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                        if (t > tMin && t < tMax) return true;
                    }
                }
                else
                {
                    int left = mt[mo];
                    int axis = mt[mo + 2];
                    bool neg = axis == 0 ? negX : (axis == 1 ? negY : negZ);
                    if (sp + 2 > StackSize) continue;
                    if (neg) { stack[sp++] = left; stack[sp++] = left + 1; }
                    else { stack[sp++] = left + 1; stack[sp++] = left; }
                }
            }
            return false;
        }
    }
}
