using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Splits the forest into irregular regions (warped Voronoi cells around scattered sites), each one a zone from the
    /// settings, with soft borders. Zones that prefer the deep forest are more likely on sites with a high dread value.
    /// Weights are stored on a 4 m grid per zone and sampled bilinearly.
    /// </summary>
    public sealed class ForestZoneMap
    {
        public const float GridCell = 4f;

        public readonly List<ForestZoneSite> sites = new List<ForestZoneSite>();
        public readonly int zoneCount;
        readonly ForestGrid[] grids;

        ForestZoneMap(int zoneCount, ForestGrid[] grids)
        {
            this.zoneCount = zoneCount;
            this.grids = grids;
        }

        public static ForestZoneMap Build(ForestShape shape, ForestZoneSettings s, ForestGrid dread, int seed)
        {
            List<ForestZone> zones = s.zones;
            int zc = zones != null ? zones.Count : 0;
            var grids = new ForestGrid[Mathf.Max(1, zc)];
            for (int z = 0; z < grids.Length; z++) grids[z] = ForestGrid.Covering(shape.terrainMin, shape.terrainSize, GridCell);
            var map = new ForestZoneMap(Mathf.Max(1, zc), grids);

            var rng = new ForestRandom(seed, "zones");
            var weights = new float[Mathf.Max(1, zc)];
            float spacing = s.zoneSize * 0.8f;
            Vector2 min = shape.terrainMin, size = shape.terrainSize;
            int target = Mathf.Max(1, Mathf.RoundToInt(size.x * size.y / (s.zoneSize * s.zoneSize) * 1.2f));
            for (int attempt = 0; attempt < target * 40 && map.sites.Count < target; attempt++)
            {
                var p = new Vector2(min.x + rng.Value() * size.x, min.y + rng.Value() * size.y);
                bool ok = true;
                for (int k = 0; k < map.sites.Count && ok; k++)
                    if ((map.sites[k].position - p).sqrMagnitude < spacing * spacing) ok = false;
                if (!ok) continue;

                float d = dread != null ? dread.Sample(p) : 0.5f;
                for (int z = 0; z < zc; z++)
                {
                    ForestZone zone = zones[z];
                    weights[z] = zone != null && zone.enabled ? zone.weight * Mathf.Exp(1.6f * zone.dreadPreference * (2f * d - 1f)) : 0f;
                }
                int pick = zc > 0 ? rng.Pick(weights, zc) : -1;
                map.sites.Add(new ForestZoneSite { position = p, zone = Mathf.Max(0, pick) });
            }

            // Weights per grid node: every site within a few border widths of the nearest one contributes, fading with how
            // much further away it is than the nearest. The position is warped first so the borders wiggle.
            ForestGrid g0 = grids[0];
            float blend = Mathf.Max(1f, s.borderBlend);
            int warpA = seed ^ 0x77A1, warpB = seed ^ 0x2B91;
            var acc = new float[map.zoneCount];
            for (int y = 0; y < g0.height; y++)
            {
                for (int x = 0; x < g0.width; x++)
                {
                    Vector2 p = g0.NodePosition(x, y);
                    p.x += s.borderWarp * ForestNoise.Fbm(p.x / 60f, p.y / 60f, warpA, 3);
                    p.y += s.borderWarp * ForestNoise.Fbm(p.x / 60f, p.y / 60f, warpB, 3);

                    float d0 = float.MaxValue;
                    for (int k = 0; k < map.sites.Count; k++) d0 = Mathf.Min(d0, Vector2.Distance(p, map.sites[k].position));
                    System.Array.Clear(acc, 0, acc.Length);
                    float total = 0f;
                    for (int k = 0; k < map.sites.Count; k++)
                    {
                        float d = Vector2.Distance(p, map.sites[k].position) - d0;
                        if (d > blend * 3f) continue;
                        float wgt = Mathf.Exp(-(d / blend) * (d / blend));
                        acc[map.sites[k].zone] += wgt;
                        total += wgt;
                    }
                    for (int z = 0; z < map.zoneCount; z++) grids[z][x, y] = total > 0f ? acc[z] / total : (z == 0 ? 1f : 0f);
                }
            }
            return map;
        }

        /// <summary>Fills w (length >= zoneCount) with how much each zone applies at p. They add up to 1.</summary>
        public void Weights(Vector2 p, float[] w)
        {
            for (int z = 0; z < zoneCount; z++) w[z] = grids[z].Sample(p);
        }

        public int Dominant(Vector2 p)
        {
            int best = 0;
            float bw = -1f;
            for (int z = 0; z < zoneCount; z++)
            {
                float v = grids[z].Sample(p);
                if (v > bw) { bw = v; best = z; }
            }
            return best;
        }
    }
}
