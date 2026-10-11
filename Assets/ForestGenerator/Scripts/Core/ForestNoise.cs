using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Deterministic 2D noise. It uses no Unity noise calls and no mutable static state, so it is safe on worker threads and
    /// gives the same result on every platform. The Periodic variants repeat every <c>period</c> units, for tileable textures.
    /// </summary>
    public static class ForestNoise
    {
        // 16 evenly spread unit gradients.
        static readonly float[] GradX = new float[16];
        static readonly float[] GradY = new float[16];

        static ForestNoise()
        {
            for (int i = 0; i < 16; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / 16f;
                GradX[i] = Mathf.Cos(a);
                GradY[i] = Mathf.Sin(a);
            }
        }

        public static uint Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 0x27D4EB2Du + 0x165667B1u;
                h ^= (uint)x * 0x85EBCA6Bu;
                h = (h << 13) | (h >> 19);
                h ^= (uint)y * 0xC2B2AE35u;
                h *= 0x9E3779B1u;
                h ^= h >> 15;
                h *= 0x85EBCA77u;
                h ^= h >> 13;
                h *= 0xC2B2AE3Du;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>A hash of a lattice point as a float in [0, 1).</summary>
        public static float Hash01(int x, int y, int seed)
        {
            return (Hash(x, y, seed) >> 8) * (1f / 16777216f);
        }

        static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        static float Corner(int x, int y, int seed, float dx, float dy)
        {
            uint g = Hash(x, y, seed) & 15u;
            return GradX[g] * dx + GradY[g] * dy;
        }

        static int Wrap(int v, int period)
        {
            int m = v % period;
            return m < 0 ? m + period : m;
        }

        /// <summary>Smooth gradient noise, about [-1, 1].</summary>
        public static float Gradient(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            float u = Fade(fx), v = Fade(fy);
            float a = Corner(xi, yi, seed, fx, fy);
            float b = Corner(xi + 1, yi, seed, fx - 1f, fy);
            float c = Corner(xi, yi + 1, seed, fx, fy - 1f);
            float d = Corner(xi + 1, yi + 1, seed, fx - 1f, fy - 1f);
            float r = Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
            return Mathf.Clamp(r * 1.42f, -1f, 1f);
        }

        /// <summary>Gradient noise that repeats every <paramref name="period"/> units in x and y.</summary>
        public static float Periodic(float x, float y, int period, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            float u = Fade(fx), v = Fade(fy);
            int x0 = Wrap(xi, period), x1 = Wrap(xi + 1, period);
            int y0 = Wrap(yi, period), y1 = Wrap(yi + 1, period);
            float a = Corner(x0, y0, seed, fx, fy);
            float b = Corner(x1, y0, seed, fx - 1f, fy);
            float c = Corner(x0, y1, seed, fx, fy - 1f);
            float d = Corner(x1, y1, seed, fx - 1f, fy - 1f);
            float r = Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
            return Mathf.Clamp(r * 1.42f, -1f, 1f);
        }

        /// <summary>Fractal (several octaves of) gradient noise, about [-1, 1].</summary>
        public static float Fbm(float x, float y, int seed, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Gradient(x, y, seed + o * 7919) * amp;
                norm += amp;
                amp *= gain;
                x *= lacunarity;
                y *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>Fractal periodic noise; each octave doubles the period, so the result still tiles.</summary>
        public static float FbmPeriodic(float x, float y, int period, int seed, int octaves, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Periodic(x, y, period, seed + o * 7919) * amp;
                norm += amp;
                amp *= gain;
                x *= 2f;
                y *= 2f;
                period *= 2;
            }
            return sum / norm;
        }

        /// <summary>Ridged fractal noise in [0, 1]: sharp crests, broad valleys.</summary>
        public static float Ridged(float x, float y, int seed, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                float n = 1f - Mathf.Abs(Gradient(x, y, seed + o * 104729));
                sum += n * n * amp;
                norm += amp;
                amp *= 0.5f;
                x *= 2f;
                y *= 2f;
            }
            return sum / norm;
        }

        /// <summary>
        /// Cellular (Worley) noise: distance to the nearest (f1) and second-nearest (f2) feature point, in cell units, and the
        /// id of the nearest cell. With period &gt; 0 the pattern repeats every period cells.
        /// </summary>
        public static void Cellular(float x, float y, int period, int seed, out float f1, out float f2, out uint id)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            f1 = 9f;
            f2 = 9f;
            id = 0;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = xi + ox, cy = yi + oy;
                    int hx = period > 0 ? Wrap(cx, period) : cx;
                    int hy = period > 0 ? Wrap(cy, period) : cy;
                    uint h = Hash(hx, hy, seed);
                    float px = cx + ((h & 0xFFFFu) / 65535f) * 0.9f + 0.05f;
                    float py = cy + ((h >> 16) / 65535f) * 0.9f + 0.05f;
                    float dx = px - x, dy = py - y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < f1)
                    {
                        f2 = f1;
                        f1 = d;
                        id = Hash(hx, hy, seed ^ 0x5bd1e995);
                    }
                    else if (d < f2)
                    {
                        f2 = d;
                    }
                }
            }
        }

        /// <summary>Smooth 0..1 threshold (the shader smoothstep).</summary>
        public static float Smooth(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
