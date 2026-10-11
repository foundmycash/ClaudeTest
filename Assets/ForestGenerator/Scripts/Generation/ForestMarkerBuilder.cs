using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>Finds the gameplay markers that follow from the layout: clearings, junctions, long sightlines, blind corners...</summary>
    public static class ForestMarkerBuilder
    {
        public static List<ForestMarker> FromLayout(ForestLayout layout, ForestHeightField hf, ForestSettings s, int seed)
        {
            var markers = new List<ForestMarker>();
            System.Func<Vector2, float> h = hf.SampleHeight;

            markers.Add(new ForestMarker { type = ForestMarkerType.Spawn, position = At(layout.spawnPosition, h), yaw = layout.spawnYaw, radius = 2f, dread = 0f });

            foreach (ForestClearing c in layout.clearings)
            {
                if (c.trailhead) continue;
                markers.Add(new ForestMarker { type = c.heart ? ForestMarkerType.Heart : ForestMarkerType.Clearing, position = At(c.center, h), radius = c.radius, dread = c.dread });
            }

            foreach (ForestNode n in layout.nodes)
            {
                if (n.degree >= 3)
                    markers.Add(new ForestMarker { type = ForestMarkerType.Junction, position = At(n.position, h), radius = 6f, dread = layout.dread.Sample(n.position) });
                if (n.kind == ForestNodeKind.DeadEnd)
                    markers.Add(new ForestMarker { type = ForestMarkerType.DeadEnd, position = At(n.position, h), radius = 4f, dread = layout.dread.Sample(n.position) });
            }

            foreach (ForestLandmarkSite l in layout.landmarks)
                markers.Add(new ForestMarker { type = ForestMarkerType.Landmark, position = At(l.position, h), yaw = l.yaw, radius = 3f, dread = l.dread, value = (float)l.kind });

            FindSightlines(layout, h, markers);
            FindAmbientSpots(layout, h, seed, markers);
            return markers;
        }

        static Vector3 At(Vector2 p, System.Func<Vector2, float> height)
        {
            return new Vector3(p.x, height(p), p.y);
        }

        static float Yaw(Vector2 d)
        {
            return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Long straight stretches (the trail stays within 1.5 m of the straight line for 40 m or more) and sharp bends
        /// (the direction changes by more than 55 degrees within about 18 m).
        /// </summary>
        static void FindSightlines(ForestLayout layout, System.Func<Vector2, float> h, List<ForestMarker> markers)
        {
            const float Spacing = ForestLayoutBuilder.TrailPointSpacing;
            int reach = Mathf.RoundToInt(40f / Spacing);
            int half = Mathf.RoundToInt(9f / Spacing);
            var reveals = new List<Vector2>();
            var corners = new List<Vector2>();

            foreach (ForestTrail t in layout.trails)
            {
                List<Vector2> p = t.points;

                for (int i = 0; i + reach < p.Count; i += 4)
                {
                    // extend the window as long as the trail stays straight
                    int j = i + reach;
                    if (!Straight(p, i, j)) continue;
                    while (j + 4 < p.Count && Straight(p, i, j + 4)) j += 4;
                    bool near = false;
                    foreach (Vector2 r in reveals) if (Vector2.Distance(r, p[i]) < 30f) near = true;
                    if (!near)
                    {
                        reveals.Add(p[i]);
                        markers.Add(new ForestMarker
                        {
                            type = ForestMarkerType.RevealPoint, position = At(p[i], h), yaw = Yaw(p[j] - p[i]),
                            radius = 2f, dread = t.dread[i], value = (j - i) * Spacing
                        });
                    }
                    i = j;
                }

                for (int i = half; i + half < p.Count; i += 2)
                {
                    Vector2 a = (p[i] - p[i - half]).normalized;
                    Vector2 b = (p[i + half] - p[i]).normalized;
                    float angle = Vector2.SignedAngle(a, b);
                    if (Mathf.Abs(angle) < 55f) continue;
                    bool near = false;
                    foreach (Vector2 c in corners) if (Vector2.Distance(c, p[i]) < 20f) near = true;
                    if (near) continue;
                    corners.Add(p[i]);
                    markers.Add(new ForestMarker { type = ForestMarkerType.BlindCorner, position = At(p[i], h), yaw = Yaw(a), radius = t.HalfWidth, dread = t.dread[i], value = angle });
                }
            }
        }

        static bool Straight(List<Vector2> p, int i, int j)
        {
            Vector2 a = p[i], b = p[j];
            for (int k = i + 1; k < j; k++)
                if (ForestTrailRouter.DistanceToSegment(p[k], a, b) > 1.5f) return false;
            return true;
        }

        /// <summary>Spots deep in the trees, at least 14 m from any trail and 45 m from each other.</summary>
        static void FindAmbientSpots(ForestLayout layout, System.Func<Vector2, float> h, int seed, List<ForestMarker> markers)
        {
            var rng = new ForestRandom(seed, "ambient");
            Vector2 half = layout.shape.HalfSize;
            var spots = new List<Vector2>();
            for (int attempt = 0; attempt < 3000; attempt++)
            {
                var p = new Vector2(rng.Range(-half.x, half.x), rng.Range(-half.y, half.y));
                if (layout.shape.EdgeDistance(p) < 8f || layout.trailField.Inside(p) > -14f) continue;
                bool near = false;
                foreach (Vector2 q in spots) if (Vector2.Distance(p, q) < 45f) near = true;
                foreach (ForestClearing c in layout.clearings) if (c.Inside(p) > -6f) near = true;
                if (near) continue;
                spots.Add(p);
                markers.Add(new ForestMarker { type = ForestMarkerType.AmbientSound, position = At(p, h) + Vector3.up * 2f, radius = 8f, dread = layout.dread.Sample(p) });
            }
        }
    }
}
