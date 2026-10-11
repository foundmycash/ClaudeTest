using System;
using System.Threading;
using System.Threading.Tasks;

namespace HavocCave.Lightmapping
{
    /// <summary>
    /// What happens to the raw tracer output: the noisy indirect light is smoothed (never across a chart border or round a
    /// corner), the dominant light direction is finished, the colour is spread a few texels past every chart's edge so
    /// bilinear filtering never reads empty texels, and everything is packed into the texture formats.
    /// </summary>
    internal static class LmPost
    {
        /// <summary>Cross bilateral filter. Texels only average with texels of the same chart that face about the same way.</summary>
        public static void Denoise(LmAtlas atlas, int[] triChart, sbyte[] normal, float[] indirect, float[] dirIndirect,
                                   int radius, int threads, CancellationToken token, out float[] indirectOut, out float[] dirOut)
        {
            int n = atlas.w * atlas.h;
            if (radius <= 0)
            {
                indirectOut = indirect;
                dirOut = dirIndirect;
                return;
            }

            indirectOut = new float[n * 3];
            dirOut = new float[n * 3];
            int span = radius * 2 + 1;
            var gauss = new float[span * span];
            float sigma = Math.Max(0.6f, radius * 0.5f);
            for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                    gauss[(y + radius) * span + x + radius] = (float)Math.Exp(-(x * x + y * y) / (2f * sigma * sigma));

            const int Block = 256;
            int blocks = (atlas.validCount + Block - 1) / Block;
            int w = atlas.w, h = atlas.h;
            int[] tri = atlas.tri;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = token };
            float[] outInd = indirectOut, outDir = dirOut;

            Parallel.For(0, blocks, opts, b =>
            {
                int from = b * Block;
                int to = Math.Min(atlas.validCount, from + Block);
                for (int i = from; i < to; i++)
                {
                    int idx = atlas.valid[i];
                    int cx = idx % w, cy = idx / w;
                    int chart = triChart[tri[idx]];
                    float nx = normal[idx * 3] * (1f / 127f), ny = normal[idx * 3 + 1] * (1f / 127f), nz = normal[idx * 3 + 2] * (1f / 127f);

                    float sr = 0f, sg = 0f, sb = 0f, dx = 0f, dy = 0f, dz = 0f, sw = 0f;
                    int y0 = Math.Max(0, cy - radius), y1 = Math.Min(h - 1, cy + radius);
                    int x0 = Math.Max(0, cx - radius), x1 = Math.Min(w - 1, cx + radius);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int j = y * w + x;
                            int tj = tri[j];
                            if (tj < 0 || triChart[tj] != chart) continue;
                            float nd = nx * normal[j * 3] * (1f / 127f) + ny * normal[j * 3 + 1] * (1f / 127f) + nz * normal[j * 3 + 2] * (1f / 127f);
                            if (nd <= 0.5f) continue;
                            float nd2 = nd * nd, nd4 = nd2 * nd2;
                            float wgt = gauss[(y - cy + radius) * span + x - cx + radius] * nd4 * nd4;
                            sr += indirect[j * 3] * wgt; sg += indirect[j * 3 + 1] * wgt; sb += indirect[j * 3 + 2] * wgt;
                            dx += dirIndirect[j * 3] * wgt; dy += dirIndirect[j * 3 + 1] * wgt; dz += dirIndirect[j * 3 + 2] * wgt;
                            sw += wgt;
                        }

                    if (sw > 1e-9f)
                    {
                        float inv = 1f / sw;
                        outInd[idx * 3] = sr * inv; outInd[idx * 3 + 1] = sg * inv; outInd[idx * 3 + 2] = sb * inv;
                        outDir[idx * 3] = dx * inv; outDir[idx * 3 + 1] = dy * inv; outDir[idx * 3 + 2] = dz * inv;
                    }
                    else
                    {
                        outInd[idx * 3] = indirect[idx * 3]; outInd[idx * 3 + 1] = indirect[idx * 3 + 1]; outInd[idx * 3 + 2] = indirect[idx * 3 + 2];
                        outDir[idx * 3] = dirIndirect[idx * 3]; outDir[idx * 3 + 1] = dirIndirect[idx * 3 + 1]; outDir[idx * 3 + 2] = dirIndirect[idx * 3 + 2];
                    }
                }
            });
        }

        /// <summary>
        /// Adds direct and indirect, finishes the direction map and spreads the result over the empty texels next to charts.
        /// Returns colour (3 per texel) and direction (4 per texel: unit direction and the half lambert the baked normal
        /// has against it, which the shader divides out when it applies the normal map).
        /// </summary>
        public static void Finish(LmAtlas atlas, sbyte[] normal, float[] direct, float[] indirect, float[] dirDirect, float[] dirIndirect,
                                  bool directional, int dilate, int threads, CancellationToken token, out float[] color, out float[] dir)
        {
            int w = atlas.w, h = atlas.h, n = w * h;
            color = direct;                         // finished in place: the direct light is the start of the final colour
            dir = directional ? new float[n * 4] : null;
            var state = new byte[n];                // 0 empty, 1 valid, 2 filled in this pass
            float[] col = color, dr = dir;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = token };

            const int Block = 1024;
            int blocks = (atlas.validCount + Block - 1) / Block;
            Parallel.For(0, blocks, opts, b =>
            {
                int from = b * Block;
                int to = Math.Min(atlas.validCount, from + Block);
                for (int i = from; i < to; i++)
                {
                    int idx = atlas.valid[i];
                    state[idx] = 1;
                    col[idx * 3] += indirect[idx * 3];
                    col[idx * 3 + 1] += indirect[idx * 3 + 1];
                    col[idx * 3 + 2] += indirect[idx * 3 + 2];
                    if (!directional) continue;

                    float nx = normal[idx * 3] * (1f / 127f), ny = normal[idx * 3 + 1] * (1f / 127f), nz = normal[idx * 3 + 2] * (1f / 127f);
                    float vx = dirDirect[idx * 3] + dirIndirect[idx * 3];
                    float vy = dirDirect[idx * 3 + 1] + dirIndirect[idx * 3 + 1];
                    float vz = dirDirect[idx * 3 + 2] + dirIndirect[idx * 3 + 2];
                    float len = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
                    if (len < 1e-8f) { vx = nx; vy = ny; vz = nz; len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz); }
                    if (len < 1e-8f) { vx = 0f; vy = 1f; vz = 0f; len = 1f; }
                    vx /= len; vy /= len; vz /= len;
                    float hl = (nx * vx + ny * vy + nz * vz) * 0.5f + 0.5f;
                    if (hl < 0.1f) hl = 0.1f; else if (hl > 1f) hl = 1f;
                    dr[idx * 4] = vx; dr[idx * 4 + 1] = vy; dr[idx * 4 + 2] = vz; dr[idx * 4 + 3] = hl;
                }
            });

            for (int pass = 0; pass < dilate; pass++)
            {
                Parallel.For(0, h, opts, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (state[idx] != 0) continue;
                        float r = 0f, g = 0f, bl = 0f, d0 = 0f, d1 = 0f, d2 = 0f, d3 = 0f;
                        int cnt = 0;
                        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
                            {
                                int j = yy * w + xx;
                                if (state[j] != 1) continue;
                                r += col[j * 3]; g += col[j * 3 + 1]; bl += col[j * 3 + 2];
                                if (dr != null) { d0 += dr[j * 4]; d1 += dr[j * 4 + 1]; d2 += dr[j * 4 + 2]; d3 += dr[j * 4 + 3]; }
                                cnt++;
                            }
                        if (cnt == 0) continue;
                        float inv = 1f / cnt;
                        col[idx * 3] = r * inv; col[idx * 3 + 1] = g * inv; col[idx * 3 + 2] = bl * inv;
                        if (dr != null)
                        {
                            float len = (float)Math.Sqrt(d0 * d0 + d1 * d1 + d2 * d2);
                            if (len < 1e-6f) len = 1f;
                            dr[idx * 4] = d0 / len; dr[idx * 4 + 1] = d1 / len; dr[idx * 4 + 2] = d2 / len; dr[idx * 4 + 3] = d3 * inv;
                        }
                        state[idx] = 2;
                    }
                });
                for (int i = 0; i < n; i++) if (state[i] == 2) state[i] = 1;
            }
        }

        public static uint[] EncodeColor(float[] color, int texels)
        {
            var packed = new uint[texels];
            Parallel.For(0, (texels + 4095) / 4096, b =>
            {
                int from = b * 4096, to = Math.Min(texels, from + 4096);
                for (int i = from; i < to; i++) packed[i] = LmMath.PackRgb9e5(color[i * 3], color[i * 3 + 1], color[i * 3 + 2]);
            });
            return packed;
        }

        public static uint[] EncodeDirection(float[] dir, int texels)
        {
            var packed = new uint[texels];
            Parallel.For(0, (texels + 4095) / 4096, b =>
            {
                int from = b * 4096, to = Math.Min(texels, from + 4096);
                for (int i = from; i < to; i++)
                {
                    uint x = Byte(dir[i * 4] * 0.5f + 0.5f), y = Byte(dir[i * 4 + 1] * 0.5f + 0.5f);
                    uint z = Byte(dir[i * 4 + 2] * 0.5f + 0.5f), a = Byte(dir[i * 4 + 3]);
                    packed[i] = x | (y << 8) | (z << 16) | (a << 24);
                }
            });
            return packed;
        }

        static uint Byte(float v)
        {
            if (!(v > 0f)) return 0u;
            if (v >= 1f) return 255u;
            return (uint)(v * 255f + 0.5f);
        }
    }
}
