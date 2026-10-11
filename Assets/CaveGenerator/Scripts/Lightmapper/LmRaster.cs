using System;

namespace HavocCave.Lightmapping
{
    /// <summary>
    /// For every texel of the lightmap: which triangle covers it and where in that triangle. A texel is covered when its
    /// centre is inside a triangle, or (so bilinear filtering at a chart's edge still reads real surface) when it is
    /// within about one texel of one, in which case it is snapped to the nearest point of that triangle.
    /// </summary>
    internal sealed class LmAtlas
    {
        public readonly int w, h;
        public readonly int[] tri;          // global triangle index, -1 = empty
        public readonly float[] b1, b2;     // barycentric weights of corners 1 and 2
        public float[] dist;                // 0 = centre inside the triangle, otherwise how far it was snapped (texels). Released once rasterising is done.
        public int[] valid;                 // indices of the covered texels
        public int validCount;

        public LmAtlas(int w, int h)
        {
            this.w = w;
            this.h = h;
            int n = w * h;
            tri = new int[n];
            b1 = new float[n];
            b2 = new float[n];
            dist = new float[n];
            for (int i = 0; i < n; i++) { tri[i] = -1; dist[i] = float.MaxValue; }
        }

        /// <summary>Writes one triangle (corners in texel coordinates). Triangles of different charts never share texels, so this is safe to call from several threads for different charts.</summary>
        public void Rasterize(int triIndex, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float area2 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area2) < 1e-9f) return;
            float invArea = 1f / area2;

            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)) - 1f));
            int x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)) + 1f));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)) - 1f));
            int y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy)) + 1f));

            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float u = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * invArea;     // weight of A
                    float v = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * invArea;     // weight of B
                    float wgt = 1f - u - v;                                                  // weight of C
                    float d;
                    float wb, wc;
                    if (u >= -1e-4f && v >= -1e-4f && wgt >= -1e-4f)
                    {
                        d = 0f;
                        wb = v < 0f ? 0f : v;
                        wc = wgt < 0f ? 0f : wgt;
                    }
                    else
                    {
                        float cu, cv, cw;
                        ClosestBarycentric(px, py, ax, ay, bx, by, cx, cy, out cu, out cv, out cw);
                        float qx = ax * cu + bx * cv + cx * cw - px;
                        float qy = ay * cu + by * cv + cy * cw - py;
                        float d2 = qx * qx + qy * qy;
                        if (d2 > 0.5f) continue;
                        d = (float)Math.Sqrt(d2);
                        wb = cv;
                        wc = cw;
                    }

                    int idx = y * w + x;
                    if (d < dist[idx])
                    {
                        dist[idx] = d;
                        tri[idx] = triIndex;
                        b1[idx] = wb;
                        b2[idx] = wc;
                    }
                }
        }

        public void CollectValid()
        {
            dist = null;        // only needed while triangles are being written
            int n = w * h;
            int count = 0;
            for (int i = 0; i < n; i++) if (tri[i] >= 0) count++;
            valid = new int[count];
            int k = 0;
            for (int i = 0; i < n; i++) if (tri[i] >= 0) valid[k++] = i;
            validCount = count;
        }

        /// <summary>Barycentric weights (of A, B, C) of the point on triangle ABC that is closest to P. All in 2D.</summary>
        static void ClosestBarycentric(float px, float py, float ax, float ay, float bx, float by, float cx, float cy,
                                       out float wa, out float wb, out float wc)
        {
            float abx = bx - ax, aby = by - ay, acx = cx - ax, acy = cy - ay;
            float apx = px - ax, apy = py - ay;
            float d1 = abx * apx + aby * apy, d2 = acx * apx + acy * apy;
            if (d1 <= 0f && d2 <= 0f) { wa = 1f; wb = 0f; wc = 0f; return; }

            float bpx = px - bx, bpy = py - by;
            float d3 = abx * bpx + aby * bpy, d4 = acx * bpx + acy * bpy;
            if (d3 >= 0f && d4 <= d3) { wa = 0f; wb = 1f; wc = 0f; return; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float t = d1 / (d1 - d3);
                wa = 1f - t; wb = t; wc = 0f;
                return;
            }

            float cpx = px - cx, cpy = py - cy;
            float d5 = abx * cpx + aby * cpy, d6 = acx * cpx + acy * cpy;
            if (d6 >= 0f && d5 <= d6) { wa = 0f; wb = 0f; wc = 1f; return; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float t = d2 / (d2 - d6);
                wa = 1f - t; wb = 0f; wc = t;
                return;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                wa = 0f; wb = 1f - t; wc = t;
                return;
            }

            float den = 1f / (va + vb + vc);
            wb = vb * den;
            wc = vc * den;
            wa = 1f - wb - wc;
        }
    }
}
