using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>One placed object, in the forest's local space.</summary>
    public struct ForestInstance
    {
        public int category;
        public int variant;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;

        /// <summary>Extra per-instance value for special categories (streetlights: state + 10 if green).</summary>
        public float extra;
    }

    /// <summary>A crown that shades the ground (for the ground light map).</summary>
    public struct ForestCanopy
    {
        public Vector2 position;
        public float radius;
        public float height;
        public float shade;
    }

    /// <summary>A ground surface painted around a placed object.</summary>
    public struct ForestGroundStamp
    {
        public Vector2 position;
        public float radius;
        public ForestGroundRole role;
        public float strength;
    }

    public sealed class ForestScatterResult
    {
        public readonly List<ForestInstance> instances = new List<ForestInstance>();
        public readonly List<ForestCanopy> canopies = new List<ForestCanopy>();
        public readonly List<ForestGroundStamp> stamps = new List<ForestGroundStamp>();
        public readonly List<ForestMarker> markers = new List<ForestMarker>();
        public readonly List<Vector3> clearanceViolations = new List<Vector3>();
        public int[] categoryCounts = new int[0];
    }

    /// <summary>
    /// Places the asset library's categories. Every category is scattered by "dart throwing": random points over the forest,
    /// each kept with a probability that follows the local density (zone, tag, clusters, ground surface, slope) and only if it
    /// keeps its distance from everything placed before it. That gives natural, irregular groups with no grid. Blocking
    /// objects are kept off the monster's routes (trails, clearings, the monster gaps), so it always has a way through.
    /// </summary>
    public sealed class ForestScatter
    {
        readonly ForestSettings s;
        readonly ForestAssetLibrary lib;
        readonly ForestLayout layout;
        readonly ForestHeightField hf;
        readonly ForestGroundMaps ground;
        readonly List<ForestMarker> layoutMarkers;
        readonly int seed;
        readonly ForestSpatialHash hash;
        readonly ForestScatterResult result = new ForestScatterResult();
        readonly float[] zoneWeights;

        // monster gaps (capsules in XZ) and bend thickets
        readonly List<Vector4> gaps = new List<Vector4>();            // xy = start, zw = end
        readonly List<float> gapRadius = new List<float>();
        readonly List<Vector4> thickets = new List<Vector4>();        // xy = corner, zw = inside direction

        public ForestScatter(ForestSettings s, ForestAssetLibrary lib, ForestLayout layout, ForestHeightField hf, ForestGroundMaps ground,
                             List<ForestMarker> layoutMarkers, int seed)
        {
            this.s = s;
            this.lib = lib;
            this.layout = layout;
            this.hf = hf;
            this.ground = ground;
            this.layoutMarkers = layoutMarkers;
            this.seed = seed;
            hash = new ForestSpatialHash(layout.shape.terrainMin, layout.shape.terrainSize, 2f);
            zoneWeights = new float[Mathf.Max(1, layout.zoneMap.zoneCount)];
        }

        struct CategoryPlan
        {
            public int index;
            public ForestAssetCategory cat;
            public ForestPrefabInfo[] infos;
            public float[] weights;
            public float order;
        }

        public ForestScatterResult Run(List<string> warnings)
        {
            if (lib == null || lib.categories == null) return result;
            result.categoryCounts = new int[lib.categories.Count];

            PlanMonsterGaps();
            PlanThickets();

            // keep the trailhead clear
            hash.Add(new ForestSpatialHash.Entry { a = layout.spawnPosition, b = layout.spawnPosition, radius = 4f, solid = true, category = -1 });

            // Order: landmarks, then big solid things, trees, thickets, undergrowth, ground cover.
            var plans = new List<CategoryPlan>();
            for (int c = 0; c < lib.categories.Count; c++)
            {
                ForestAssetCategory cat = lib.categories[c];
                if (cat == null || !cat.enabled || !cat.HasUsableVariant) continue;
                var plan = new CategoryPlan { index = c, cat = cat, infos = new ForestPrefabInfo[cat.variants.Count], weights = new float[cat.variants.Count] };
                for (int v = 0; v < cat.variants.Count; v++)
                {
                    ForestAssetVariant variant = cat.variants[v];
                    plan.infos[v] = variant != null && variant.prefab != null ? ForestPrefabInfo.Get(variant.prefab) : null;
                    plan.weights[v] = plan.infos[v] != null ? variant.weight : 0f;
                }
                float kindOrder = cat.placement == ForestPlacement.Streetlight ? -1f
                                : cat.placement == ForestPlacement.Landmark ? 0f
                                : cat.placement == ForestPlacement.BoundaryRock ? 0.5f
                                : cat.kind == ForestAssetKind.Obstacle ? 1f
                                : cat.kind == ForestAssetKind.Tree ? 2f
                                : cat.placement == ForestPlacement.BendThicket ? 3f
                                : cat.kind == ForestAssetKind.Undergrowth ? 4f : 5f;
                plan.order = kindOrder * 1000f - MaxFootprint(plan) * 10f + c * 0.01f;
                plans.Add(plan);
            }
            plans.Sort((x, y) => x.order.CompareTo(y.order));

            foreach (CategoryPlan plan in plans)
            {
                if (plan.cat.placement == ForestPlacement.Landmark) PlaceLandmarks(plan);
                else if (plan.cat.placement == ForestPlacement.Streetlight) PlaceStreetlights(plan);
                else if (plan.cat.placement == ForestPlacement.BoundaryRock) PlaceBoundaryRocks(plan);
                else Scatter(plan);
            }

            CheckClearance();
            return result;
        }

        float MaxFootprint(CategoryPlan plan)
        {
            float m = 0f;
            for (int v = 0; v < plan.infos.Length; v++)
                if (plan.infos[v] != null) m = Mathf.Max(m, Footprint(plan.cat, plan.cat.variants[v], plan.infos[v]));
            return m;
        }

        /// <summary>Footprint radius at scale 1: the variant's own value, the trunk for trees, else the prefab's collider/bounds extent.</summary>
        static float Footprint(ForestAssetCategory cat, ForestAssetVariant v, ForestPrefabInfo info)
        {
            if (v.footprint > 0f) return v.footprint;
            if (cat.kind == ForestAssetKind.Tree)
                return info.trunkRadius > 0f ? info.trunkRadius * 1.3f : Mathf.Max(0.15f, Mathf.Min(info.bounds.extents.x, info.bounds.extents.z) * 0.12f);
            return Mathf.Max(0.05f, info.footprintRadius);
        }

        static float CanopyRadius(ForestPrefabInfo info)
        {
            return Mathf.Max(info.bounds.extents.x, info.bounds.extents.z);
        }

        // ---------------------------------------------------------------- monster gaps and thickets

        void PlanMonsterGaps()
        {
            int wanted = s.scatter.monsterGaps;
            if (wanted <= 0) return;
            var rng = new ForestRandom(seed, "monster gaps");
            float half = s.monster.CorridorHalfWidth;
            float pocket = s.monster.MinClearingRadius * 0.8f;
            var candidates = new List<KeyValuePair<int, int>>();
            for (int t = 0; t < layout.trails.Count; t++)
            {
                ForestTrail trail = layout.trails[t];
                if (!trail.monsterAccessible) continue;
                for (int k = 8; k < trail.points.Count - 8; k += 4) candidates.Add(new KeyValuePair<int, int>(t, k));
            }
            // shuffle deterministically
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = rng.Range(0, i + 1);
                KeyValuePair<int, int> tmp = candidates[i]; candidates[i] = candidates[j]; candidates[j] = tmp;
            }

            var placed = new List<Vector2>();
            foreach (KeyValuePair<int, int> c in candidates)
            {
                if (gaps.Count >= wanted) break;
                ForestTrail trail = layout.trails[c.Key];
                Vector2 p = trail.points[c.Value];
                bool near = false;
                foreach (ForestNode n in layout.nodes) if (Vector2.Distance(n.position, p) < 25f) near = true;
                foreach (Vector2 q in placed) if (Vector2.Distance(q, p) < 35f) near = true;
                if (near) continue;

                Vector2 tangent = (trail.points[c.Value + 2] - trail.points[c.Value - 2]).normalized;
                var side = new Vector2(-tangent.y, tangent.x) * (rng.Chance(0.5f) ? 1f : -1f);
                float length = rng.Range(12f, 20f);
                Vector2 start = p + side * trail.HalfWidth;
                Vector2 end = start + side * length;
                // the gap must lead into woods: not onto another trail, into a clearing or out of the forest
                bool ok = layout.shape.EdgeDistance(end) > pocket + 10f;
                for (float f = 0.35f; f <= 1.001f && ok; f += 0.15f)
                    if (layout.trailField.Inside(Vector2.Lerp(start, end, f)) > -(half + 2f)) ok = false;
                foreach (ForestClearing cl in layout.clearings) if (cl.Inside(end) > -pocket - 4f) ok = false;
                if (!ok) continue;

                placed.Add(p);
                gaps.Add(new Vector4(start.x, start.y, end.x, end.y));
                gapRadius.Add(half);
                gaps.Add(new Vector4(end.x, end.y, end.x, end.y));
                gapRadius.Add(pocket);
                float dread = trail.dread[c.Value];
                result.markers.Add(new ForestMarker { type = ForestMarkerType.MonsterGap, position = At(start), yaw = Yaw(-side), radius = half, dread = dread, value = length });
                result.markers.Add(new ForestMarker { type = ForestMarkerType.MonsterLair, position = At(end), yaw = Yaw(-side), radius = pocket, dread = dread });
            }
        }

        void PlanThickets()
        {
            if (s.scatter.bendThickets <= 0f || layoutMarkers == null) return;
            foreach (ForestMarker m in layoutMarkers)
            {
                if (m.type != ForestMarkerType.BlindCorner) continue;
                Vector2 dir = new Vector2(Mathf.Sin(m.yaw * Mathf.Deg2Rad), Mathf.Cos(m.yaw * Mathf.Deg2Rad));
                Vector2 left = new Vector2(-dir.y, dir.x);
                Vector2 inside = m.value >= 0f ? left : -left;
                thickets.Add(new Vector4(m.position.x, m.position.z, inside.x, inside.y));
            }
        }

        float ThicketBoost(Vector2 p)
        {
            float best = 0f;
            foreach (Vector4 t in thickets)
            {
                Vector2 d = p - new Vector2(t.x, t.y);
                float dist = d.magnitude;
                if (dist > 16f) continue;
                float side = Vector2.Dot(d, new Vector2(t.z, t.w));
                if (side < 2f) continue;
                best = Mathf.Max(best, ForestNoise.Smooth(16f, 6f, dist));
            }
            return best;
        }

        bool InGap(Vector2 a, Vector2 b, float r)
        {
            for (int i = 0; i < gaps.Count; i++)
            {
                Vector4 g = gaps[i];
                float d = SegmentDistance(new Vector2(g.x, g.y), new Vector2(g.z, g.w), a, b);
                if (d < gapRadius[i] + r) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- scattering

        float ZoneFactor(ForestAssetCategory cat, Vector2 p)
        {
            layout.zoneMap.Weights(p, zoneWeights);
            List<ForestZone> zones = s.zones.zones;
            float f = 0f, total = 0f;
            for (int z = 0; z < layout.zoneMap.zoneCount; z++)
            {
                float w = zoneWeights[z];
                if (w <= 0.001f) continue;
                total += w;
                ForestZone zone = zones != null && z < zones.Count ? zones[z] : null;
                if (zone == null) { f += w; continue; }
                float kind = cat.kind == ForestAssetKind.Tree ? zone.treeDensity : (cat.kind == ForestAssetKind.Undergrowth ? zone.undergrowthDensity : 1f);
                f += w * kind * zone.TagMultiplier(cat.tag);
            }
            return total > 0f ? f / total : 1f;
        }

        float MaxZoneFactor(ForestAssetCategory cat)
        {
            float m = 1f;
            if (s.zones.zones == null) return m;
            foreach (ForestZone zone in s.zones.zones)
            {
                if (zone == null || !zone.enabled) continue;
                float kind = cat.kind == ForestAssetKind.Tree ? zone.treeDensity : (cat.kind == ForestAssetKind.Undergrowth ? zone.undergrowthDensity : 1f);
                m = Mathf.Max(m, kind * zone.TagMultiplier(cat.tag));
            }
            return m;
        }

        void Scatter(CategoryPlan plan)
        {
            ForestAssetCategory cat = plan.cat;
            var rng = new ForestRandom(seed, "cat:" + cat.name + ":" + plan.index);
            int clusterSeed = ForestRandom.StableHash(cat.name) ^ seed;
            bool thicket = cat.placement == ForestPlacement.BendThicket;
            if (thicket && thickets.Count == 0) return;

            float baseDensity = cat.density * s.scatter.globalDensity * (cat.kind == ForestAssetKind.GroundCover ? s.scatter.groundCoverDensity : 1f);
            if (thicket) baseDensity *= s.scatter.bendThickets;
            if (baseDensity <= 0f) return;
            float clusterMax = 1f + cat.clustering * 1.4f;
            float maxDensity = baseDensity * (thicket ? 1f : MaxZoneFactor(cat)) * clusterMax * Mathf.Max(1f, cat.boundaryDensity);

            // where darts may land
            Vector2 min, size;
            if (thicket || cat.boundaryDensity > 0f)
            {
                min = layout.shape.terrainMin;
                size = layout.shape.terrainSize;
            }
            else
            {
                Vector2 half = layout.shape.HalfSize + Vector2.one * (s.world.edgeWobble + 2f);
                min = -half;
                size = half * 2f;
            }
            int darts = Mathf.Min(600000, Mathf.CeilToInt(size.x * size.y * maxDensity / 100f));
            const float ThicketReach = 16f;
            if (thicket) darts = Mathf.CeilToInt(thickets.Count * Mathf.PI * ThicketReach * ThicketReach * maxDensity / 100f);   // darts only around the bends
            float spacingScale = s.scatter.spacingScale;
            int made = 0;

            for (int d = 0; d < darts; d++)
            {
                if (cat.maxCount > 0 && made >= cat.maxCount) break;
                Vector2 p;
                if (thicket)
                {
                    Vector4 corner = thickets[rng.Range(0, thickets.Count)];
                    p = new Vector2(corner.x, corner.y) + rng.InsideCircle() * ThicketReach;
                }
                else p = new Vector2(min.x + rng.Value() * size.x, min.y + rng.Value() * size.y);
                float keep = rng.Value();
                int vi = rng.Pick(plan.weights, plan.weights.Length);
                float sc = rng.Range(cat.variants[Mathf.Max(0, vi)].scale);
                float stretch = rng.Range(cat.variants[Mathf.Max(0, vi)].heightStretch);
                float yaw = cat.randomYaw ? rng.Value() * 360f : 0f;
                float tiltA = rng.Angle(), tiltT = rng.Value();
                if (vi < 0) break;

                // ---- density here
                float density;
                if (thicket) density = baseDensity * ThicketBoost(p);
                else
                {
                    float edge = layout.shape.EdgeDistance(p);
                    float band = edge < 0f ? cat.boundaryDensity : 1f;
                    if (band <= 0f) continue;
                    density = baseDensity * band * ZoneFactor(cat, p);
                    if (cat.clustering > 0f)
                    {
                        float n = ForestNoise.Fbm(p.x / cat.clusterSize, p.y / cat.clusterSize, clusterSeed, 3) * 0.5f + 0.5f;
                        density *= Mathf.Lerp(1f, ForestNoise.Smooth(0.3f, 0.72f, n) * clusterMax, cat.clustering);
                    }
                    if (cat.followGround && ground != null)
                        density *= Mathf.Lerp(1f, Mathf.Clamp01(ground.Weight(cat.groundSurface, p) * 2.5f), cat.groundInfluence);
                }
                if (keep * maxDensity >= density) continue;

                // ---- the ground
                float slope = hf.SlopeDegrees(p);
                if (slope < cat.slope.x || slope > cat.slope.y) continue;

                ForestPrefabInfo info = plan.infos[vi];
                ForestAssetVariant variant = cat.variants[vi];
                float foot = Footprint(cat, variant, info) * sc;

                // ---- the footprint as a capsule on the ground
                Quaternion yawRot = Quaternion.Euler(0f, yaw, 0f);
                Vector3 axis3 = yawRot * new Vector3(info.footprintAxis.x, 0f, info.footprintAxis.y);
                Vector3 center3 = yawRot * new Vector3(info.footprintCenter.x, 0f, info.footprintCenter.y) * sc;
                Vector2 axis = new Vector2(axis3.x, axis3.z);
                Vector2 c = p + new Vector2(center3.x, center3.z);
                float halfLen = cat.kind == ForestAssetKind.Tree ? 0f : info.footprintHalfLength * sc;
                Vector2 a = c - axis * halfLen, b = c + axis * halfLen;

                if (!Allowed(cat, info, a, b, foot, sc)) continue;

                // same-category spacing, and nothing solid underneath
                float spacing = cat.spacing * sc * spacingScale;
                if (hash.Overlaps(a, b, foot, spacing, plan.index, cat.kind == ForestAssetKind.GroundCover ? 0.6f : 0.9f)) continue;

                // ---- transform
                float h = hf.SampleHeight(p);
                Vector3 normal = hf.NormalAt(p);
                Vector3 up = Vector3.Slerp(Vector3.up, normal, cat.alignToSlope);
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, up);
                if (cat.randomTilt > 0f)
                    rot = Quaternion.AngleAxis(cat.randomTilt * tiltT, new Vector3(Mathf.Cos(tiltA), 0f, Mathf.Sin(tiltA))) * rot;
                rot = rot * yawRot;

                var inst = new ForestInstance
                {
                    category = plan.index,
                    variant = vi,
                    position = new Vector3(p.x, h - cat.sink, p.y),
                    rotation = rot,
                    scale = new Vector3(sc, sc * stretch, sc)
                };
                Register(cat, plan.index, info, inst, a, b, foot);
                made++;
            }
            result.categoryCounts[plan.index] += made;
        }

        bool Allowed(ForestAssetCategory cat, ForestPrefabInfo info, Vector2 a, Vector2 b, float foot, float sc)
        {
            Vector2 mid = (a + b) * 0.5f;
            ForestTrailField field = layout.trailField;
            bool solid = cat.kind != ForestAssetKind.GroundCover;

            // trails: keep the distance from every trail edge, and off the monster's routes if it blocks it
            float trailKeep = cat.trailDistance + foot;
            if (MaxAlong(a, b, field.Inside) > -trailKeep) return false;
            if (cat.maxTrailDistance > 0f && field.Inside(mid) < -(cat.maxTrailDistance + foot)) return false;
            if (cat.blocksMonster)
            {
                float keep = foot;
                if (cat.kind == ForestAssetKind.Tree) keep = Mathf.Max(foot, CanopyRadius(info) * sc * (1f - s.scatter.canopyOverhang));
                if (MaxAlong(a, b, field.MonsterInside) > -keep) return false;
                if (InGap(a, b, keep)) return false;
            }

            // clearings
            foreach (ForestClearing c in layout.clearings)
            {
                if ((mid - c.center).sqrMagnitude > (c.radius * 1.5f + 20f) * (c.radius * 1.5f + 20f)) continue;
                if (!cat.inClearings && Mathf.Max(c.Inside(a), c.Inside(b)) > -foot - (solid ? 0.8f : 0f)) return false;
                if (cat.blocksMonster && SegmentPointDistance(c.center, a, b) < s.monster.MinClearingRadius + foot) return false;
            }

            // landmark sites stay free
            if (solid && cat.placement != ForestPlacement.Landmark)
                foreach (ForestLandmarkSite l in layout.landmarks)
                    if (SegmentPointDistance(l.position, a, b) < 4.5f + foot) return false;
            return true;
        }

        static float MaxAlong(Vector2 a, Vector2 b, System.Func<Vector2, float> f)
        {
            if ((a - b).sqrMagnitude < 0.01f) return f(a);
            return Mathf.Max(f(a), Mathf.Max(f(b), f((a + b) * 0.5f)));
        }

        void Register(ForestAssetCategory cat, int catIndex, ForestPrefabInfo info, ForestInstance inst, Vector2 a, Vector2 b, float foot)
        {
            result.instances.Add(inst);
            bool solid = cat.kind == ForestAssetKind.Tree || cat.kind == ForestAssetKind.Obstacle || cat.placement == ForestPlacement.Landmark;
            hash.Add(new ForestSpatialHash.Entry { a = a, b = b, radius = foot, solid = solid, blocksMonster = cat.blocksMonster, category = catIndex });

            Vector2 p = new Vector2(inst.position.x, inst.position.z);
            float sc = inst.scale.x;
            if (cat.canopyShade > 0f)
            {
                Vector3 c = info.bounds.center;
                result.canopies.Add(new ForestCanopy
                {
                    position = p + new Vector2(c.x, c.z) * sc,
                    radius = CanopyRadius(info) * sc,
                    height = c.y * inst.scale.y,
                    shade = cat.canopyShade
                });
            }
            switch (cat.groundMark)
            {
                case ForestGroundMark.Roots: result.stamps.Add(new ForestGroundStamp { position = p, radius = Mathf.Max(1.2f, foot * 3.5f), role = ForestGroundRole.Roots, strength = 0.8f }); break;
                case ForestGroundMark.Needles: result.stamps.Add(new ForestGroundStamp { position = p, radius = CanopyRadius(info) * sc, role = ForestGroundRole.Needles, strength = 0.45f }); break;
                case ForestGroundMark.LeafLitter: result.stamps.Add(new ForestGroundStamp { position = p, radius = CanopyRadius(info) * sc, role = ForestGroundRole.LeafLitter, strength = 0.55f }); break;
                case ForestGroundMark.Moss: result.stamps.Add(new ForestGroundStamp { position = (a + b) * 0.5f, radius = foot * 1.5f + Vector2.Distance(a, b) * 0.5f, role = ForestGroundRole.Moss, strength = 0.6f }); break;
                case ForestGroundMark.Dirt: result.stamps.Add(new ForestGroundStamp { position = (a + b) * 0.5f, radius = foot * 1.6f + Vector2.Distance(a, b) * 0.5f, role = ForestGroundRole.Dirt, strength = 0.6f }); break;
            }
        }

        // ---------------------------------------------------------------- landmarks

        void PlaceLandmarks(CategoryPlan plan)
        {
            ForestAssetCategory cat = plan.cat;
            var rng = new ForestRandom(seed, "landmarks:" + plan.index);
            int made = 0;
            foreach (ForestLandmarkSite site in layout.landmarks)
            {
                if (cat.maxCount > 0 && made >= cat.maxCount) break;
                int vi = rng.Pick(plan.weights, plan.weights.Length);
                if (vi < 0) break;
                ForestPrefabInfo info = plan.infos[vi];
                ForestAssetVariant variant = cat.variants[vi];
                float sc = rng.Range(variant.scale);
                float stretch = rng.Range(variant.heightStretch);
                float foot = Footprint(cat, variant, info) * sc;
                if (hash.Overlaps(site.position, site.position, foot, 0f, plan.index, 0.9f)) continue;
                Quaternion rot = Quaternion.Euler(0f, site.yaw + rng.Range(-25f, 25f), 0f);
                var inst = new ForestInstance
                {
                    category = plan.index,
                    variant = vi,
                    position = new Vector3(site.position.x, hf.SampleHeight(site.position) - cat.sink, site.position.y),
                    rotation = rot,
                    scale = new Vector3(sc, sc * stretch, sc)
                };
                Register(cat, plan.index, info, inst, site.position, site.position, foot);
                made++;
            }
            result.categoryCounts[plan.index] += made;
        }

        // ---------------------------------------------------------------- boundary rocks

        /// <summary>
        /// Big rock formations along the raised boundary, densest where the edge is a cliff, lighter on rocky inclines, none
        /// on the dense-woodland stretches (trees and deadfall cover those). They stand in the slope, lined up with the edge,
        /// so the boundary reads as a rock wall and hides the stretched terrain.
        /// </summary>
        void PlaceBoundaryRocks(CategoryPlan plan)
        {
            ForestAssetCategory cat = plan.cat;
            var rng = new ForestRandom(seed, "boundary rocks:" + plan.index);
            ForestShape shape = layout.shape;
            int samples = Mathf.RoundToInt((shape.HalfSize.x + shape.HalfSize.y) * 4f / 3.5f);   // about every 3.5 m of edge
            Vector2[] edge = shape.SampleEdge(samples, 0f);
            float band = s.boundary.bandWidth;
            int made = 0;
            for (int i = 0; i < edge.Length; i++)
            {
                if (cat.maxCount > 0 && made >= cat.maxCount) break;
                Vector2 p0 = edge[i];
                Vector2 tangent = (edge[(i + 1) % edge.Length] - edge[(i + edge.Length - 1) % edge.Length]).normalized;
                Vector2 outward = p0.normalized;
                Vector3 types = shape.EdgeTypes(p0);
                float rocky = types.x + 0.55f * types.y;
                int count = rocky > 0.75f ? 2 : rocky > 0.35f ? 1 : 0;
                float densityRoll = rng.Value();
                if (densityRoll > cat.density * s.scatter.globalDensity) count = 0;
                for (int k = 0; k < count; k++)
                {
                    int vi = rng.Pick(plan.weights, plan.weights.Length);
                    if (vi < 0) return;
                    float depth = rng.Range(1f, Mathf.Min(band * 0.45f, 16f)) * (k == 0 ? 0.5f : 1.2f);
                    Vector2 p = p0 + outward * depth + tangent * rng.Range(-2f, 2f);
                    ForestAssetVariant variant = cat.variants[vi];
                    ForestPrefabInfo info = plan.infos[vi];
                    float sc = rng.Range(variant.scale) * Mathf.Lerp(0.85f, 1.5f, rocky);
                    float stretch = rng.Range(variant.heightStretch);
                    float foot = Footprint(cat, variant, info) * sc;
                    if (shape.EdgeDistance(p) > -0.5f) continue;                                 // stay in the slope, out of the playable area
                    if (hash.Overlaps(p, p, foot, cat.spacing * sc, plan.index, 0.55f)) continue;
                    float yaw = Yaw(tangent) + rng.Range(-25f, 25f);
                    Vector3 normal = hf.NormalAt(p);
                    Quaternion rot = Quaternion.FromToRotation(Vector3.up, Vector3.Slerp(Vector3.up, normal, cat.alignToSlope)) * Quaternion.Euler(0f, yaw, 0f);
                    var inst = new ForestInstance
                    {
                        category = plan.index,
                        variant = vi,
                        position = new Vector3(p.x, hf.SampleHeight(p) - cat.sink * sc, p.y),
                        rotation = rot,
                        scale = new Vector3(sc, sc * stretch, sc)
                    };
                    Register(cat, plan.index, info, inst, p, p, foot);
                    made++;
                }
            }
            result.categoryCounts[plan.index] += made;
        }

        // ---------------------------------------------------------------- streetlights

        /// <summary>
        /// Old streetlights: most stand at the edge of a trail with the arm over it (high enough for the monster to pass
        /// under), some stand somewhere unexpected deep in the trees. Deeper in the forest more of them are dead or
        /// flickering. Each placed lamp's state is stored in its instance's extra value.
        /// </summary>
        void PlaceStreetlights(CategoryPlan plan)
        {
            ForestAssetCategory cat = plan.cat;
            ForestStreetlightSettings sl = s.streetlights;
            var rng = new ForestRandom(seed, "streetlights:" + plan.index);
            float trailMetres = 0f;
            foreach (ForestTrail t in layout.trails) if (t.kind != ForestTrailKind.PlayerOnly) trailMetres += t.length;
            int total = Mathf.RoundToInt(trailMetres / 100f * sl.perHundredMetres);
            if (cat.maxCount > 0) total = Mathf.Min(total, cat.maxCount);
            int offTrail = Mathf.RoundToInt(total * sl.offTrailShare);
            int onTrail = total - offTrail;
            var placed = new List<Vector2>();
            int made = 0;

            for (int attempt = 0; attempt < total * 60 && made < total; attempt++)
            {
                bool off = made >= onTrail;
                int vi = rng.Pick(plan.weights, plan.weights.Length);
                if (vi < 0) return;
                ForestTrail trail = layout.trails[rng.Range(0, layout.trails.Count)];
                if (trail.kind == ForestTrailKind.PlayerOnly || trail.points.Count < 10) continue;
                int k = rng.Range(3, trail.points.Count - 3);
                Vector2 tangent = (trail.points[k + 2] - trail.points[k - 2]).normalized;
                var side = new Vector2(-tangent.y, tangent.x) * (rng.Chance(0.5f) ? 1f : -1f);
                Vector2 p;
                float yaw;
                if (!off)
                {
                    p = trail.points[k] + side * (trail.HalfWidth + 0.7f);
                    yaw = Yaw(-side);                                      // the arm reaches out over the trail
                }
                else
                {
                    p = trail.points[k] + side * rng.Range(14f, 26f);      // somewhere it has no reason to be
                    yaw = rng.Value() * 360f;
                }

                bool spaced = true;
                foreach (Vector2 q in placed) if (Vector2.Distance(p, q) < sl.spacing) spaced = false;
                if (!spaced) continue;
                if (layout.shape.EdgeDistance(p) < 6f) continue;
                if (layout.trailField.MonsterInside(p) > -0.3f) continue;            // the pole never stands in the monster's way
                if (off && layout.trailField.Inside(p) > -10f) continue;
                if (hf.SlopeDegrees(p) > 25f) continue;
                if (hash.Overlaps(p, p, 0.5f, 0f, plan.index, 0.9f)) continue;

                // the deeper in, the more likely dead or flickering
                float dread = layout.dread.Sample(p);
                float decay = 1f + sl.decayWithDread * dread * 2f;
                float r = rng.Value(), r2 = rng.Value(), r3 = rng.Value();
                ForestLampState state = r < sl.brokenShare * decay ? ForestLampState.Dead
                                      : r2 < sl.flickerShare * decay ? ForestLampState.Flickering : ForestLampState.Working;
                bool green = r3 < sl.greenShare;

                ForestAssetVariant variant = cat.variants[vi];
                float sc = rng.Range(variant.scale);
                var inst = new ForestInstance
                {
                    category = plan.index,
                    variant = vi,
                    position = new Vector3(p.x, hf.SampleHeight(p) - cat.sink, p.y),
                    rotation = Quaternion.Euler(0f, yaw, 0f),
                    scale = new Vector3(sc, sc, sc),
                    extra = (int)state + (green ? 10 : 0)
                };
                Register(cat, plan.index, plan.infos[vi], inst, p, p, 0.5f);
                placed.Add(p);
                made++;
                result.markers.Add(new ForestMarker
                {
                    type = ForestMarkerType.Streetlight, position = inst.position, yaw = yaw, radius = sl.range, dread = dread, value = (int)state
                });
            }
            result.categoryCounts[plan.index] += made;
        }

        // ---------------------------------------------------------------- the monster's clearance

        /// <summary>Walks every monster route (1 m steps) and every clearing's turning circle, recording anything solid in the way.</summary>
        void CheckClearance()
        {
            float half = s.monster.CorridorHalfWidth;
            foreach (ForestTrail t in layout.trails)
            {
                if (!t.monsterAccessible) continue;
                for (int k = 0; k + 1 < t.points.Count; k++)
                {
                    Vector2 p = t.points[k];
                    if (hash.BlocksMonsterNear(p, half * 0.98f))
                    {
                        Vector3 v = At(p);
                        if (result.clearanceViolations.Count == 0 || Vector3.Distance(result.clearanceViolations[result.clearanceViolations.Count - 1], v) > 3f)
                            result.clearanceViolations.Add(v);
                    }
                }
            }
            float turn = s.monster.MinClearingRadius;
            foreach (ForestClearing c in layout.clearings)
                if (hash.BlocksMonsterNear(c.center, turn * 0.98f)) result.clearanceViolations.Add(At(c.center));
        }

        // ---------------------------------------------------------------- helpers

        Vector3 At(Vector2 p)
        {
            return new Vector3(p.x, hf.SampleHeight(p), p.y);
        }

        static float Yaw(Vector2 d)
        {
            return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
        }

        static float SegmentPointDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            return ForestTrailRouter.DistanceToSegment(p, a, b);
        }

        /// <summary>Closest distance between two segments.</summary>
        public static float SegmentDistance(Vector2 p1, Vector2 q1, Vector2 p2, Vector2 q2)
        {
            if ((p1 - q1).sqrMagnitude < 1e-6f) return ForestTrailRouter.DistanceToSegment(p1, p2, q2);
            if ((p2 - q2).sqrMagnitude < 1e-6f) return ForestTrailRouter.DistanceToSegment(p2, p1, q1);
            if (SegmentsCross(p1, q1, p2, q2)) return 0f;
            return Mathf.Min(Mathf.Min(ForestTrailRouter.DistanceToSegment(p1, p2, q2), ForestTrailRouter.DistanceToSegment(q1, p2, q2)),
                             Mathf.Min(ForestTrailRouter.DistanceToSegment(p2, p1, q1), ForestTrailRouter.DistanceToSegment(q2, p1, q1)));
        }

        static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Cross(d - c, a - c), d2 = Cross(d - c, b - c), d3 = Cross(b - a, c - a), d4 = Cross(b - a, d - a);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        static float Cross(Vector2 u, Vector2 v)
        {
            return u.x * v.y - u.y * v.x;
        }
    }

    /// <summary>A 2D grid of buckets for the footprints placed so far, so overlap tests only look at neighbours.</summary>
    public sealed class ForestSpatialHash
    {
        public struct Entry
        {
            public Vector2 a, b;        // capsule end points (equal for a circle)
            public float radius;
            public bool solid;
            public bool blocksMonster;
            public int category;
        }

        readonly Vector2 min;
        readonly float cell;
        readonly int w, h;
        readonly List<int>[] buckets;
        readonly List<Entry> entries = new List<Entry>();
        float maxRadius;

        public ForestSpatialHash(Vector2 min, Vector2 size, float cell)
        {
            this.min = min;
            this.cell = cell;
            w = Mathf.CeilToInt(size.x / cell) + 1;
            h = Mathf.CeilToInt(size.y / cell) + 1;
            buckets = new List<int>[w * h];
        }

        public int Count { get { return entries.Count; } }

        public void Add(Entry e)
        {
            int id = entries.Count;
            entries.Add(e);
            maxRadius = Mathf.Max(maxRadius, e.radius + Vector2.Distance(e.a, e.b) * 0.5f);
            Vector2 lo = Vector2.Min(e.a, e.b) - Vector2.one * e.radius, hi = Vector2.Max(e.a, e.b) + Vector2.one * e.radius;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((lo.x - min.x) / cell), 0, w - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((hi.x - min.x) / cell), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((lo.y - min.y) / cell), 0, h - 1), y1 = Mathf.Clamp(Mathf.FloorToInt((hi.y - min.y) / cell), 0, h - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * w + x;
                    if (buckets[i] == null) buckets[i] = new List<int>(4);
                    buckets[i].Add(id);
                }
        }

        /// <summary>
        /// True if the capsule (a, b, radius) is closer than <paramref name="spacing"/> to an entry of the same category, or
        /// overlaps a solid entry (by more than 1 - overlapFactor of the two radii).
        /// </summary>
        public bool Overlaps(Vector2 a, Vector2 b, float radius, float spacing, int category, float overlapFactor)
        {
            float reach = Mathf.Max(spacing, radius + maxRadius) + 0.1f;
            Vector2 lo = Vector2.Min(a, b) - Vector2.one * reach, hi = Vector2.Max(a, b) + Vector2.one * reach;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((lo.x - min.x) / cell), 0, w - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((hi.x - min.x) / cell), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((lo.y - min.y) / cell), 0, h - 1), y1 = Mathf.Clamp(Mathf.FloorToInt((hi.y - min.y) / cell), 0, h - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    List<int> bucket = buckets[y * w + x];
                    if (bucket == null) continue;
                    for (int k = 0; k < bucket.Count; k++)
                    {
                        Entry e = entries[bucket[k]];
                        bool same = e.category == category;
                        if (!same && !e.solid) continue;
                        float d = ForestScatter.SegmentDistance(a, b, e.a, e.b);
                        if (same && d < spacing) return true;
                        if (e.solid && d < (radius + e.radius) * overlapFactor) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>True if anything that blocks the monster reaches within r of p.</summary>
        public bool BlocksMonsterNear(Vector2 p, float r)
        {
            float reach = r + maxRadius + 0.1f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((p.x - reach - min.x) / cell), 0, w - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((p.x + reach - min.x) / cell), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((p.y - reach - min.y) / cell), 0, h - 1), y1 = Mathf.Clamp(Mathf.FloorToInt((p.y + reach - min.y) / cell), 0, h - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    List<int> bucket = buckets[y * w + x];
                    if (bucket == null) continue;
                    foreach (int id in bucket)
                    {
                        Entry e = entries[id];
                        if (!e.blocksMonster) continue;
                        if (ForestTrailRouter.DistanceToSegment(p, e.a, e.b) < r + e.radius) return true;
                    }
                }
            return false;
        }
    }
}
