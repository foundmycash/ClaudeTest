using UnityEngine;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Paints the built-in placeholder ground textures: tileable colour + height for each of the 8 surfaces, and a macro
    /// noise texture. They are stand-ins with the right colours and structure (stones, leaves, needles, roots...) so the
    /// blending can be judged; replace them with real textures in the Ground Layers asset.
    /// </summary>
    public static class ForestPlaceholderTextures
    {
        public struct Result
        {
            public Color[] albedo;      // sRGB colour
            public float[] height;      // 0..1
        }

        public static Result Paint(ForestGroundRole role, int size, int seed)
        {
            var r = new Result { albedo = new Color[size * size], height = new float[size * size] };
            switch (role)
            {
                case ForestGroundRole.Dirt: Dirt(r, size, seed); break;
                case ForestGroundRole.Gravel: Gravel(r, size, seed); break;
                case ForestGroundRole.Moss: Moss(r, size, seed); break;
                case ForestGroundRole.Rock: Rock(r, size, seed); break;
                case ForestGroundRole.Grass: Grass(r, size, seed); break;
                case ForestGroundRole.LeafLitter: Leaves(r, size, seed); break;
                case ForestGroundRole.Roots: Roots(r, size, seed); break;
                default: Needles(r, size, seed); break;
            }
            return r;
        }

        // ---------------------------------------------------------------- helpers

        static float Fbm(float u, float v, int period, int seed, int oct)
        {
            return ForestNoise.FbmPeriodic(u * period, v * period, period, seed, oct);
        }

        static Color C(float r, float g, float b)
        {
            return new Color(r, g, b, 1f);
        }

        static Color Shade(Color c, float k)
        {
            return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f);
        }

        static int Wrap(int v, int size)
        {
            v %= size;
            return v < 0 ? v + size : v;
        }

        /// <summary>
        /// Draws a filled, oriented shape (leaf, blade, needle) with wrap-around. shape(a, b) gets the position along (a, -1..1)
        /// and across (b, -1..1) and returns the coverage height, or negative where the shape is absent.
        /// </summary>
        static void Stamp(Result r, int size, float cx, float cy, float angle, float halfLen, float halfWid, Color color,
                          float baseHeight, System.Func<float, float, float> shape, System.Func<float, float, float> tone)
        {
            float ca = Mathf.Cos(angle), sa = Mathf.Sin(angle);
            float ext = Mathf.Max(halfLen, halfWid) + 1f;
            int x0 = Mathf.FloorToInt(cx - ext), x1 = Mathf.CeilToInt(cx + ext);
            int y0 = Mathf.FloorToInt(cy - ext), y1 = Mathf.CeilToInt(cy + ext);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float a = (dx * ca + dy * sa) / halfLen;
                    float b = (-dx * sa + dy * ca) / halfWid;
                    float h = shape(a, b);
                    if (h < 0f) continue;
                    int i = Wrap(y, size) * size + Wrap(x, size);
                    float hh = baseHeight + h;
                    if (hh < r.height[i] - 0.15f) continue;    // hidden under something higher
                    r.height[i] = Mathf.Max(r.height[i], hh);
                    r.albedo[i] = Shade(color, tone != null ? tone(a, b) : 1f);
                }
            }
        }

        // ---------------------------------------------------------------- surfaces

        static void Dirt(Result r, int s, int seed)
        {
            Color dark = C(0.20f, 0.155f, 0.115f), light = C(0.32f, 0.26f, 0.195f), pebble = C(0.34f, 0.31f, 0.27f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    float b = Fbm(u, v, 4, seed, 5);
                    float fine = Fbm(u, v, 64, seed + 3, 3);
                    float f1, f2; uint id;
                    ForestNoise.Cellular(u * 56f, v * 56f, 56, seed + 7, out f1, out f2, out id);
                    float peb = (id & 255u) < 36u ? ForestNoise.Smooth(0.2f, 0.1f, f1) : 0f;
                    Color c = Color.Lerp(dark, light, b * 0.5f + 0.5f);
                    c = Shade(c, 1f + 0.14f * fine);
                    c = Color.Lerp(c, Shade(pebble, 0.8f + 0.4f * ((id >> 8) & 255u) / 255f), peb);
                    int i = y * s + x;
                    r.albedo[i] = c;
                    r.height[i] = Mathf.Clamp01(0.42f + 0.18f * b + 0.08f * fine + peb * 0.35f);
                }
            }
        }

        static void Gravel(Result r, int s, int seed)
        {
            // Loose trail gravel: a scatter of stones of mixed size half sunk in packed grit, not a paved surface.
            Color soil = C(0.24f, 0.21f, 0.18f), grit = C(0.36f, 0.34f, 0.31f), stoneA = C(0.40f, 0.39f, 0.37f), stoneB = C(0.52f, 0.50f, 0.46f), stoneDark = C(0.29f, 0.28f, 0.27f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    float warpU = u + 0.004f * Fbm(u, v, 32, seed + 21, 2), warpV = v + 0.004f * Fbm(u, v, 32, seed + 22, 2);
                    float baseN = Fbm(u, v, 8, seed + 3, 3);
                    Color c = Color.Lerp(soil, grit, ForestNoise.Smooth(-0.5f, 0.6f, baseN + 0.3f * Fbm(u, v, 128, seed + 4, 2)));
                    float h = 0.2f + 0.1f * baseN;

                    // two sizes of stones; only some cells hold a stone, so the grit shows through
                    for (int layer = 0; layer < 2; layer++)
                    {
                        int period = layer == 0 ? 34 : 72;
                        float f1, f2; uint id;
                        ForestNoise.Cellular(warpU * period, warpV * period, period, seed + layer * 31, out f1, out f2, out id);
                        float present = layer == 0 ? 0.45f : 0.6f;
                        if ((id & 255u) / 255f > present) continue;
                        float size = 0.28f + 0.22f * (((id >> 8) & 255u) / 255f);
                        float stone = ForestNoise.Smooth(size, size * 0.55f, f1);
                        if (stone <= 0f) continue;
                        float h3 = ((id >> 16) & 255u) / 255f;
                        Color sc = h3 < 0.2f ? stoneDark : Color.Lerp(stoneA, stoneB, ((id >> 24) & 255u) / 255f);
                        float dome = Mathf.Clamp01(1f - f1 / size);
                        sc = Shade(sc, 0.85f + 0.2f * dome);
                        float sh = (layer == 0 ? 0.55f : 0.42f) + 0.35f * dome;
                        if (sh > h)
                        {
                            c = Color.Lerp(c, sc, stone);
                            h = Mathf.Lerp(h, sh, stone);
                        }
                    }
                    int i = y * s + x;
                    r.albedo[i] = c;
                    r.height[i] = Mathf.Clamp01(h);
                }
            }
        }

        static void Moss(Result r, int s, int seed)
        {
            Color low = C(0.12f, 0.16f, 0.07f), high = C(0.27f, 0.32f, 0.12f), yellow = C(0.37f, 0.36f, 0.16f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    float clumps = ForestNoise.Smooth(-0.45f, 0.6f, Fbm(u, v, 10, seed, 4));
                    float fine = Fbm(u, v, 80, seed + 3, 3);
                    float patch = ForestNoise.Smooth(0.15f, 0.55f, Fbm(u, v, 4, seed + 9, 2));
                    Color c = Color.Lerp(low, high, clumps);
                    c = Color.Lerp(c, yellow, patch * 0.6f * clumps);
                    c = Shade(c, 1f + 0.2f * fine);
                    int i = y * s + x;
                    r.albedo[i] = c;
                    r.height[i] = Mathf.Clamp01(0.25f + 0.5f * clumps + 0.12f * fine);
                }
            }
        }

        static void Rock(Result r, int s, int seed)
        {
            Color dark = C(0.29f, 0.29f, 0.29f), light = C(0.47f, 0.46f, 0.44f), lichen = C(0.52f, 0.54f, 0.45f), moss = C(0.17f, 0.21f, 0.10f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    float b = Fbm(u, v, 3, seed, 5);
                    float f1, f2; uint id;
                    ForestNoise.Cellular(u * 5f + 0.15f * Fbm(u, v, 8, seed + 2, 2), v * 5f, 5, seed + 4, out f1, out f2, out id);
                    float crack = 1f - ForestNoise.Smooth(0.0f, 0.05f, f2 - f1);
                    float l1, l2; uint lid;
                    ForestNoise.Cellular(u * 34f, v * 34f, 34, seed + 6, out l1, out l2, out lid);
                    float lich = (lid & 255u) < 60u ? ForestNoise.Smooth(0.35f, 0.15f, l1) : 0f;
                    Color c = Color.Lerp(dark, light, b * 0.5f + 0.5f);
                    c = Shade(c, 1f + 0.15f * Fbm(u, v, 48, seed + 8, 3));
                    c = Color.Lerp(c, lichen, lich * 0.7f);
                    c = Color.Lerp(c, moss, ForestNoise.Smooth(-0.15f, -0.5f, b) * 0.7f);
                    c = Shade(c, 1f - 0.6f * crack);
                    int i = y * s + x;
                    r.albedo[i] = c;
                    r.height[i] = Mathf.Clamp01(0.55f + 0.35f * b - 0.45f * crack);
                }
            }
        }

        static void Grass(Result r, int s, int seed)
        {
            Color soil = C(0.15f, 0.13f, 0.095f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    int i = y * s + x;
                    r.albedo[i] = Shade(soil, 1f + 0.25f * Fbm(u, v, 16, seed, 3));
                    r.height[i] = 0.1f;
                }
            }
            var rng = new ForestRandom(seed, 1);
            Color[] palette = { C(0.27f, 0.33f, 0.14f), C(0.35f, 0.38f, 0.18f), C(0.43f, 0.41f, 0.23f), C(0.21f, 0.27f, 0.11f), C(0.48f, 0.44f, 0.27f) };
            int blades = s * s / 48;
            for (int k = 0; k < blades; k++)
            {
                float len = rng.Range(6f, 13f) * s / 512f;
                float wid = rng.Range(0.9f, 1.6f) * s / 512f;
                float ang = Mathf.PI * 0.5f + rng.Range(-0.7f, 0.7f);
                Color col = palette[rng.Range(0, palette.Length)];
                Stamp(r, s, rng.Range(0f, s), rng.Range(0f, s), ang, len, wid, col, 0.25f,
                      (a, b) => Mathf.Abs(b) < 1f - Mathf.Max(0f, a) * 0.7f ? 0.5f + 0.25f * a : -1f,
                      (a, b) => 0.8f + 0.35f * (a * 0.5f + 0.5f));
            }
        }

        static void Leaves(Result r, int s, int seed)
        {
            Color soil = C(0.13f, 0.10f, 0.075f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    int i = y * s + x;
                    r.albedo[i] = Shade(soil, 1f + 0.3f * Fbm(u, v, 12, seed, 3));
                    r.height[i] = 0.05f;
                }
            }
            var rng = new ForestRandom(seed, 2);
            Color[] palette = { C(0.36f, 0.22f, 0.12f), C(0.50f, 0.28f, 0.11f), C(0.43f, 0.17f, 0.09f), C(0.52f, 0.42f, 0.19f), C(0.25f, 0.16f, 0.10f), C(0.31f, 0.24f, 0.14f) };
            int leaves = s * s / 190;
            for (int k = 0; k < leaves; k++)
            {
                float len = rng.Range(9f, 15f) * s / 512f;
                float wid = len * rng.Range(0.42f, 0.6f);
                Color col = Shade(palette[rng.Range(0, palette.Length)], rng.Range(0.8f, 1.15f));
                float layer = 0.25f + 0.55f * k / leaves;
                Stamp(r, s, rng.Range(0f, s), rng.Range(0f, s), rng.Angle(), len, wid, col, layer,
                      (a, b) =>
                      {
                          float widthAt = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a)) * (1f - 0.35f * Mathf.Max(0f, a));
                          return Mathf.Abs(b) < widthAt ? 0.15f * (1f - Mathf.Abs(b)) : -1f;
                      },
                      (a, b) => (Mathf.Abs(b) < 0.08f ? 0.7f : 1f) * (1f - 0.25f * Mathf.Abs(b) * Mathf.Abs(b)));
            }
        }

        static void Roots(Result r, int s, int seed)
        {
            Color soil = C(0.21f, 0.17f, 0.125f), bark = C(0.32f, 0.255f, 0.19f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    int i = y * s + x;
                    r.albedo[i] = Shade(soil, 1f + 0.3f * Fbm(u, v, 24, seed, 3));
                    r.height[i] = 0.2f + 0.1f * Fbm(u, v, 8, seed + 1, 2);
                }
            }
            var rng = new ForestRandom(seed, 3);
            int roots = 12;
            for (int k = 0; k < roots; k++)
            {
                float px = rng.Range(0f, s), py = rng.Range(0f, s);
                float heading = rng.Angle();
                float steps = rng.Range(0.4f, 0.9f) * s;
                float r0 = rng.Range(6f, 11f) * s / 512f;
                for (int t = 0; t < steps; t++)
                {
                    float frac = t / steps;
                    float rad = Mathf.Lerp(r0, r0 * 0.3f, frac);
                    heading += ForestNoise.Gradient(t * 0.02f, k * 3.1f, seed + 9) * 0.06f;
                    px += Mathf.Cos(heading);
                    py += Mathf.Sin(heading);
                    int ext = Mathf.CeilToInt(rad) + 1;
                    for (int oy = -ext; oy <= ext; oy++)
                    {
                        for (int ox = -ext; ox <= ext; ox++)
                        {
                            float d = Mathf.Sqrt(ox * ox + oy * oy) / rad;
                            if (d > 1f) continue;
                            float prof = Mathf.Sqrt(1f - d * d);
                            int i = Wrap(Mathf.RoundToInt(py) + oy, s) * s + Wrap(Mathf.RoundToInt(px) + ox, s);
                            float hh = 0.35f + 0.6f * prof * (0.5f + 0.5f * rad / r0);
                            if (hh <= r.height[i]) continue;
                            r.height[i] = hh;
                            float light = 0.65f + 0.45f * prof + 0.1f * ForestNoise.Gradient(t * 0.4f, d * 3f, seed + k);
                            r.albedo[i] = Shade(bark, light);
                        }
                    }
                }
            }
        }

        static void Needles(Result r, int s, int seed)
        {
            Color duff = C(0.23f, 0.18f, 0.13f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    int i = y * s + x;
                    r.albedo[i] = Shade(duff, 1f + 0.3f * Fbm(u, v, 10, seed, 4));
                    r.height[i] = 0.15f;
                }
            }
            var rng = new ForestRandom(seed, 4);
            Color[] palette = { C(0.42f, 0.31f, 0.20f), C(0.33f, 0.25f, 0.17f), C(0.48f, 0.41f, 0.30f), C(0.28f, 0.20f, 0.13f), C(0.38f, 0.34f, 0.27f) };
            int needles = s * s / 30;
            for (int k = 0; k < needles; k++)
            {
                float len = rng.Range(5f, 10f) * s / 512f;
                float wid = rng.Range(0.6f, 0.95f) * s / 512f;
                Color col = palette[rng.Range(0, palette.Length)];
                Stamp(r, s, rng.Range(0f, s), rng.Range(0f, s), rng.Angle(), len, wid, col, 0.2f + 0.5f * rng.Value(),
                      (a, b) => Mathf.Abs(b) < 1f ? 0.1f : -1f, null);
            }
            for (int k = 0; k < 40; k++)
            {
                float len = rng.Range(25f, 50f) * s / 512f;
                Stamp(r, s, rng.Range(0f, s), rng.Range(0f, s), rng.Angle(), len, 1.6f * s / 512f, C(0.20f, 0.15f, 0.10f), 0.6f,
                      (a, b) => Mathf.Abs(b) < 1f ? 0.2f * (1f - Mathf.Abs(b)) : -1f, (a, b) => 0.8f + 0.4f * (1f - Mathf.Abs(b)));
            }
        }

        // ---------------------------------------------------------------- normal map and macro noise

        /// <summary>Tangent-space normals (OpenGL convention, green up) from a tileable height map.</summary>
        public static Color[] NormalsFromHeight(float[] h, int s, float strength)
        {
            var n = new Color[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float l = h[y * s + Wrap(x - 1, s)], rr = h[y * s + Wrap(x + 1, s)];
                    float d = h[Wrap(y - 1, s) * s + x], u = h[Wrap(y + 1, s) * s + x];
                    var v = new Vector3(-(rr - l) * strength, -(u - d) * strength, 1f).normalized;
                    n[y * s + x] = new Color(v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, 1f);
                }
            }
            return n;
        }

        /// <summary>How bumpy each surface's normal map is (height units are 0..1 per texel step, so these are large).</summary>
        public static float NormalStrength(ForestGroundRole role)
        {
            switch (role)
            {
                case ForestGroundRole.Gravel: return 9f;
                case ForestGroundRole.Rock: return 7f;
                case ForestGroundRole.Roots: return 7f;
                case ForestGroundRole.LeafLitter: return 6f;
                case ForestGroundRole.Grass: return 5f;
                case ForestGroundRole.Needles: return 5f;
                case ForestGroundRole.Moss: return 4f;
                default: return 4f;
            }
        }

        /// <summary>Tileable large-scale noise: r = brightness, g = warm/cool, b and a = spare octaves.</summary>
        public static Color[] MacroNoise(int s, int seed)
        {
            var c = new Color[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float u = (float)x / s, v = (float)y / s;
                    c[y * s + x] = new Color(
                        Fbm(u, v, 3, seed, 4) * 0.5f + 0.5f,
                        Fbm(u, v, 2, seed + 1, 3) * 0.5f + 0.5f,
                        Fbm(u, v, 6, seed + 2, 3) * 0.5f + 0.5f,
                        Fbm(u, v, 12, seed + 3, 2) * 0.5f + 0.5f);
                }
            }
            return c;
        }
    }
}
