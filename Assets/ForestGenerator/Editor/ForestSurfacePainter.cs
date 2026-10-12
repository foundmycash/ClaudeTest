using System;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Paints the placeholder surface textures (bark, needles, leaves, stone...) for the forest models. Every texture tiles
    /// seamlessly by construction: all noise is periodic with whole-number cell counts, and every stamp wraps round the
    /// edges. Plain C# (no UnityEngine), so it can also run outside the editor. Output is sRGB, row 0 at the bottom (the
    /// way Unity stores textures), so "up" in the picture is up the trunk.
    /// The textures are used as detail on top of the models' vertex colours (see ForestSurfaceArrays): what matters is the
    /// pattern, not the average colour.
    /// </summary>
    public static class ForestSurfacePainter
    {
        public const int Count = 8;
        public const int Bark = 0, BirchBark = 1, Needles = 2, Leaves = 3, Blades = 4, Stone = 5, Iron = 6, Wood = 7;
        public static readonly string[] Names = { "Bark", "BirchBark", "Needles", "Leaves", "Blades", "Stone", "Iron", "Wood" };

        /// <summary>size * size * 3 floats (r, g, b in 0..1, sRGB).</summary>
        public static float[] Paint(int surface, int size, int seed)
        {
            var c = new Canvas(size);
            switch (surface)
            {
                case Bark: PaintBark(c, seed); break;
                case BirchBark: PaintBirch(c, seed); break;
                case Needles: PaintNeedles(c, seed); break;
                case Leaves: PaintLeaves(c, seed); break;
                case Blades: PaintBlades(c, seed); break;
                case Stone: PaintStone(c, seed); break;
                case Iron: PaintIron(c, seed); break;
                default: PaintWood(c, seed); break;
            }
            return c.rgb;
        }

        // ---------------------------------------------------------------- bark: broken vertical furrows between plates

        static void PaintBark(Canvas c, int seed)
        {
            // long ridges running up the trunk, split by furrows that wander, branch and fade out, with a few cross breaks
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float wander = 1.3f * (Fbm(u, v, 2, 3, 3, seed + 1) - 0.5f) + 0.3f * (Fbm(u, v, 6, 9, 2, seed + 2) - 0.5f);
                    // main furrows
                    float phase = u * 6f + wander;
                    float d1 = MathF.Abs(Frac(phase + 0.5f) - 0.5f);                          // 0 on a furrow, 0.5 mid-ridge
                    float width1 = 0.035f + 0.075f * Fbm(u, v, 3, 4, 2, seed + 3);
                    float fur1 = 1f - Smooth(Clamp01(d1 / width1));
                    // branching furrows that only exist in places
                    float phase2 = u * 11f + 1.7f * wander + 0.4f;
                    float d2 = MathF.Abs(Frac(phase2 + 0.5f) - 0.5f);
                    float on2 = Smooth(Clamp01((Fbm(u, v, 3, 3, 3, seed + 4) - 0.5f) / 0.15f));
                    float fur2 = (1f - Smooth(Clamp01(d2 / 0.045f))) * on2;
                    // cross breaks (short horizontal cracks)
                    float nb = Fbm(u, v, 5, 22, 2, seed + 5);
                    float brk = (1f - Smooth(Clamp01(MathF.Abs(2f * nb - 1f) / 0.07f))) * Smooth(Clamp01((Fbm(u, v, 4, 4, 2, seed + 6) - 0.55f) / 0.12f));
                    float furrow = Clamp01(fur1 + fur2 + brk * 0.5f);
                    float round = Smooth(Clamp01(d1 / 0.3f));                                 // ridges are darker toward their edges
                    float fibre = Fbm(u, v, 80, 7, 3, seed + 7);                              // long fibres
                    float fibre2 = Fbm(u, v, 26, 4, 2, seed + 8);
                    float flake = R((int)(u * s) / 2, (int)(v * s) / 6, seed + 9);
                    float tone = 0.8f + 0.4f * Fbm(u, v, 4, 5, 2, seed + 10);
                    float k = tone * (0.5f + 0.5f * round) * (0.45f + 0.95f * fibre) * (0.75f + 0.5f * fibre2) * (0.88f + 0.24f * flake);
                    float r = 0.30f * k, g = 0.22f * k, b = 0.16f * k;
                    r = Lerp(r, 0.03f, furrow);
                    g = Lerp(g, 0.024f, furrow);
                    b = Lerp(b, 0.02f, furrow);
                    c.Set(x, y, r, g, b);
                }
        }

        // ---------------------------------------------------------------- birch: pale papery bark with dark horizontal marks

        static void PaintBirch(Canvas c, int seed)
        {
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float paper = Fbm(u, v, 3, 22, 4, seed + 1);                           // horizontal peeling bands
                    float fine = Fbm(u, v, 70, 12, 2, seed + 2);                           // fibres
                    float k = 0.72f + 0.35f * paper + 0.12f * (fine - 0.5f);
                    float warm = Fbm(u, v, 5, 5, 2, seed + 3);
                    c.Set(x, y, 0.84f * k + 0.04f * warm, 0.81f * k, 0.74f * k - 0.03f * warm);
                }
            var rng = new Rng(seed + 10);
            // thin horizontal cracks in the paper
            for (int i = 0; i < 60; i++)
            {
                float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                float tone = 0.12f + rng.Next() * 0.1f;
                c.Capsule(cx, cy, (rng.Next() - 0.5f) * 0.12f, 8f + rng.Next() * 26f, 0.7f + rng.Next() * 0.7f, tone, tone * 0.95f, tone * 0.9f, 0.65f, 1f);
            }
            // dark pointed marks (lenticels, old branch scars), each with a pale curled lip
            for (int i = 0; i < 46; i++)
            {
                float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                float len = 16f + rng.Next() * 40f, wid = len * (0.1f + rng.Next() * 0.1f);
                float ang = (rng.Next() - 0.5f) * 0.16f;
                float tone = 0.05f + rng.Next() * 0.08f;
                if (rng.Next() < 0.6f) c.Lens(cx, cy - wid * 0.9f, ang, len * 0.95f, wid * 0.45f, 0.93f, 0.9f, 0.84f, 0.5f);
                c.Lens(cx, cy, ang, len, wid, tone, tone * 0.95f, tone * 0.9f, 0.92f);
                if (rng.Next() < 0.5f) c.Lens(cx + (rng.Next() - 0.5f) * len * 0.5f, cy, ang, len * 0.45f, wid * 0.6f, tone * 0.6f, tone * 0.6f, tone * 0.55f, 0.9f);
            }
            for (int i = 0; i < 6; i++)      // a few big dark scars
            {
                float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                float len = 50f + rng.Next() * 40f;
                c.Lens(cx, cy, (rng.Next() - 0.5f) * 0.2f, len, len * 0.16f, 0.06f, 0.055f, 0.05f, 0.9f);
            }
        }

        // ---------------------------------------------------------------- needles: dense short strokes, mostly hanging

        static void PaintNeedles(Canvas c, int seed)
        {
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float clump = Fbm(u, v, 6, 6, 3, seed + 1);
                    float k = 0.5f + 0.9f * clump;
                    c.Set(x, y, 0.045f * k, 0.075f * k, 0.045f * k);
                }
            var rng = new Rng(seed + 20);
            int count = (int)(c.size * c.size / 95f);
            for (int i = 0; i < count; i++)
            {
                float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                float spray = (rng.Next() - 0.5f) * 2.4f;                  // hanging bias with plenty of spread
                float angle = -MathF.PI * 0.5f + spray;
                float len = 7f + rng.Next() * 10f;
                float t = rng.Next();
                float lit = 0.4f + 0.6f * t * t;
                float r = Lerp(0.07f, 0.24f, lit), g = Lerp(0.11f, 0.40f, lit), b = Lerp(0.06f, 0.18f, lit);
                float shade = 0.7f + 0.5f * Fbm(cx / c.size, cy / c.size, 6, 6, 3, seed + 1);
                c.Capsule(cx, cy, angle, len, 0.85f, r * shade, g * shade, b * shade, 0.92f, 1f);
            }
            // a few pale tips and brown dead needles
            for (int i = 0; i < count / 12; i++)
            {
                float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                c.Capsule(cx, cy, -MathF.PI * 0.5f + (rng.Next() - 0.5f) * 2f, 6f + rng.Next() * 6f, 0.8f, 0.30f, 0.20f, 0.10f, 0.7f, 1f);
            }
        }

        // ---------------------------------------------------------------- leaves: overlapping leaf shapes with a midrib and veins

        static void PaintLeaves(Canvas c, int seed)
        {
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float k = 0.7f + 0.5f * Fbm(u, v, 5, 5, 3, seed + 1);
                    c.Set(x, y, 0.10f * k, 0.11f * k, 0.06f * k);                    // the dim gaps between leaves
                }
            var rng = new Rng(seed + 30);
            int count = (int)(c.size * c.size / 1300f);
            for (int layer = 0; layer < 3; layer++)
            {
                float dim = layer == 0 ? 0.65f : layer == 1 ? 0.85f : 1f;
                for (int i = 0; i < count; i++)
                {
                    float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                    float len = 22f + rng.Next() * 26f, wid = len * (0.45f + rng.Next() * 0.2f);
                    float tint = rng.Next();
                    float r = Lerp(0.22f, 0.34f, tint) * dim, g = Lerp(0.29f, 0.33f, tint) * dim, b = Lerp(0.10f, 0.12f, tint) * dim;
                    c.Leaf(cx, cy, rng.Next() * MathF.PI * 2f, len, wid, r, g, b);
                }
            }
        }

        // ---------------------------------------------------------------- blades: one blade or frond across the tile (u across, v along)

        static void PaintBlades(Canvas c, int seed)
        {
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float side = MathF.Abs(u - 0.5f) * 2f;                                  // 0 at the midrib, 1 at the edge
                    float warp = 0.02f * (Fbm(u, v, 3, 4, 2, seed + 1) - 0.5f);
                    float chev = Frac(v * 12f + (side + warp) * 3.5f);                       // slanted side veins pointing up and out
                    float vein = 1f - Smooth(Clamp01(MathF.Abs(chev - 0.5f) * 2f / 0.22f)); // thin line where chev is near 0.5
                    float rib = 1f - Smooth(Clamp01(side / 0.09f));
                    float edge = Smooth(Clamp01((side - 0.7f) / 0.3f));
                    float speck = Fbm(u, v, 24, 40, 2, seed + 2);
                    float k = 0.78f + 0.4f * (speck - 0.5f);
                    k *= 1f - 0.45f * edge;
                    k *= 1f - 0.4f * vein;
                    k += 0.45f * rib;
                    c.Set(x, y, 0.20f * k, 0.34f * k, 0.12f * k);
                }
        }

        // ---------------------------------------------------------------- stone: grain, pits, and partial cracks

        static void PaintStone(Canvas c, int seed)
        {
            // rough granite: crags, strata, fine grain, pale lichen specks and a few fractures that run out
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float wu = u + 0.16f * (Fbm(u, v, 3, 3, 3, seed + 1) - 0.5f);
                    float wv = v + 0.16f * (Fbm(u, v, 3, 3, 3, seed + 2) - 0.5f);
                    float f1, f2; int id;
                    Worley(wu, wv, 3, 3, seed + 3, out f1, out f2, out id);
                    float edge = f2 - f1;
                    float crack = 1f - Smooth(Clamp01(edge / 0.045f));
                    crack *= Smooth(Clamp01((Fbm(u, v, 3, 3, 3, seed + 4) - 0.5f) / 0.18f));   // only some borders are cracks
                    float crag = Fbm(u, v, 5, 5, 5, seed + 5);
                    float ridged = 1f - MathF.Abs(2f * Fbm(wu, wv, 7, 7, 4, seed + 6) - 1f);  // sharp crests
                    float strata = 0.5f + 0.5f * MathF.Sin((v * 9f + 1.7f * (Fbm(u, v, 2, 2, 2, seed + 10) - 0.5f)) * MathF.PI * 2f);
                    float pit = Fbm(u, v, 26, 26, 3, seed + 7);
                    float grain = R((int)(u * s), (int)(v * s), seed + 8);
                    float k = (0.55f + 0.7f * crag) * (0.75f + 0.45f * ridged * ridged) * (0.92f + 0.16f * strata) * (0.7f + 0.55f * pit) * (0.85f + 0.3f * grain);
                    k *= 1f - 0.75f * crack;
                    float lichen = Smooth(Clamp01((Fbm(u, v, 9, 9, 4, seed + 11) - 0.62f) / 0.12f)) * (0.5f + 0.5f * grain);
                    float warm = Fbm(u, v, 3, 3, 2, seed + 12) - 0.5f;
                    float r = 0.42f * k + 0.025f * warm, g = 0.41f * k, b = 0.385f * k - 0.025f * warm;
                    r = Lerp(r, 0.52f, lichen * 0.55f);
                    g = Lerp(g, 0.55f, lichen * 0.55f);
                    b = Lerp(b, 0.42f, lichen * 0.55f);
                    c.Set(x, y, r, g, b);
                }
        }

        // ---------------------------------------------------------------- iron: dark metal with rust blotches, drips and pits

        static void PaintIron(Canvas c, int seed)
        {
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float blotch = Fbm(u, v, 4, 4, 5, seed + 1);
                    float drip = Fbm(u, v, 20, 3, 4, seed + 2);                              // long vertical streaks
                    float rust = Smooth(Clamp01((blotch * 0.7f + drip * 0.5f - 0.62f) / 0.2f)) * 0.9f;
                    float f1, f2; int id;
                    Worley(u, v, 22, 22, seed + 3, out f1, out f2, out id);
                    float pitMask = (id & 255) < 70 ? 1f : 0f;                              // only some cells are pits
                    float pit = (1f - Smooth(Clamp01(f1 / 0.16f))) * pitMask;
                    float fine = Fbm(u, v, 64, 64, 3, seed + 4);
                    float metal = 0.7f + 0.6f * fine;
                    float r = 0.19f * metal, g = 0.185f * metal, b = 0.18f * metal;
                    float rv = 0.6f + 0.8f * Fbm(u, v, 11, 11, 3, seed + 5);
                    r = Lerp(r, 0.40f * rv, rust);
                    g = Lerp(g, 0.19f * rv, rust);
                    b = Lerp(b, 0.08f * rv, rust);
                    float dark = 1f - 0.5f * pit * (0.4f + 0.6f * rust);
                    c.Set(x, y, r * dark, g * dark, b * dark);
                }
            var rng = new Rng(seed + 40);
            for (int i = 0; i < 26; i++)     // scratches
            {
                float cx = rng.Next() * c.size, cy = rng.Next() * c.size;
                float a = (rng.Next() - 0.5f) * 1.2f + (rng.Next() < 0.5f ? 0f : MathF.PI * 0.5f);
                c.Capsule(cx, cy, a, 8f + rng.Next() * 30f, 0.6f, 0.5f, 0.46f, 0.42f, 0.28f, 1f);
            }
        }

        // ---------------------------------------------------------------- wood: weathered long grain with cracks

        static void PaintWood(Canvas c, int seed)
        {
            int s = c.size;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float wob = Fbm(u, v, 3, 2, 3, seed + 1);
                    float ring = 0.5f + 0.5f * MathF.Sin((u * 11f + wob * 2.6f) * MathF.PI * 2f);
                    float grain = Fbm(u, v, 56, 2, 4, seed + 2);                              // long fibres
                    float fine = Fbm(u, v, 120, 6, 2, seed + 3);
                    float n = Fbm(u + 0.04f * (wob - 0.5f), v, 7, 2, 3, seed + 4);
                    float line = MathF.Abs(2f * n - 1f);
                    float crack = 1f - Smooth(Clamp01(line / 0.06f));
                    crack *= Smooth(Clamp01((Fbm(u, v, 3, 2, 2, seed + 5) - 0.4f) / 0.2f));
                    float k = 0.55f + 0.35f * ring + 0.45f * (grain - 0.5f) + 0.2f * (fine - 0.5f);
                    float bleach = Fbm(u, v, 4, 3, 3, seed + 6);
                    float r = 0.47f * k, g = 0.41f * k, b = 0.34f * k;
                    r = Lerp(r, r * 1.08f + 0.03f, bleach * 0.4f);
                    r = Lerp(r, 0.05f, crack * 0.9f);
                    g = Lerp(g, 0.04f, crack * 0.9f);
                    b = Lerp(b, 0.035f, crack * 0.9f);
                    c.Set(x, y, r, g, b);
                }
        }

        // ---------------------------------------------------------------- periodic noise

        static uint H(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(y * 668265263) + (uint)(seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                h = (h ^ (h >> 15)) * 2246822519u;
                h ^= h >> 13;
                return h;
            }
        }

        static float R(int x, int y, int seed)
        {
            return (H(x, y, seed) & 0xFFFFFF) / 16777216f;
        }

        static int Mod(int a, int m)
        {
            int r = a % m;
            return r < 0 ? r + m : r;
        }

        static float Frac(float x)
        {
            return x - MathF.Floor(x);
        }

        static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        static float Clamp01(float x)
        {
            return x < 0f ? 0f : x > 1f ? 1f : x;
        }

        static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        /// <summary>Value noise that repeats every 1 unit of u and v, with px by py cells across the tile. 0..1.</summary>
        static float VNoise(float u, float v, int px, int py, int seed)
        {
            float x = Frac(u) * px, y = Frac(v) * py;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = Smooth(x - x0), fy = Smooth(y - y0);
            int xa = Mod(x0, px), xb = Mod(x0 + 1, px), ya = Mod(y0, py), yb = Mod(y0 + 1, py);
            float a = R(xa, ya, seed), b = R(xb, ya, seed), c = R(xa, yb, seed), d = R(xb, yb, seed);
            return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }

        static float Fbm(float u, float v, int px, int py, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * VNoise(u, v, px, py, seed + o * 17);
                norm += amp;
                amp *= 0.5f;
                px *= 2;
                py *= 2;
            }
            return sum / norm;
        }

        /// <summary>Periodic cellular noise: distance to the nearest and second-nearest point (in cell widths) and the nearest cell's id.</summary>
        static void Worley(float u, float v, int px, int py, int seed, out float f1, out float f2, out int id)
        {
            float x = Frac(u) * px, y = Frac(v) * py;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            f1 = 9f;
            f2 = 9f;
            id = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = x0 + dx, cy = y0 + dy;
                    int wx = Mod(cx, px), wy = Mod(cy, py);
                    float ddx = cx + R(wx, wy, seed) - x, ddy = cy + R(wx, wy, seed + 1) - y;
                    float d = MathF.Sqrt(ddx * ddx + ddy * ddy);
                    if (d < f1)
                    {
                        f2 = f1;
                        f1 = d;
                        id = (int)(H(wx, wy, seed + 2) & 0x7FFFFFFF);
                    }
                    else if (d < f2) f2 = d;
                }
        }

        /// <summary>
        /// Periodic cellular noise on a px by py lattice, measured in texture space (so cells stretched by px != py still get
        /// borders of an even width), in units of 1/px of the texture. f1 / f2: nearest and second-nearest point.
        /// </summary>
        static void WorleyAniso(float u, float v, int px, int py, int seed, out float f1, out float f2, out int id)
        {
            float x = Frac(u) * px, y = Frac(v) * py;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            f1 = 9f;
            f2 = 9f;
            id = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int cx = x0 + dx, cy = y0 + dy;
                    int wx = Mod(cx, px), wy = Mod(cy, py);
                    float ddx = (cx + R(wx, wy, seed) - x) / px, ddy = (cy + R(wx, wy, seed + 1) - y) / py;
                    float d = MathF.Sqrt(ddx * ddx + ddy * ddy) * px;
                    if (d < f1)
                    {
                        f2 = f1;
                        f1 = d;
                        id = (int)(H(wx, wy, seed + 2) & 0x7FFFFFFF);
                    }
                    else if (d < f2) f2 = d;
                }
        }

        sealed class Rng
        {
            uint state;

            public Rng(int seed)
            {
                state = (uint)seed * 2654435761u + 12345u;
            }

            public float Next()
            {
                unchecked
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    return (state & 0xFFFFFF) / 16777216f;
                }
            }
        }

        /// <summary>The picture being painted. Stamps wrap round the edges so the result always tiles.</summary>
        sealed class Canvas
        {
            public readonly int size;
            public readonly float[] rgb;

            public Canvas(int size)
            {
                this.size = size;
                rgb = new float[size * size * 3];
            }

            public void Set(int x, int y, float r, float g, float b)
            {
                int i = (y * size + x) * 3;
                rgb[i] = Clamp01(r);
                rgb[i + 1] = Clamp01(g);
                rgb[i + 2] = Clamp01(b);
            }

            public void Blend(int x, int y, float r, float g, float b, float a)
            {
                if (a <= 0f) return;
                int i = (Mod(y, size) * size + Mod(x, size)) * 3;
                rgb[i] = Lerp(rgb[i], r, a);
                rgb[i + 1] = Lerp(rgb[i + 1], g, a);
                rgb[i + 2] = Lerp(rgb[i + 2], b, a);
            }

            /// <summary>A soft line segment of half length halfLen along angle, halfWid thick.</summary>
            public void Capsule(float cx, float cy, float angle, float halfLen, float halfWid, float r, float g, float b, float alpha, float soft)
            {
                float ca = MathF.Cos(angle), sa = MathF.Sin(angle);
                int ext = (int)MathF.Ceiling(halfLen + halfWid + soft + 1f);
                int x0 = (int)MathF.Floor(cx) - ext, x1 = (int)MathF.Floor(cx) + ext;
                int y0 = (int)MathF.Floor(cy) - ext, y1 = (int)MathF.Floor(cy) + ext;
                for (int yy = y0; yy <= y1; yy++)
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        float dx = xx + 0.5f - cx, dy = yy + 0.5f - cy;
                        float lx = dx * ca + dy * sa, ly = -dx * sa + dy * ca;
                        float ax = MathF.Max(MathF.Abs(lx) - halfLen, 0f);
                        float d = MathF.Sqrt(ax * ax + ly * ly);
                        float cov = Clamp01((halfWid - d) / soft + 0.5f);
                        if (cov > 0f) Blend(xx, yy, r, g, b, alpha * cov);
                    }
            }

            /// <summary>A plain pointed oval (a lens): len and wid are the full length and width.</summary>
            public void Lens(float cx, float cy, float angle, float len, float wid, float r, float g, float b, float alpha)
            {
                float ca = MathF.Cos(angle), sa = MathF.Sin(angle);
                float half0 = len * 0.5f;
                int ext = (int)MathF.Ceiling(half0 + wid * 0.5f + 2f);
                int x0 = (int)MathF.Floor(cx) - ext, x1 = (int)MathF.Floor(cx) + ext;
                int y0 = (int)MathF.Floor(cy) - ext, y1 = (int)MathF.Floor(cy) + ext;
                for (int yy = y0; yy <= y1; yy++)
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        float dx = xx + 0.5f - cx, dy = yy + 0.5f - cy;
                        float lx = dx * ca + dy * sa, ly = -dx * sa + dy * ca;
                        float t = lx / len + 0.5f;
                        if (t < 0f || t > 1f) continue;
                        float half = wid * 0.5f * MathF.Pow(MathF.Sin(t * MathF.PI), 0.9f);
                        float edge = Clamp01((half - MathF.Abs(ly)) / 1f + 0.5f);
                        if (edge > 0f) Blend(xx, yy, r, g, b, alpha * edge);
                    }
            }

            /// <summary>A pointed oval leaf with a lighter midrib, side veins and one edge in shade.</summary>
            public void Leaf(float cx, float cy, float angle, float len, float wid, float r, float g, float b)
            {
                float ca = MathF.Cos(angle), sa = MathF.Sin(angle);
                int ext = (int)MathF.Ceiling(len * 0.5f + wid * 0.5f + 2f);
                int x0 = (int)MathF.Floor(cx) - ext, x1 = (int)MathF.Floor(cx) + ext;
                int y0 = (int)MathF.Floor(cy) - ext, y1 = (int)MathF.Floor(cy) + ext;
                for (int yy = y0; yy <= y1; yy++)
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        float dx = xx + 0.5f - cx, dy = yy + 0.5f - cy;
                        float lx = dx * ca + dy * sa, ly = -dx * sa + dy * ca;
                        float t = lx / len + 0.5f;
                        if (t < 0f || t > 1f) continue;
                        float half = wid * 0.5f * MathF.Pow(MathF.Sin(t * MathF.PI), 0.75f);
                        if (half < 0.5f) continue;
                        float edge = Clamp01((half - MathF.Abs(ly)) / 1.2f);
                        if (edge <= 0f) continue;
                        float across = ly / half;                                            // -1..1
                        float shade = 0.82f + 0.22f * across;
                        float vein = 1f - Smooth(Clamp01(MathF.Abs(Frac(t * 7f - MathF.Abs(across) * 1.6f) - 0.5f) * 2f / 0.25f));
                        float rib = 1f - Smooth(Clamp01(MathF.Abs(ly) / (0.9f + wid * 0.04f)));
                        float k = shade * (1f - 0.22f * vein) + 0.35f * rib;
                        float rim = 1f - 0.35f * Smooth(Clamp01((MathF.Abs(across) - 0.8f) / 0.2f));
                        Blend(xx, yy, r * k * rim, g * k * rim, b * k * rim, edge);
                    }
            }
        }
    }
}
