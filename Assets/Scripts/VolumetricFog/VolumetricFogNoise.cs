using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace VRFog
{
    /// <summary>
    /// Builds the tileable 3D noise the fog shader samples (several octaves of Perlin noise in one single-channel
    /// texture), and shares one copy between all fog volumes that use the same settings.
    /// </summary>
    public static class VolumetricFogNoise
    {
        const int Octaves = 4;
        const int BaseCellsPerTile = 4;   // grows x2 per octave; powers of two make the lattice wrap cleanly

        class Entry
        {
            public Texture3D texture;
            public int users;
        }

        static readonly Dictionary<long, Entry> cache = new Dictionary<long, Entry>();

        /// <summary>Returns the shared noise texture for these settings. Call <see cref="Release"/> when done with it.</summary>
        public static Texture3D Acquire(int size, int seed)
        {
            long key = ((long)size << 32) | (uint)seed;
            if (!cache.TryGetValue(key, out Entry entry))
            {
                entry = new Entry { texture = Create(size, seed) };
                cache.Add(key, entry);
            }
            entry.users++;
            return entry.texture;
        }

        public static void Release(int size, int seed)
        {
            long key = ((long)size << 32) | (uint)seed;
            if (!cache.TryGetValue(key, out Entry entry))
                return;

            if (--entry.users > 0)
                return;

            cache.Remove(key);
            if (entry.texture != null)
                Object.DestroyImmediate(entry.texture);
        }

        static Texture3D Create(int size, int seed)
        {
            byte[] permutation = BuildPermutation(seed);
            float[] values = new float[size * size * size];

            Parallel.For(0, size, z =>
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float fx = (x + 0.5f) / size, fy = (y + 0.5f) / size, fz = (z + 0.5f) / size;

                        float sum = 0f, amplitude = 1f, total = 0f;
                        int cells = BaseCellsPerTile;
                        for (int o = 0; o < Octaves; o++)
                        {
                            sum += amplitude * Perlin(fx * cells, fy * cells, fz * cells, cells, permutation);
                            total += amplitude;
                            amplitude *= 0.5f;
                            cells *= 2;
                        }
                        values[(z * size + y) * size + x] = sum / total;
                    }
                }
            });

            // Stretch to the full 0..1 range so the shader's coverage / sharpness sliders behave predictably.
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            float scale = 1f / Mathf.Max(max - min, 1e-5f);

            byte[] bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
                bytes[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01((values[i] - min) * scale) * 255f);

            // Mip levels let the shader pick a blurrier copy when its ray steps are longer than the noise detail, instead of
            // aliasing that detail into grain.
            var texture = new Texture3D(size, size, size, TextureFormat.R8, true)
            {
                name = "Volumetric Fog Noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixelData(bytes, 0);
            texture.Apply(true, true);
            return texture;
        }

        static byte[] BuildPermutation(int seed)
        {
            var random = new System.Random(seed);
            byte[] table = new byte[256];
            for (int i = 0; i < 256; i++)
                table[i] = (byte)i;
            for (int i = 255; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (table[i], table[j]) = (table[j], table[i]);
            }

            byte[] doubled = new byte[512];
            for (int i = 0; i < 512; i++)
                doubled[i] = table[i & 255];
            return doubled;
        }

        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        // Improved Perlin noise whose lattice wraps every 'period' cells (a power of two), so the texture tiles.
        static float Perlin(float x, float y, float z, int period, byte[] p)
        {
            int mask = period - 1;
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float xf = x - xi, yf = y - yi, zf = z - zi;
            xi &= mask; yi &= mask; zi &= mask;
            int x1 = (xi + 1) & mask, y1 = (yi + 1) & mask, z1 = (zi + 1) & mask;

            float u = Fade(xf), v = Fade(yf), w = Fade(zf);

            // Corner hashes use the wrapped neighbour indices; the table is doubled so the sums never overflow.
            return Lerp(w,
                Lerp(v,
                    Lerp(u, Grad(p[p[p[xi] + yi] + zi], xf, yf, zf), Grad(p[p[p[x1] + yi] + zi], xf - 1f, yf, zf)),
                    Lerp(u, Grad(p[p[p[xi] + y1] + zi], xf, yf - 1f, zf), Grad(p[p[p[x1] + y1] + zi], xf - 1f, yf - 1f, zf))),
                Lerp(v,
                    Lerp(u, Grad(p[p[p[xi] + yi] + z1], xf, yf, zf - 1f), Grad(p[p[p[x1] + yi] + z1], xf - 1f, yf, zf - 1f)),
                    Lerp(u, Grad(p[p[p[xi] + y1] + z1], xf, yf - 1f, zf - 1f), Grad(p[p[p[x1] + y1] + z1], xf - 1f, yf - 1f, zf - 1f))));
        }

        static float Lerp(float t, float a, float b) => a + t * (b - a);
    }
}
