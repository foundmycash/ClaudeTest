using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// A fine grid (0.5 m) that answers "how far inside a trail is this spot?" in constant time.
    ///   Inside        = max over all trails of (half width - distance to its centre line): positive on a trail, 0 at its
    ///                   edge, negative beside it. Overlapping trails and junctions merge naturally.
    ///   MonsterInside = the same for the monster's routes only, using the monster corridor width where that is wider.
    /// Values further than <see cref="Range"/> metres from every trail are clamped.
    /// </summary>
    public sealed class ForestTrailField
    {
        public const float Range = 24f;

        public readonly int width, height;
        public readonly float cell;
        public readonly Vector2 origin;
        readonly float[] inside;
        readonly float[] monsterInside;
        readonly int[] trailIndex;
        readonly int[] pointIndex;

        public ForestTrailField(Vector2 min, Vector2 size, float cell)
        {
            this.cell = cell;
            origin = min;
            width = Mathf.CeilToInt(size.x / cell) + 1;
            height = Mathf.CeilToInt(size.y / cell) + 1;
            int n = width * height;
            inside = new float[n];
            monsterInside = new float[n];
            trailIndex = new int[n];
            pointIndex = new int[n];
            for (int i = 0; i < n; i++)
            {
                inside[i] = -Range;
                monsterInside[i] = -Range;
                trailIndex[i] = -1;
            }
        }

        public void Build(List<ForestTrail> trails, float monsterCorridorHalf)
        {
            for (int t = 0; t < trails.Count; t++)
            {
                ForestTrail trail = trails[t];
                float hw = trail.HalfWidth;
                float mhw = trail.monsterAccessible ? Mathf.Max(hw, monsterCorridorHalf) : -1f;
                float reach = Mathf.Max(hw, mhw) + Range;
                for (int k = 0; k + 1 < trail.points.Count; k++)
                {
                    Vector2 a = trail.points[k], b = trail.points[k + 1];
                    Vector2 ab = b - a;
                    float len2 = Mathf.Max(1e-8f, ab.sqrMagnitude);
                    Vector2 lo = Vector2.Min(a, b) - Vector2.one * reach;
                    Vector2 hi = Vector2.Max(a, b) + Vector2.one * reach;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((lo.x - origin.x) / cell));
                    int x1 = Mathf.Min(width - 1, Mathf.CeilToInt((hi.x - origin.x) / cell));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt((lo.y - origin.y) / cell));
                    int y1 = Mathf.Min(height - 1, Mathf.CeilToInt((hi.y - origin.y) / cell));
                    for (int y = y0; y <= y1; y++)
                    {
                        float py = origin.y + y * cell;
                        for (int x = x0; x <= x1; x++)
                        {
                            float px = origin.x + x * cell;
                            float u = Mathf.Clamp01(((px - a.x) * ab.x + (py - a.y) * ab.y) / len2);
                            float dx = px - (a.x + ab.x * u), dy = py - (a.y + ab.y * u);
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            int i = y * width + x;
                            float v = hw - d;
                            if (v > inside[i])
                            {
                                inside[i] = v;
                                trailIndex[i] = t;
                                pointIndex[i] = u < 0.5f ? k : k + 1;
                            }
                            if (mhw > 0f)
                            {
                                float mv = mhw - d;
                                if (mv > monsterInside[i]) monsterInside[i] = mv;
                            }
                        }
                    }
                }
            }
        }

        float SampleArray(float[] arr, Vector2 p)
        {
            float fx = Mathf.Clamp((p.x - origin.x) / cell, 0f, width - 1.001f);
            float fy = Mathf.Clamp((p.y - origin.y) / cell, 0f, height - 1.001f);
            int x = (int)fx, y = (int)fy;
            float tx = fx - x, ty = fy - y;
            int i = y * width + x;
            float a = arr[i] + (arr[i + 1] - arr[i]) * tx;
            float b = arr[i + width] + (arr[i + width + 1] - arr[i + width]) * tx;
            return a + (b - a) * ty;
        }

        /// <summary>Metres inside the nearest trail (negative = beside it).</summary>
        public float Inside(Vector2 p)
        {
            return SampleArray(inside, p);
        }

        /// <summary>Metres inside the nearest monster corridor (negative = beside it).</summary>
        public float MonsterInside(Vector2 p)
        {
            return SampleArray(monsterInside, p);
        }

        /// <summary>The trail and centre-line point that the nearest grid node belongs to. False when no trail is within range.</summary>
        public bool Nearest(Vector2 p, out int trail, out int point)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt((p.x - origin.x) / cell), 0, width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt((p.y - origin.y) / cell), 0, height - 1);
            int i = y * width + x;
            trail = trailIndex[i];
            point = pointIndex[i];
            return trail >= 0;
        }
    }
}
