using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace HavocForest
{
    /// <summary>Everything one generation run produced, before it is turned into Unity objects.</summary>
    public sealed class ForestBuildResult
    {
        public int seed;
        public ForestShape shape;
        public ForestHeightField heights;
        public ForestLayout layout;
        public ForestGroundMaps ground;
        public ForestScatterResult scatter;
        public List<ForestChunkMesh> chunks;
        public List<ForestMarker> markers;
        public readonly List<string> warnings = new List<string>();
        public readonly StringBuilder timings = new StringBuilder();
        public string contentHash;
    }

    /// <summary>
    /// Runs the generation steps in order. No Unity objects are created; apart from reading the asset library's prefabs
    /// (main thread) it is plain computation. Same settings + same library + same seed = same result.
    ///   1. shape and base terrain (hills, bumps, the raised boundary)
    ///   2. layout (clearings, trails, depth, landmarks, zones)
    ///   3. final terrain (trails and clearings levelled into it)
    ///   4. markers
    ///   5. ground blend maps
    ///   6. scatter (trees, undergrowth, rocks, ground cover, landmarks) and the ground marks they leave
    ///   7. ground light map (terrain hollows, canopy shade, dappled sun)
    ///   8. terrain chunk meshes
    /// </summary>
    public static class ForestBuilder
    {
        public static ForestBuildResult Build(ForestSettings s, int seed)
        {
            return Build(s, null, seed);
        }

        public static ForestBuildResult Build(ForestSettings s, ForestAssetLibrary library, int seed)
        {
            var r = new ForestBuildResult { seed = seed };
            var sw = Stopwatch.StartNew();
            System.Action<string> lap = step =>
            {
                r.timings.Append(step).Append(' ').Append(sw.ElapsedMilliseconds).Append(" ms, ");
                sw.Restart();
            };

            float spacing = s.terrain.vertexSpacing;
            r.shape = new ForestShape(s, seed, spacing);
            r.heights = new ForestHeightField(r.shape.terrainMin, r.shape.terrainSize, spacing);
            BaseHeights(r.heights.grid, r.shape, s, seed);
            lap("terrain");

            r.layout = ForestLayoutBuilder.Build(s, seed, r.shape, r.heights.grid, r.warnings);
            lap("layout");

            ShapeTerrain(r.heights, r.layout, s, seed);
            r.heights.ComputeDiagonals();
            lap("levelling");

            r.markers = ForestMarkerBuilder.FromLayout(r.layout, r.heights, s, seed);
            lap("markers");

            r.ground = ForestGroundPainter.PaintBlend(s, seed, r.layout, r.heights);
            lap("ground");

            r.scatter = new ForestScatter(s, library, r.layout, r.heights, r.ground, r.markers, seed).Run(r.warnings);
            r.markers.AddRange(r.scatter.markers);
            ForestGroundPainter.ApplyStamps(r.ground, r.scatter.stamps, r.layout, seed);
            lap("scatter");

            Vector3 sunDir = s.atmosphere != null && s.atmosphere.calm != null ? s.atmosphere.calm.ToState().SunDirection : Quaternion.Euler(32f, 225f, 0f) * Vector3.forward;
            ForestGroundPainter.PaintLight(s, r.layout, r.heights, r.ground, r.scatter.canopies, sunDir, seed);
            lap("light");
            if (r.scatter.clearanceViolations.Count > 0)
                r.warnings.Add(r.scatter.clearanceViolations.Count + " place(s) where something solid stands in the monster's way (shown as red spheres with Monster Clearance gizmos on).");

            r.chunks = ForestTerrainMesher.Build(r.heights, s.terrain.chunkCells);
            lap("meshes");

            r.contentHash = Hash(r);
            return r;
        }

        // ---------------------------------------------------------------- terrain

        static void BaseHeights(ForestGrid g, ForestShape shape, ForestSettings s, int seed)
        {
            ForestTerrainSettings t = s.terrain;
            int hillSeed = seed ^ 0x1F1A, ridgeSeed = seed ^ 0x3C55, bumpSeed = seed ^ 0x77B3;
            float hillScale = Mathf.Max(10f, t.hillScale), bumpScale = Mathf.Max(1f, t.bumpScale);
            Parallel.For(0, g.height, y =>
            {
                for (int x = 0; x < g.width; x++)
                {
                    Vector2 p = g.NodePosition(x, y);
                    float rolling = ForestNoise.Fbm(p.x / hillScale, p.y / hillScale, hillSeed, 4);
                    float ridged = ForestNoise.Ridged(p.x / (hillScale * 1.3f), p.y / (hillScale * 1.3f), ridgeSeed, 3) * 2f - 1f;
                    float hills = t.hillHeight * Mathf.Lerp(rolling, ridged * 0.8f, t.ridges);
                    float bumps = t.bumpHeight * ForestNoise.Fbm(p.x / bumpScale, p.y / bumpScale, bumpSeed, 3);
                    float edge = shape.EdgeDistance(p);
                    g[x, y] = hills + bumps + shape.BoundaryRise(p, edge);
                }
            });
        }

        /// <summary>Levels the trails (flat across, following the smoothed ground along) and flattens the clearings.</summary>
        static void ShapeTerrain(ForestHeightField hf, ForestLayout layout, ForestSettings s, int seed)
        {
            ForestGrid g = hf.grid;
            ForestGrid smooth = g.Blurred(7f);
            foreach (ForestClearing c in layout.clearings) c.floorHeight = smooth.Sample(c.center);

            float shoulder = Mathf.Max(0.5f, s.trails.shoulder);
            float flatten = s.clearings.flatten;
            int bowlSeed = seed ^ 0x4B07;
            var result = new float[g.values.Length];
            Parallel.For(0, g.height, y =>
            {
                for (int x = 0; x < g.width; x++)
                {
                    Vector2 p = g.NodePosition(x, y);
                    float h = g[x, y];

                    float inside = layout.trailField.Inside(p);
                    int ti, pi;
                    if (inside > -shoulder && layout.trailField.Nearest(p, out ti, out pi))
                    {
                        float w = ForestNoise.Smooth(-shoulder, 0.25f, inside);
                        float target = smooth.Sample(layout.trails[ti].points[pi]) - 0.06f;
                        h = Mathf.Lerp(h, target, w);
                    }

                    for (int c = 0; c < layout.clearings.Count; c++)
                    {
                        ForestClearing cl = layout.clearings[c];
                        if ((p - cl.center).sqrMagnitude > (cl.radius * 1.8f + 10f) * (cl.radius * 1.8f + 10f)) continue;
                        float ins = cl.Inside(p);
                        if (ins <= -8f) continue;
                        float w = ForestNoise.Smooth(-8f, cl.radius * 0.25f, ins) * flatten;
                        float bowl = 0.18f * ForestNoise.Fbm(p.x / 6f, p.y / 6f, bowlSeed, 2);
                        h = Mathf.Lerp(h, cl.floorHeight + bowl, w);
                    }
                    result[y * g.width + x] = h;
                }
            });
            System.Array.Copy(result, g.values, result.Length);
        }

        // ---------------------------------------------------------------- determinism check

        /// <summary>A short fingerprint of the result: two runs with the same settings and seed must give the same one.</summary>
        static string Hash(ForestBuildResult r)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                System.Action<int> mix = v => { h ^= (uint)v; h *= 1099511628211UL; };
                foreach (float f in r.heights.grid.values) mix(System.BitConverter.SingleToInt32Bits(f));
                foreach (ForestTrail t in r.layout.trails)
                    foreach (Vector2 p in t.points) { mix(Mathf.RoundToInt(p.x * 1000f)); mix(Mathf.RoundToInt(p.y * 1000f)); }
                foreach (Color32 c in r.ground.control0) mix(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                foreach (Color32 c in r.ground.control1) mix(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                if (r.scatter != null)
                    foreach (ForestInstance i in r.scatter.instances)
                    {
                        mix(i.category * 977 + i.variant);
                        mix(Mathf.RoundToInt(i.position.x * 1000f));
                        mix(Mathf.RoundToInt(i.position.y * 1000f));
                        mix(Mathf.RoundToInt(i.position.z * 1000f));
                        mix(Mathf.RoundToInt(i.rotation.y * 10000f));
                        mix(Mathf.RoundToInt(i.scale.y * 10000f));
                    }
                return h.ToString("x16");
            }
        }
    }
}
