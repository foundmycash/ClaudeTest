using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace HavocForest
{
    /// <summary>The painted ground: two blend maps (8 surface weights) and the ground light map, as raw pixels.</summary>
    public sealed class ForestGroundMaps
    {
        public Vector2 min;            // the area the maps cover (the whole terrain), forest-local metres
        public Vector2 size;
        public int blendResolution;
        public Color32[] control0;     // r = dirt, g = gravel, b = moss, a = rock
        public Color32[] control1;     // r = grass, g = leaf litter, b = roots, a = needles
        public int lightResolution;
        public Color32[] light;        // r = sky light (ambient occlusion), g = sunlight that gets through the canopy, b/a unused

        /// <summary>0..1 how much of a surface is at p (nearest texel).</summary>
        public float Weight(ForestGroundRole role, Vector2 p)
        {
            int x = Mathf.Clamp((int)((p.x - min.x) / size.x * blendResolution), 0, blendResolution - 1);
            int y = Mathf.Clamp((int)((p.y - min.y) / size.y * blendResolution), 0, blendResolution - 1);
            int r = (int)role;
            Color32 c = r < 4 ? control0[y * blendResolution + x] : control1[y * blendResolution + x];
            switch (r & 3)
            {
                case 0: return c.r / 255f;
                case 1: return c.g / 255f;
                case 2: return c.b / 255f;
                default: return c.a / 255f;
            }
        }
    }

    /// <summary>
    /// Decides which ground surfaces show where: the zone's mix, broken into patches, rock on steep ground, grass and moss in
    /// the clearings, gravel down the middle of the trails with packed dirt edges and leaves drifting onto them; then the
    /// placed objects mark the ground around them (roots, needles, leaves, moss). Also bakes the ground light map: hollows are
    /// darker, and every tree crown shades the ground below it (ambient) and casts a dappled shadow along the sun.
    /// Runs on worker threads; everything it reads is read-only.
    /// </summary>
    public static class ForestGroundPainter
    {
        public static ForestGroundMaps PaintBlend(ForestSettings s, int seed, ForestLayout layout, ForestHeightField hf)
        {
            var maps = new ForestGroundMaps { min = hf.Min, size = hf.Size };
            PaintBlend(s, seed, layout, hf, maps);
            return maps;
        }

        static void PaintBlend(ForestSettings s, int seed, ForestLayout layout, ForestHeightField hf, ForestGroundMaps maps)
        {
            int res = (int)s.ground.blendMapResolution;
            maps.blendResolution = res;
            maps.control0 = new Color32[res * res];
            maps.control1 = new Color32[res * res];

            ForestShape shape = layout.shape;
            Vector2 min = hf.Min, size = hf.Size;
            List<ForestZone> zones = s.zones.zones;
            int zoneCount = layout.zoneMap.zoneCount;
            ForestGroundSettings g = s.ground;
            ForestTrailSettings ts = s.trails;
            int dirt = (int)ForestGroundRole.Dirt, gravel = (int)ForestGroundRole.Gravel, moss = (int)ForestGroundRole.Moss,
                rock = (int)ForestGroundRole.Rock, grass = (int)ForestGroundRole.Grass, leaf = (int)ForestGroundRole.LeafLitter,
                roots = (int)ForestGroundRole.Roots, needles = (int)ForestGroundRole.Needles;
            int patchSeed = seed ^ 0x6D1A, raggedSeed = seed ^ 0x2A61, leafSeed = seed ^ 0x9F13, moistSeed = seed ^ 0x40C7;

            Parallel.For(0, res, y =>
            {
                var zw = new float[zoneCount];
                var mix = new float[ForestGroundMix.Count];
                var trailMix = new float[ForestGroundMix.Count];
                for (int x = 0; x < res; x++)
                {
                    var p = new Vector2(min.x + (x + 0.5f) / res * size.x, min.y + (y + 0.5f) / res * size.y);

                    // ---- the zone's surfaces
                    layout.zoneMap.Weights(p, zw);
                    System.Array.Clear(mix, 0, mix.Length);
                    for (int z = 0; z < zoneCount; z++)
                    {
                        if (zw[z] <= 0.001f) continue;
                        ForestGroundMix gm = z < zones.Count && zones[z] != null ? zones[z].ground : ForestGroundMix.Make(0.2f, 0f, 0.3f, 0f, 0f, 0.1f, 0.1f, 0.5f);
                        for (int k = 0; k < ForestGroundMix.Count; k++) mix[k] += zw[z] * gm[k];
                    }

                    // ---- patches: every surface comes and goes on its own noise
                    for (int k = 0; k < ForestGroundMix.Count; k++)
                    {
                        float n = ForestNoise.Fbm(p.x / g.patchSize + k * 17.3f, p.y / g.patchSize - k * 9.1f, patchSeed + k * 101, 3);
                        mix[k] *= Mathf.Max(0f, 1f + g.patchContrast * 1.6f * n);
                    }
                    float moist = ForestNoise.Fbm(p.x / 45f, p.y / 45f, moistSeed, 2);
                    mix[moss] *= 1f + 0.7f * moist;
                    mix[dirt] *= 1f - 0.4f * moist;
                    Normalize(mix);

                    // ---- steep ground is rock
                    float slope = hf.SlopeDegrees(p);
                    float rk = ForestNoise.Smooth(g.rockSlopeStart, Mathf.Max(g.rockSlopeStart + 1f, g.rockSlopeEnd), slope + 4f * ForestNoise.Fbm(p.x / 3f, p.y / 3f, patchSeed ^ 0x55, 2));
                    if (rk > 0f)
                    {
                        for (int k = 0; k < ForestGroundMix.Count; k++) mix[k] *= 1f - 0.9f * rk;
                        mix[rock] += rk * 1.4f;
                        mix[moss] += rk * 0.15f;
                    }

                    // ---- the boundary band: duff and moss on the banks
                    float edge = shape.EdgeDistance(p);
                    if (edge < 0f)
                    {
                        float t = Mathf.Clamp01(-edge / 20f);
                        mix[needles] += 0.25f * t;
                        mix[moss] += 0.2f * t;
                    }

                    // ---- clearings: grass and moss, fewer needles; the trailhead is trampled dirt and gravel
                    for (int c = 0; c < layout.clearings.Count; c++)
                    {
                        ForestClearing cl = layout.clearings[c];
                        if ((p - cl.center).sqrMagnitude > (cl.radius * 1.7f + 8f) * (cl.radius * 1.7f + 8f)) continue;
                        float w = ForestNoise.Smooth(-4f, 5f, cl.Inside(p) + 2f * ForestNoise.Fbm(p.x / 3f, p.y / 3f, raggedSeed, 2));
                        if (w <= 0f) continue;
                        if (cl.trailhead)
                        {
                            mix[dirt] += w * 0.9f;
                            mix[gravel] += w * 0.5f;
                            mix[needles] *= 1f - 0.5f * w;
                        }
                        else
                        {
                            float n = ForestNoise.Fbm(p.x / 4f, p.y / 4f, leafSeed ^ c, 2) * 0.5f + 0.5f;
                            mix[grass] += w * g.clearingGrass * 2f * (0.5f + 0.5f * n);
                            mix[moss] += w * 0.4f;
                            mix[needles] *= 1f - 0.6f * w;
                            mix[roots] *= 1f - 0.8f * w;
                            mix[leaf] *= 1f - 0.3f * w;
                        }
                    }
                    Normalize(mix);

                    // ---- trails: gravel down the middle, packed dirt to the ragged edges, leaves drifting in
                    float inside = layout.trailField.Inside(p);
                    float rag = ts.edgeRaggedness * (0.6f * ForestNoise.Fbm(p.x / 1.6f, p.y / 1.6f, raggedSeed, 3)
                                                   + 0.7f * ForestNoise.Fbm(p.x / 5.5f, p.y / 5.5f, raggedSeed + 1, 2));
                    float dirtW = ForestNoise.Smooth(-0.55f, 0.3f, inside + rag);
                    int ti, pi;
                    if (dirtW > 0f && layout.trailField.Nearest(p, out ti, out pi))
                    {
                        ForestTrail trail = layout.trails[ti];
                        float hw = trail.HalfWidth;
                        bool hasGravel = trail.kind != ForestTrailKind.PlayerOnly;
                        float gravelW = hasGravel ? ForestNoise.Smooth(-0.35f, 0.5f, inside - hw * (1f - ts.gravelShare) + rag * 0.7f) : 0f;
                        System.Array.Clear(trailMix, 0, trailMix.Length);
                        trailMix[dirt] = 1f - gravelW;
                        trailMix[gravel] = gravelW * 1.4f;
                        float creep = g.leavesOnTrails * ForestNoise.Smooth(0.15f, 0.75f, ForestNoise.Fbm(p.x / 2.2f, p.y / 2.2f, leafSeed, 2) * 0.5f + 0.5f)
                                    * (1f - ForestNoise.Smooth(0.2f, hw * 0.9f, inside));
                        trailMix[leaf] = creep;
                        trailMix[needles] = creep * 0.3f;
                        Normalize(trailMix);
                        for (int k = 0; k < ForestGroundMix.Count; k++) mix[k] = Mathf.Lerp(mix[k], trailMix[k], dirtW);
                    }
                    Normalize(mix);

                    int i = y * res + x;
                    maps.control0[i] = new Color32(ToByte(mix[dirt]), ToByte(mix[gravel]), ToByte(mix[moss]), ToByte(mix[rock]));
                    maps.control1[i] = new Color32(ToByte(mix[grass]), ToByte(mix[leaf]), ToByte(mix[roots]), ToByte(mix[needles]));
                }
            });
        }

        /// <summary>
        /// Paints the ground surfaces that placed objects leave around them (roots by trunks, needles under conifers, leaves
        /// under broadleaf trees, moss around rocks), with a ragged edge. Weaker on the trails, which are kept walked clean.
        /// </summary>
        public static void ApplyStamps(ForestGroundMaps maps, List<ForestGroundStamp> stamps, ForestLayout layout, int seed)
        {
            if (stamps == null || stamps.Count == 0) return;
            int res = maps.blendResolution;
            var weights = new float[res * res * ForestGroundMix.Count];
            for (int i = 0; i < res * res; i++)
            {
                Color32 a = maps.control0[i], b = maps.control1[i];
                int o = i * ForestGroundMix.Count;
                weights[o] = a.r; weights[o + 1] = a.g; weights[o + 2] = a.b; weights[o + 3] = a.a;
                weights[o + 4] = b.r; weights[o + 5] = b.g; weights[o + 6] = b.b; weights[o + 7] = b.a;
            }
            var touched = new bool[res * res];
            float texel = maps.size.x / res;
            int noiseSeed = seed ^ 0x51AB;
            foreach (ForestGroundStamp st in stamps)
            {
                float r = st.radius * 1.25f;
                int x0 = Mathf.Max(0, (int)((st.position.x - r - maps.min.x) / texel)), x1 = Mathf.Min(res - 1, (int)((st.position.x + r - maps.min.x) / texel));
                int y0 = Mathf.Max(0, (int)((st.position.y - r - maps.min.y) / texel)), y1 = Mathf.Min(res - 1, (int)((st.position.y + r - maps.min.y) / texel));
                int role = (int)st.role;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(maps.min.x + (x + 0.5f) * texel, maps.min.y + (y + 0.5f) * texel);
                        float d = Vector2.Distance(p, st.position);
                        float edge = st.radius * (1f + 0.3f * ForestNoise.Gradient(p.x / 1.3f, p.y / 1.3f, noiseSeed + role));
                        float w = st.strength * ForestNoise.Smooth(edge, edge * 0.35f, d);
                        if (w <= 0f) continue;
                        if (layout.trailField.Inside(p) > -0.3f) w *= st.role == ForestGroundRole.Roots ? 0.5f : 0.15f;
                        int i = y * res + x;
                        weights[i * ForestGroundMix.Count + role] += w * 255f;
                        touched[i] = true;
                    }
                }
            }
            for (int i = 0; i < res * res; i++)
            {
                if (!touched[i]) continue;
                int o = i * ForestGroundMix.Count;
                float sum = 0f;
                for (int k = 0; k < ForestGroundMix.Count; k++) sum += weights[o + k];
                float inv = sum > 0f ? 1f / sum : 0f;
                maps.control0[i] = new Color32(ToByte(weights[o] * inv), ToByte(weights[o + 1] * inv), ToByte(weights[o + 2] * inv), ToByte(weights[o + 3] * inv));
                maps.control1[i] = new Color32(ToByte(weights[o + 4] * inv), ToByte(weights[o + 5] * inv), ToByte(weights[o + 6] * inv), ToByte(weights[o + 7] * inv));
            }
        }

        /// <summary>
        /// The ground light map. Red = sky light: hollows are a little darker, and every crown above darkens the ground under
        /// it (overlapping crowns darker still). Green = sunlight that reaches the ground: each crown casts its shadow along
        /// the sun direction, broken up into dapples where light gets through the leaves.
        /// </summary>
        public static void PaintLight(ForestSettings s, ForestLayout layout, ForestHeightField hf, ForestGroundMaps maps,
                                      List<ForestCanopy> canopies, Vector3 sunDirection, int seed)
        {
            int res = (int)s.ground.lightMapResolution;
            maps.lightResolution = res;
            maps.light = new Color32[res * res];
            Vector2 min = hf.Min, size = hf.Size;
            ForestGrid broad = hf.grid.Blurred(18f);
            float strength = s.ground.terrainOcclusion;
            var sky = new float[res * res];
            var sun = new float[res * res];
            Parallel.For(0, res, y =>
            {
                for (int x = 0; x < res; x++)
                {
                    var p = new Vector2(min.x + (x + 0.5f) / res * size.x, min.y + (y + 0.5f) / res * size.y);
                    float hollow = Mathf.Max(0f, broad.Sample(p) - hf.SampleHeight(p));
                    sky[y * res + x] = 1f - strength * Mathf.Clamp01(hollow * 0.12f);
                    sun[y * res + x] = 1f;
                }
            });

            if (canopies != null && canopies.Count > 0)
            {
                float texel = size.x / res;
                // where a crown's shadow lands: travel along the sun direction from the crown's height down to the ground
                float down = Mathf.Max(0.15f, -sunDirection.y);
                var shadowShift = new Vector2(sunDirection.x, sunDirection.z) / down;
                int dappleSeed = seed ^ 0x2DA9;
                foreach (ForestCanopy c in canopies)
                {
                    Stamp(sky, res, min, texel, c.position, c.radius * 1.15f, (p, t) => 1f - c.shade * 0.2f * (1f - t * t));
                    Vector2 shadow = c.position + shadowShift * c.height;
                    float sr = c.radius * 1.05f;
                    Stamp(sun, res, min, texel, shadow, sr, (p, t) =>
                    {
                        // dapples: holes in the shade where the noise is high, more of them towards the crown's edge
                        float n = ForestNoise.Fbm(p.x / 0.9f, p.y / 0.9f, dappleSeed, 2) * 0.5f + 0.5f;
                        float holes = ForestNoise.Smooth(0.52f - 0.25f * t, 0.68f - 0.2f * t, n);
                        float cover = c.shade * 0.8f * (1f - t * t * t);
                        return 1f - cover * (1f - holes);
                    });
                }
            }

            // Even the darkest spot under the trees keeps some sky light and a few sun flecks.
            for (int i = 0; i < res * res; i++)
                maps.light[i] = new Color32(ToByte(Mathf.Max(0.45f, sky[i])), ToByte(Mathf.Max(0.06f, sun[i])), 0, 255);
        }

        /// <summary>Multiplies a buffer by f(position, distance / radius) inside a circle.</summary>
        static void Stamp(float[] buf, int res, Vector2 min, float texel, Vector2 center, float radius, System.Func<Vector2, float, float> f)
        {
            int x0 = Mathf.Max(0, (int)((center.x - radius - min.x) / texel)), x1 = Mathf.Min(res - 1, (int)((center.x + radius - min.x) / texel));
            int y0 = Mathf.Max(0, (int)((center.y - radius - min.y) / texel)), y1 = Mathf.Min(res - 1, (int)((center.y + radius - min.y) / texel));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(min.x + (x + 0.5f) * texel, min.y + (y + 0.5f) * texel);
                    float t = Vector2.Distance(p, center) / radius;
                    if (t >= 1f) continue;
                    buf[y * res + x] *= f(p, t);
                }
            }
        }

        static void Normalize(float[] w)
        {
            float sum = 0f;
            for (int k = 0; k < w.Length; k++) sum += w[k];
            if (sum < 1e-5f)
            {
                System.Array.Clear(w, 0, w.Length);
                w[(int)ForestGroundRole.Dirt] = 1f;
                return;
            }
            float inv = 1f / sum;
            for (int k = 0; k < w.Length; k++) w[k] *= inv;
        }

        static byte ToByte(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }
    }
}
