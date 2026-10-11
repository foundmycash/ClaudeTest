using UnityEngine;

namespace HavocCave
{
    /// <summary>
    /// Small deterministic 3D value noise. It uses no Unity noise calls and no static state,
    /// so it is safe to call from worker threads and gives the same result on every platform.
    /// </summary>
    public static class CaveNoise
    {
        static float Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(y * 668265263) + (uint)(z * 1274126177) + (uint)(seed * 362437);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFu) * (2f / 65535f) - 1f;
            }
        }

        static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        /// <summary>Smooth noise in [-1, 1].</summary>
        public static float Value(float x, float y, float z, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            int zi = Mathf.FloorToInt(z);
            float u = Fade(x - xi);
            float v = Fade(y - yi);
            float w = Fade(z - zi);

            float c000 = Hash(xi, yi, zi, seed);
            float c100 = Hash(xi + 1, yi, zi, seed);
            float c010 = Hash(xi, yi + 1, zi, seed);
            float c110 = Hash(xi + 1, yi + 1, zi, seed);
            float c001 = Hash(xi, yi, zi + 1, seed);
            float c101 = Hash(xi + 1, yi, zi + 1, seed);
            float c011 = Hash(xi, yi + 1, zi + 1, seed);
            float c111 = Hash(xi + 1, yi + 1, zi + 1, seed);

            float x00 = c000 + (c100 - c000) * u;
            float x10 = c010 + (c110 - c010) * u;
            float x01 = c001 + (c101 - c001) * u;
            float x11 = c011 + (c111 - c011) * u;
            float y0 = x00 + (x10 - x00) * v;
            float y1 = x01 + (x11 - x01) * v;
            return y0 + (y1 - y0) * w;
        }

        /// <summary>Three octaves of value noise, normalised to roughly [-1, 1].</summary>
        public static float Fbm(float x, float y, float z, int seed)
        {
            float sum = 0f;
            float amp = 1f;
            float norm = 0f;
            float freq = 1f;
            for (int o = 0; o < 3; o++)
            {
                sum += amp * Value(x * freq + o * 17.13f, y * freq + o * 31.71f, z * freq - o * 7.93f, seed + o * 1013);
                norm += amp;
                amp *= 0.5f;
                freq *= 2.07f;
            }
            return sum / norm;
        }

        /// <summary>GLSL style smoothstep (UnityEngine.Mathf.SmoothStep is a different function).</summary>
        public static float Smooth(float a, float b, float x)
        {
            if (b == a) return x < a ? 0f : 1f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
