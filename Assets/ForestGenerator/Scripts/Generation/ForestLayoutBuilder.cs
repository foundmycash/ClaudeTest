using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Plans the forest: where the trailhead, clearings and dead ends are, which of them the trails link (a spanning tree for
    /// the backbone, a few extra links for loops, short spurs for dead ends), the routes of the trails, how deep in the forest
    /// every spot is ("dread"), the landmark sites and the zone regions.
    /// </summary>
    public static class ForestLayoutBuilder
    {
        public const float TrailPointSpacing = 1.5f;

        struct Edge
        {
            public int a, b;
            public ForestTrailKind kind;
        }

        public static ForestLayout Build(ForestSettings s, int seed, ForestShape shape, ForestGrid baseHeights, List<string> warnings)
        {
            var layout = new ForestLayout { shape = shape };
            var rng = new ForestRandom(seed, "layout");
            Vector2 half = shape.HalfSize;
            Vector2 radiusRange = s.ClearingRadius;
            ForestMonsterProfile monster = s.monster;

            // ---- 1. the trailhead, near the edge, where the player starts
            float trailheadRadius = Mathf.Max(s.clearings.trailheadRadius, monster.MinClearingRadius);
            float startAngle = rng.Angle();
            Vector2 trailhead = PointAtEdgeDistance(shape, new Vector2(Mathf.Cos(startAngle), Mathf.Sin(startAngle)), trailheadRadius + 12f);
            AddClearing(layout, trailhead, trailheadRadius, 0.15f, rng, true);
            AddNode(layout, ForestNodeKind.Trailhead, trailhead, 0);

            // ---- 2. clearings, spread out
            int wanted = Mathf.Max(1, s.clearings.count);
            float spacing = s.clearings.spacing;
            for (int attempt = 0; attempt < wanted * 600 && layout.clearings.Count - 1 < wanted; attempt++)
            {
                var p = new Vector2(rng.Range(-half.x, half.x), rng.Range(-half.y, half.y));
                float r = rng.Range(radiusRange);
                if (shape.EdgeDistance(p) < r + 24f) continue;
                bool ok = true;
                for (int k = 0; k < layout.clearings.Count && ok; k++)
                    if (Vector2.Distance(layout.clearings[k].center, p) < spacing) ok = false;
                if (!ok) continue;
                int ci = AddClearing(layout, p, r, s.clearings.irregularity, rng, false);
                AddNode(layout, ForestNodeKind.Clearing, p, ci);
            }
            if (layout.clearings.Count - 1 < wanted)
                warnings.Add("Only " + (layout.clearings.Count - 1) + " of " + wanted + " clearings fit. Lower the clearing count or spacing, or make the forest bigger.");

            // ---- 3. dead ends
            for (int i = 0; i < s.trails.deadEnds; i++)
            {
                for (int attempt = 0; attempt < 400; attempt++)
                {
                    var p = new Vector2(rng.Range(-half.x, half.x), rng.Range(-half.y, half.y));
                    if (shape.EdgeDistance(p) < 26f) continue;
                    bool ok = true;
                    for (int k = 0; k < layout.nodes.Count && ok; k++)
                        if (Vector2.Distance(layout.nodes[k].position, p) < 45f) ok = false;
                    if (!ok) continue;
                    AddNode(layout, ForestNodeKind.DeadEnd, p, -1);
                    break;
                }
            }

            // ---- 4. which nodes the trails link
            List<Edge> edges = PlanEdges(layout, s, rng);

            // ---- 5. route every trail
            var router = new ForestTrailRouter(shape, baseHeights, s.trails, seed);
            for (int n = 0; n < layout.nodes.Count; n++)
            {
                ForestNode node = layout.nodes[n];
                float r = node.clearing >= 0 ? layout.clearings[node.clearing].radius : 6f;
                router.MarkMeetingPoint(node.position, r + 10f);
            }
            int wobbleSeed = seed ^ 0x6A09E;
            for (int e = 0; e < edges.Count; e++)
            {
                Edge edge = edges[e];
                Vector2 a = layout.nodes[edge.a].position, b = layout.nodes[edge.b].position;
                List<Vector2> raw = router.Route(a, b);
                if (raw == null)
                {
                    warnings.Add("A trail could not be routed and was drawn straight (node " + edge.a + " to " + edge.b + ").");
                    raw = new List<Vector2> { a, b };
                }
                float length;
                List<Vector2> pts = ForestTrailRouter.Resample(ForestTrailRouter.Smooth(raw, 3), TrailPointSpacing, out length);
                Wobble(pts, length, 0.45f, wobbleSeed + e * 31);
                router.MarkTrail(pts, s.trails.separation);

                var trail = new ForestTrail
                {
                    kind = edge.kind,
                    width = s.TrailWidth(edge.kind),
                    monsterAccessible = edge.kind != ForestTrailKind.PlayerOnly,
                    fromNode = edge.a,
                    toNode = edge.b,
                    length = length,
                    points = pts
                };
                layout.trails.Add(trail);
                layout.nodes[edge.a].degree++;
                layout.nodes[edge.b].degree++;
            }

            // ---- 6. how deep in is everything (along the trails, from the trailhead)
            ComputeDread(layout, s);

            // ---- 7. where the player starts: in the trailhead clearing, looking down the first trail
            layout.spawnPosition = trailhead;
            layout.spawnYaw = 0f;
            for (int t = 0; t < layout.trails.Count; t++)
            {
                ForestTrail trail = layout.trails[t];
                if (trail.fromNode != 0 && trail.toNode != 0) continue;
                List<Vector2> p = trail.points;
                int idx = Mathf.Min(p.Count - 1, Mathf.RoundToInt(10f / TrailPointSpacing));
                Vector2 look = trail.fromNode == 0 ? p[idx] : p[p.Count - 1 - idx];
                Vector2 d = look - trailhead;
                if (d.sqrMagnitude > 0.01f) layout.spawnYaw = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                break;
            }

            // ---- 8. the trail distance field, landmark sites and zones
            layout.trailField = new ForestTrailField(shape.terrainMin, shape.terrainSize, 0.5f);
            layout.trailField.Build(layout.trails, monster.CorridorHalfWidth);
            PlaceLandmarks(layout, s, rng);
            layout.zoneMap = ForestZoneMap.Build(shape, s.zones, layout.dread, seed);
            layout.zoneSites.AddRange(layout.zoneMap.sites);
            return layout;
        }

        // ---------------------------------------------------------------- building blocks

        static int AddClearing(ForestLayout layout, Vector2 center, float radius, float irregularity, ForestRandom rng, bool trailhead)
        {
            layout.clearings.Add(new ForestClearing
            {
                center = center,
                radius = radius,
                irregularity = irregularity,
                shapeSeed = (int)rng.NextUInt(),
                trailhead = trailhead
            });
            return layout.clearings.Count - 1;
        }

        static void AddNode(ForestLayout layout, ForestNodeKind kind, Vector2 p, int clearing)
        {
            layout.nodes.Add(new ForestNode { kind = kind, position = p, clearing = clearing });
        }

        /// <summary>The point along a ray from the centre that is the given distance inside the playable edge.</summary>
        static Vector2 PointAtEdgeDistance(ForestShape shape, Vector2 dir, float inset)
        {
            float r = 0f;
            float max = shape.HalfSize.magnitude + 50f;
            while (r < max && shape.EdgeDistance(dir * (r + 1f)) > inset) r += 1f;
            return dir * r;
        }

        static List<Edge> PlanEdges(ForestLayout layout, ForestSettings s, ForestRandom rng)
        {
            var edges = new List<Edge>();
            var core = new List<int>();
            for (int i = 0; i < layout.nodes.Count; i++)
                if (layout.nodes[i].kind == ForestNodeKind.Clearing) core.Add(i);

            var adjacency = new List<int>[layout.nodes.Count];
            for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>();
            System.Action<int, int, ForestTrailKind> link = (a, b, kind) =>
            {
                edges.Add(new Edge { a = a, b = b, kind = kind });
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            };

            // The backbone: a minimum spanning tree over the clearings (Prim), grown from the clearing nearest the trailhead,
            // which the trailhead links to. The trailhead has only that one trail: there is one way in.
            if (core.Count > 0)
            {
                int first = core[0];
                float best = float.MaxValue;
                foreach (int c in core)
                {
                    float d = Vector2.Distance(layout.nodes[0].position, layout.nodes[c].position);
                    if (d < best) { best = d; first = c; }
                }
                link(0, first, ForestTrailKind.Main);

                var inTree = new HashSet<int> { first };
                while (inTree.Count < core.Count)
                {
                    int ba = -1, bb = -1;
                    float bd = float.MaxValue;
                    foreach (int a in inTree)
                    {
                        foreach (int b in core)
                        {
                            if (inTree.Contains(b)) continue;
                            float d = Vector2.Distance(layout.nodes[a].position, layout.nodes[b].position);
                            if (d < bd) { bd = d; ba = a; bb = b; }
                        }
                    }
                    link(ba, bb, ForestTrailKind.Main);
                    inTree.Add(bb);
                }

                // Loops: the shortest extra links that do not run alongside an existing trail out of either clearing.
                int loopTarget = Mathf.RoundToInt(s.trails.loops * core.Count);
                var candidates = new List<KeyValuePair<float, int>>();
                for (int i = 0; i < core.Count; i++)
                    for (int j = i + 1; j < core.Count; j++)
                        candidates.Add(new KeyValuePair<float, int>(Vector2.Distance(layout.nodes[core[i]].position, layout.nodes[core[j]].position), i * 1000 + j));
                candidates.Sort((x, y) => x.Key.CompareTo(y.Key));
                int loopsMade = 0;
                foreach (var cand in candidates)
                {
                    if (loopsMade >= loopTarget) break;
                    if (cand.Key > 220f) break;
                    int a = core[cand.Value / 1000], b = core[cand.Value % 1000];
                    if (adjacency[a].Contains(b)) continue;
                    if (adjacency[a].Count >= 4 || adjacency[b].Count >= 4) continue;
                    if (!AngleClear(layout, adjacency, a, b, 38f) || !AngleClear(layout, adjacency, b, a, 38f)) continue;
                    ForestTrailKind kind = ForestTrailKind.Path;
                    if (s.trails.playerOnlyPaths && rng.Chance(s.trails.playerOnlyShare)) kind = ForestTrailKind.PlayerOnly;
                    link(a, b, kind);
                    loopsMade++;
                }
            }

            // Dead ends: each links to the nearest clearing (the router lets it branch off a trail on the way).
            for (int i = 0; i < layout.nodes.Count; i++)
            {
                if (layout.nodes[i].kind != ForestNodeKind.DeadEnd) continue;
                int best = 0;
                float bd = float.MaxValue;
                for (int j = 0; j < layout.nodes.Count; j++)
                {
                    if (layout.nodes[j].kind != ForestNodeKind.Clearing) continue;
                    float d = Vector2.Distance(layout.nodes[i].position, layout.nodes[j].position);
                    if (d < bd) { bd = d; best = j; }
                }
                link(best, i, ForestTrailKind.DeadEnd);
            }

            // Route the backbone outward from the trailhead first, so later trails join it.
            var order = new List<Edge>();
            var placedNodes = new HashSet<int> { 0 };
            var remaining = new List<Edge>(edges);
            while (remaining.Count > 0)
            {
                int pick = -1;
                for (int i = 0; i < remaining.Count && pick < 0; i++)
                    if (remaining[i].kind == ForestTrailKind.Main && (placedNodes.Contains(remaining[i].a) || placedNodes.Contains(remaining[i].b))) pick = i;
                for (int i = 0; i < remaining.Count && pick < 0; i++)
                    if (remaining[i].kind != ForestTrailKind.DeadEnd && remaining[i].kind != ForestTrailKind.Main) pick = i;
                if (pick < 0) pick = 0;
                Edge e = remaining[pick];
                remaining.RemoveAt(pick);
                // always route away from the part of the network that exists already
                if (!placedNodes.Contains(e.a) && placedNodes.Contains(e.b)) { int t = e.a; e.a = e.b; e.b = t; }
                placedNodes.Add(e.a);
                placedNodes.Add(e.b);
                order.Add(e);
            }
            return order;
        }

        static bool AngleClear(ForestLayout layout, List<int>[] adjacency, int from, int to, float minAngle)
        {
            Vector2 dir = (layout.nodes[to].position - layout.nodes[from].position).normalized;
            foreach (int other in adjacency[from])
            {
                Vector2 d = (layout.nodes[other].position - layout.nodes[from].position).normalized;
                if (Vector2.Angle(dir, d) < minAngle) return false;
            }
            return true;
        }

        /// <summary>A gentle sideways wiggle, faded out at the ends so the trail still meets its clearings.</summary>
        static void Wobble(List<Vector2> pts, float length, float amplitude, int seed)
        {
            if (pts.Count < 3) return;
            var offsets = new Vector2[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                float s = length * i / (pts.Count - 1);
                float taper = Mathf.Clamp01(Mathf.Min(s, length - s) / 8f);
                Vector2 tangent = (pts[Mathf.Min(i + 1, pts.Count - 1)] - pts[Mathf.Max(i - 1, 0)]).normalized;
                var normal = new Vector2(-tangent.y, tangent.x);
                offsets[i] = normal * (amplitude * taper * ForestNoise.Gradient(s / 9f, 0.5f, seed));
            }
            for (int i = 0; i < pts.Count; i++) pts[i] += offsets[i];
        }

        // ---------------------------------------------------------------- dread

        static void ComputeDread(ForestLayout layout, ForestSettings s)
        {
            int n = layout.nodes.Count;
            var dist = new float[n];
            var done = new bool[n];
            for (int i = 0; i < n; i++) dist[i] = float.MaxValue;
            dist[0] = 0f;
            for (int iter = 0; iter < n; iter++)
            {
                int u = -1;
                for (int i = 0; i < n; i++)
                    if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0 || dist[u] == float.MaxValue) break;
                done[u] = true;
                foreach (ForestTrail t in layout.trails)
                {
                    int v = t.fromNode == u ? t.toNode : t.toNode == u ? t.fromNode : -1;
                    if (v >= 0 && dist[u] + t.length < dist[v]) dist[v] = dist[u] + t.length;
                }
            }

            float max = 1f;
            for (int i = 0; i < n; i++)
            {
                if (dist[i] == float.MaxValue) dist[i] = 0f;
                layout.nodes[i].distance = dist[i];
                max = Mathf.Max(max, dist[i]);
            }

            // the heart of the forest: the clearing furthest in
            float heartDist = -1f;
            for (int i = 0; i < n; i++)
            {
                ForestNode node = layout.nodes[i];
                if (node.kind == ForestNodeKind.Clearing && node.distance > heartDist)
                {
                    heartDist = node.distance;
                    layout.heartClearing = node.clearing;
                }
            }
            if (layout.heartClearing >= 0) layout.clearings[layout.heartClearing].heart = true;

            foreach (ForestTrail t in layout.trails)
            {
                t.dread.Clear();
                float s0 = 0f;
                for (int k = 0; k < t.points.Count; k++)
                {
                    if (k > 0) s0 += Vector2.Distance(t.points[k - 1], t.points[k]);
                    float raw = Mathf.Min(dist[t.fromNode] + s0, dist[t.toNode] + (t.length - s0));
                    t.dread.Add(Mathf.Clamp01(raw / max));
                }
            }
            for (int i = 0; i < n; i++)
            {
                ForestNode node = layout.nodes[i];
                if (node.clearing >= 0) layout.clearings[node.clearing].dread = node.distance / max;
            }

            // Off the trails: the depth of the nearest trail point, plus a little for being away from the trail.
            ForestShape shape = layout.shape;
            ForestGrid g = ForestGrid.Covering(shape.terrainMin, shape.terrainSize, 4f);
            var samplePos = new List<Vector2>();
            var sampleDread = new List<float>();
            foreach (ForestTrail t in layout.trails)
            {
                for (int k = 0; k < t.points.Count; k += 3)
                {
                    samplePos.Add(t.points[k]);
                    sampleDread.Add(t.dread[k]);
                }
            }
            float offTrail = s.atmosphere != null ? s.atmosphere.offTrailDread : 0.25f;
            for (int y = 0; y < g.height; y++)
            {
                for (int x = 0; x < g.width; x++)
                {
                    Vector2 p = g.NodePosition(x, y);
                    float bd = float.MaxValue, bv = 0f;
                    for (int k = 0; k < samplePos.Count; k++)
                    {
                        float d = (samplePos[k] - p).sqrMagnitude;
                        if (d < bd) { bd = d; bv = sampleDread[k]; }
                    }
                    g[x, y] = samplePos.Count > 0 ? Mathf.Clamp01(bv + offTrail * Mathf.Clamp01(Mathf.Sqrt(bd) / 50f)) : 0f;
                }
            }
            layout.dread = g;
        }

        // ---------------------------------------------------------------- landmarks

        static void PlaceLandmarks(ForestLayout layout, ForestSettings s, ForestRandom rng)
        {
            int wanted = s.clearings.landmarks;
            if (wanted <= 0) return;
            ForestTrailField field = layout.trailField;
            float clearOfTrail = s.monster.CorridorHalfWidth + 2.5f;

            // 1. the end of every dead end: the reward for following it
            foreach (ForestTrail t in layout.trails)
            {
                if (layout.landmarks.Count >= wanted) return;
                if (t.kind != ForestTrailKind.DeadEnd) continue;
                List<Vector2> p = t.points;
                Vector2 end = p[p.Count - 1];
                Vector2 dir = (end - p[Mathf.Max(0, p.Count - 4)]).normalized;
                Vector2 pos = end + dir * (t.HalfWidth + 3f);
                layout.landmarks.Add(new ForestLandmarkSite { position = pos, yaw = Yaw(-dir), kind = ForestLandmarkSiteKind.DeadEnd, dread = t.dread[t.dread.Count - 1] });
            }

            // 2. the heart clearing, then the other clearings (never the trailhead)
            var clearingOrder = new List<int>();
            if (layout.heartClearing >= 0) clearingOrder.Add(layout.heartClearing);
            for (int i = 0; i < layout.clearings.Count; i++)
                if (!layout.clearings[i].trailhead && i != layout.heartClearing) clearingOrder.Add(i);

            int clearingPick = 0, trailPick = 0;
            int guard = 0;
            while (layout.landmarks.Count < wanted && guard++ < 200)
            {
                int mode = guard % 3;
                if (mode == 0 && clearingPick < clearingOrder.Count)
                {
                    ForestClearing c = layout.clearings[clearingOrder[clearingPick++]];
                    for (int attempt = 0; attempt < 16; attempt++)
                    {
                        float a = rng.Angle();
                        Vector2 pos = c.center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * c.RadiusAt(a) * 0.6f;
                        if (field.Inside(pos) > -clearOfTrail) continue;
                        layout.landmarks.Add(new ForestLandmarkSite { position = pos, yaw = Yaw(c.center - pos), kind = ForestLandmarkSiteKind.InClearing, dread = c.dread });
                        break;
                    }
                    continue;
                }

                // beside or off a long trail, at a random point away from its ends
                if (layout.trails.Count == 0) break;
                ForestTrail t = layout.trails[(trailPick++ * 7 + rng.Range(0, layout.trails.Count)) % layout.trails.Count];
                if (t.points.Count < 30) continue;
                int k = rng.Range(t.points.Count / 5, t.points.Count * 4 / 5);
                Vector2 tangent = (t.points[Mathf.Min(k + 2, t.points.Count - 1)] - t.points[Mathf.Max(k - 2, 0)]).normalized;
                var side = new Vector2(-tangent.y, tangent.x) * (rng.Chance(0.5f) ? 1f : -1f);
                bool offTrail = mode == 2;
                float dist = offTrail ? rng.Range(14f, 24f) : t.HalfWidth + rng.Range(3.5f, 6f);
                Vector2 spot = t.points[k] + side * dist;
                if (field.Inside(spot) > -clearOfTrail || layout.shape.EdgeDistance(spot) < 12f) continue;
                bool crowded = false;
                foreach (ForestLandmarkSite other in layout.landmarks)
                    if (Vector2.Distance(other.position, spot) < 40f) crowded = true;
                if (crowded) continue;
                layout.landmarks.Add(new ForestLandmarkSite
                {
                    position = spot,
                    yaw = Yaw(t.points[k] - spot),
                    kind = offTrail ? ForestLandmarkSiteKind.OffTrail : ForestLandmarkSiteKind.Trailside,
                    dread = t.dread[k]
                });
            }
        }

        static float Yaw(Vector2 dir)
        {
            return Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
        }
    }
}
