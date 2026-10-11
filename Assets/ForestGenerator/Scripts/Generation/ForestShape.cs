using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// The outline of the playable area (a rounded rectangle with a wandering edge) and the raised boundary band around it.
    /// All positions are in the forest's local XZ plane, centred on the generator.
    /// </summary>
    public sealed class ForestShape
    {
        readonly Vector2 half;
        readonly float corner;
        readonly float wobble;
        readonly float wobbleScale;
        readonly int seed;
        readonly ForestBoundarySettings boundary;
        readonly float perimeterRadius;

        /// <summary>Lower-left corner of the generated terrain.</summary>
        public readonly Vector2 terrainMin;

        /// <summary>Size of the generated terrain (the playable area plus the boundary band on every side).</summary>
        public readonly Vector2 terrainSize;

        public ForestShape(ForestSettings settings, int seed, float vertexSpacing)
        {
            ForestWorldSettings w = settings.world;
            half = new Vector2(Mathf.Max(20f, w.playableSize.x), Mathf.Max(20f, w.playableSize.y)) * 0.5f;
            corner = Mathf.Min(w.cornerRadius, Mathf.Min(half.x, half.y) * 0.95f);
            wobble = w.edgeWobble;
            wobbleScale = Mathf.Max(10f, w.edgeWobbleScale);
            this.seed = seed;
            boundary = settings.boundary;
            perimeterRadius = (half.x + half.y) * 0.5f;

            // The terrain reaches past the outer edge of the band (by the wobble plus a little), snapped to whole cells.
            float margin = boundary.bandWidth + wobble + 8f;
            float spacing = Mathf.Max(0.25f, vertexSpacing);
            int cellsX = Mathf.CeilToInt((half.x + margin) * 2f / spacing);
            int cellsZ = Mathf.CeilToInt((half.y + margin) * 2f / spacing);
            terrainSize = new Vector2(cellsX * spacing, cellsZ * spacing);
            terrainMin = -terrainSize * 0.5f;
        }

        public Vector2 HalfSize { get { return half; } }

        /// <summary>Signed distance in metres to the edge of the playable area: positive inside, negative outside.</summary>
        public float EdgeDistance(Vector2 p)
        {
            float r = corner;
            float qx = Mathf.Abs(p.x) - (half.x - r);
            float qy = Mathf.Abs(p.y) - (half.y - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            float outside = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
            float wob = wobble * ForestNoise.Fbm(p.x / wobbleScale, p.y / wobbleScale, seed ^ 0x51A3E, 3);
            return -outside + wob;
        }

        /// <summary>A coordinate that runs around the edge of the map, for noise that should vary along the boundary.</summary>
        Vector2 PerimeterCoord(Vector2 p, float scale)
        {
            float a = Mathf.Atan2(p.y, p.x);
            float k = perimeterRadius / Mathf.Max(1f, scale);
            return new Vector2(Mathf.Cos(a) * k, Mathf.Sin(a) * k);
        }

        /// <summary>How much each kind of edge (x = cliff, y = rocky incline, z = dense woodland) applies here. Adds up to 1.</summary>
        public Vector3 EdgeTypes(Vector2 p)
        {
            Vector2 q = PerimeterCoord(p, boundary.variationScale * 1.3f);
            Vector3 bias = boundary.edgeTypeWeights;
            float sharp = Mathf.Lerp(2.5f, 12f, boundary.edgeTypeSharpness);
            float a = ForestNoise.Fbm(q.x, q.y, seed ^ 0x0C11F, 2) + Mathf.Log(Mathf.Max(0.001f, bias.x)) * 0.35f;
            float b = ForestNoise.Fbm(q.x, q.y, seed ^ 0x1AC11, 2) + Mathf.Log(Mathf.Max(0.001f, bias.y)) * 0.35f;
            float c = ForestNoise.Fbm(q.x, q.y, seed ^ 0x2DE5E, 2) + Mathf.Log(Mathf.Max(0.001f, bias.z)) * 0.35f;
            if (bias.x <= 0f) a = -99f;
            if (bias.y <= 0f) b = -99f;
            if (bias.z <= 0f) c = -99f;
            float m = Mathf.Max(a, Mathf.Max(b, c));
            float ea = Mathf.Exp((a - m) * sharp), eb = Mathf.Exp((b - m) * sharp), ec = Mathf.Exp((c - m) * sharp);
            float s = ea + eb + ec;
            return new Vector3(ea / s, eb / s, ec / s);
        }

        /// <summary>The dominant kind of edge here.</summary>
        public ForestEdgeType EdgeTypeAt(Vector2 p)
        {
            Vector3 w = EdgeTypes(p);
            if (w.x >= w.y && w.x >= w.z) return ForestEdgeType.Cliff;
            return w.y >= w.z ? ForestEdgeType.RockyIncline : ForestEdgeType.DenseWoodland;
        }

        /// <summary>How far in metres the ground is raised here by the boundary (0 inside the playable area).</summary>
        public float BoundaryRise(Vector2 p, float edgeDistance)
        {
            float rise = 0f;
            // A gentle rise at the foot of the slope, just inside the edge.
            if (edgeDistance > 0f)
            {
                if (edgeDistance < 18f) rise = boundary.footRise * ForestNoise.Smooth(18f, 0f, edgeDistance);
                return rise;
            }

            float t = Mathf.Clamp01(-edgeDistance / Mathf.Max(1f, boundary.bandWidth));
            Vector2 q = PerimeterCoord(p, boundary.variationScale);
            float height = Mathf.Lerp(boundary.riseHeight.x, boundary.riseHeight.y, ForestNoise.Fbm(q.x, q.y, seed ^ 0x7E16, 2) * 0.5f + 0.5f);
            Vector3 w = EdgeTypes(p);

            // Cliffs shoot up within the first third of the band; inclines climb steadily; dense woodland is a gentler bank.
            float cliff = Mathf.Pow(ForestNoise.Smooth(0f, 0.32f, t), 0.85f);
            float terrace = ForestNoise.Smooth(0.45f, 0.6f, t) * 0.12f;
            cliff = Mathf.Min(1f, cliff * 0.92f + terrace);
            float incline = ForestNoise.Smooth(0f, 0.8f, t);
            float dense = 0.55f * ForestNoise.Smooth(0f, 1f, t);
            float profile = w.x * cliff + w.y * incline + w.z * dense;

            // Rocky bumps on the slope, strongest in the middle of it.
            float slopeBand = t * (1f - t) * 4f;
            float rough = (ForestNoise.Ridged(p.x / 7f, p.y / 7f, seed ^ 0x3B0C, 3) - 0.45f) * 2.4f * slopeBand * (w.x + w.y * 0.7f + w.z * 0.25f);
            return rise + height * profile + rough + boundary.footRise;
        }

        /// <summary>
        /// Points along the line where the edge distance equals -offset (offset 0 = the playable edge, positive = further out),
        /// found by searching outward-in along rays from the centre. The line is closed (the last point is not repeated).
        /// </summary>
        public Vector2[] SampleEdge(int count, float offset)
        {
            var pts = new Vector2[count];
            float rMax = new Vector2(half.x + boundary.bandWidth + wobble * 2f, half.y + boundary.bandWidth + wobble * 2f).magnitude;
            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float r = rMax;
                while (r > 0f && EdgeDistance(dir * r) < -offset) r -= 1f;
                float lo = Mathf.Max(0f, r), hi = r + 1f;
                for (int k = 0; k < 16; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (EdgeDistance(dir * mid) >= -offset) lo = mid; else hi = mid;
                }
                pts[i] = dir * lo;
            }
            return pts;
        }
    }
}
