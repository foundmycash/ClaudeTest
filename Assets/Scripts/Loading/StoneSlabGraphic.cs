using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LoadingScreen
{
    /// <summary>
    /// The loading screen's stone slab, drawn as a UI mesh that is cut into shards (a jittered Voronoi layout with
    /// slightly jagged borders). While the slab is intact the shards line up exactly and nothing shows; during
    /// <see cref="Crumble"/> cracks open along the shard borders, the shards break loose one after another starting at
    /// the crack origin, tumble away under gravity and erode into dust.
    ///
    /// All the slab's appearance (rock, plaque, carvings, molten channel) is the shader's job (Loading/Stone); the mesh
    /// only carries, per vertex, the position the vertex had at rest (uv0) and the shard state (uv1..uv3, see the shader).
    /// The canvas must pass TexCoord1..3 to the shader; <see cref="LoadingScreenUI"/> makes sure of that.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class StoneSlabGraphic : MaskableGraphic
    {
        /// <summary>A shard just broke loose.</summary>
        public struct Release
        {
            public Vector2 center;     // slab-local rest position
            public float area;         // canvas units squared
            public float coverage;     // 0..1, how much of the shard is stone (not empty margin)
            public Vector2 outward;    // direction away from the crack origin
            public float speed;
        }

        [Header("Layout (must match the baked textures)")]
        public Vector2 slabSize = new Vector2(1240f, 440f);
        public int seed = 11;
        [Tooltip("Roughly one big shard per cell.")]
        public Vector2Int cells = new Vector2Int(11, 5);
        [Tooltip("Extra small shards near the crack origin.")]
        [Min(0)] public int fineShards = 16;
        [Tooltip("Readable coverage mask of the slab outline (one texel = 10 canvas units). Optional.")]
        public Texture2D coverageMask;

        [Header("Crumble timing (seconds)")]
        [Tooltip("When the first shard breaks loose.")]
        public float firstRelease = 0.28f;
        [Tooltip("Time between the first and the last shard breaking loose.")]
        public float releaseSpread = 0.72f;
        [Tooltip("Cracks start to glow this long before a shard lets go.")]
        public float crackLead = 0.3f;
        [Tooltip("How long a loose shard lasts before it is gone.")]
        public Vector2 shardLife = new Vector2(0.55f, 0.85f);

        [Header("Motion (canvas units)")]
        public float gravity = 3600f;
        public Vector2 outwardSpeed = new Vector2(20f, 120f);

        public event Action<Release> ShardReleased;

        public int ShardCount => shards.Count;
        public bool Crumbling { get; private set; }
        /// <summary>Time (seconds since the crumble began) at which the last shard is gone.</summary>
        public float Duration { get; private set; }

        class Shard
        {
            public Vector2[] ring;
            public Vector2[] inward;     // per ring segment: unit vector from the border towards the inside
            public float[] depth;        // per ring segment: distance from the centroid to that segment
            public Vector2 centroid;
            public float area, coverage, rand, releaseTime, life, spinZ, tiltX, tiltY, zSpeed;
            public bool stone;
            public Vector2 velocity, outward;
            public bool loose;

            // state
            public Vector3 offset;
            public float angle, tx, ty, scale = 1f, dissolve, damage, alpha = 1f;
        }

        readonly List<Shard> shards = new List<Shard>();
        Vector2 origin;
        float shadowFade = 1f;

        public override Texture mainTexture => s_WhiteTexture;

        static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // ------------------------------------------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Builds the shard layout. <paramref name="crackOrigin"/> is in slab-local canvas units.</summary>
        public void Rebuild(Vector2 crackOrigin)
        {
            BuildLayout(crackOrigin);
            SetVerticesDirty();
        }

        void BuildLayout(Vector2 crackOrigin)
        {
            origin = crackOrigin;
            Crumbling = false;
            shadowFade = 1f;
            shards.Clear();

            var rnd = new System.Random(seed);
            var sites = new List<Vector2>();
            Vector2 half = slabSize * 0.5f;
            Vector2 cell = new Vector2(slabSize.x / cells.x, slabSize.y / cells.y);
            for (int j = 0; j < cells.y; j++)
                for (int i = 0; i < cells.x; i++)
                {
                    float jx = ((float)rnd.NextDouble() - 0.5f) * 0.78f, jy = ((float)rnd.NextDouble() - 0.5f) * 0.78f;
                    sites.Add(new Vector2(-half.x + (i + 0.5f + jx) * cell.x, -half.y + (j + 0.5f + jy) * cell.y));
                }
            for (int k = 0; k < fineShards; k++)
            {
                Vector2 s = crackOrigin + new Vector2(Gauss(rnd) * 85f, Gauss(rnd) * 60f);
                s.x = Mathf.Clamp(s.x, -half.x + 20f, half.x - 20f);
                s.y = Mathf.Clamp(s.y, -half.y + 20f, half.y - 20f);
                bool tooClose = false;
                foreach (Vector2 other in sites)
                    if ((other - s).sqrMagnitude < 26f * 26f) { tooClose = true; break; }
                if (!tooClose)
                    sites.Add(s);
            }

            // Voronoi cells by clipping the slab rectangle with the bisector of every other site
            var rect = new List<Vector2>
            {
                new Vector2(-half.x, -half.y), new Vector2(half.x, -half.y), new Vector2(half.x, half.y), new Vector2(-half.x, half.y)
            };
            var cellPolys = new List<List<Vector2>>();
            for (int a = 0; a < sites.Count; a++)
            {
                var poly = new List<Vector2>(rect);
                for (int b = 0; b < sites.Count && poly.Count > 2; b++)
                {
                    if (a == b) continue;
                    poly = Clip(poly, (sites[a] + sites[b]) * 0.5f, sites[a] - sites[b]);
                }
                if (poly.Count >= 3)
                    cellPolys.Add(poly);
            }

            float maxDist = 1f;
            foreach (Vector2 corner in rect)
                maxDist = Mathf.Max(maxDist, Vector2.Distance(corner, crackOrigin));

            Duration = 0f;
            foreach (List<Vector2> poly in cellPolys)
            {
                var shard = MakeShard(poly, half, rnd);
                if (shard == null)
                    continue;

                // timing: the crack spreads outward from the origin, with some chaos
                float u = Mathf.Clamp01(Vector2.Distance(shard.centroid, crackOrigin) / (maxDist * 0.9f));
                shard.releaseTime = firstRelease + releaseSpread * Mathf.Clamp01(u * 0.86f + shard.rand * 0.14f);
                shard.life = Mathf.Lerp(shardLife.x, shardLife.y, (float)rnd.NextDouble());
                Vector2 away = shard.centroid - crackOrigin;
                shard.outward = away.sqrMagnitude > 1f ? away.normalized : Vector2.up;
                float speed = Mathf.Lerp(outwardSpeed.x, outwardSpeed.y, (float)rnd.NextDouble());
                shard.velocity = shard.outward * speed + new Vector2(((float)rnd.NextDouble() - 0.5f) * 40f, 30f + (float)rnd.NextDouble() * 70f);
                shard.spinZ = ((float)rnd.NextDouble() - 0.5f) * 3.2f;
                shard.tiltX = ((float)rnd.NextDouble() - 0.5f) * 2.4f;
                shard.tiltY = ((float)rnd.NextDouble() - 0.5f) * 2.4f;
                shard.zSpeed = 40f + (float)rnd.NextDouble() * 160f;
                if (shard.stone)
                    Duration = Mathf.Max(Duration, shard.releaseTime + shard.life * 0.96f);
                shards.Add(shard);
            }
        }

        static float Gauss(System.Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        static List<Vector2> Clip(List<Vector2> poly, Vector2 m, Vector2 n)
        {
            var res = new List<Vector2>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                float da = Vector2.Dot(a - m, n), db = Vector2.Dot(b - m, n);
                if (da >= 0f) res.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    float t = da / (da - db);
                    res.Add(a + (b - a) * t);
                }
            }
            return res;
        }

        static int EdgeKey(Vector2 p)
        {
            unchecked { return Mathf.RoundToInt(p.x * 20f) * 73856093 ^ Mathf.RoundToInt(p.y * 20f) * 19349663; }
        }

        static float KeyRand(int key, int salt)
        {
            unchecked
            {
                uint h = (uint)(key * 2654435761u + salt * 40503u);
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                return (h & 0xFFFF) / 65535f;
            }
        }

        Shard MakeShard(List<Vector2> poly, Vector2 half, System.Random rnd)
        {
            // clean up near-duplicate points
            var clean = new List<Vector2>();
            foreach (Vector2 p in poly)
                if (clean.Count == 0 || (p - clean[clean.Count - 1]).sqrMagnitude > 0.01f)
                    clean.Add(p);
            if (clean.Count > 1 && (clean[0] - clean[clean.Count - 1]).sqrMagnitude <= 0.01f)
                clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3)
                return null;

            // jagged borders: both neighbours derive the same wobble from the edge's end points
            var ring = new List<Vector2>();
            for (int i = 0; i < clean.Count; i++)
            {
                Vector2 a = clean[i], b = clean[(i + 1) % clean.Count];
                ring.Add(a);
                float len = Vector2.Distance(a, b);
                bool onRim = (Mathf.Abs(a.x - b.x) < 0.01f && Mathf.Abs(Mathf.Abs(a.x) - half.x) < 0.01f)
                          || (Mathf.Abs(a.y - b.y) < 0.01f && Mathf.Abs(Mathf.Abs(a.y) - half.y) < 0.01f);
                if (onRim || len < 16f)
                    continue;

                bool aIsLo = a.x < b.x - 0.001f || (Mathf.Abs(a.x - b.x) <= 0.001f && a.y < b.y);
                Vector2 lo = aIsLo ? a : b, hi = aIsLo ? b : a;
                int key = EdgeKey(lo) ^ (EdgeKey(hi) * 31);
                Vector2 d = hi - lo;
                Vector2 nrm = new Vector2(-d.y, d.x).normalized;
                float amp = Mathf.Min(len * 0.11f, 9f);
                Vector2 p1 = lo + d * 0.36f + nrm * ((KeyRand(key, 1) - 0.5f) * 2f * amp);
                Vector2 p2 = lo + d * 0.70f + nrm * ((KeyRand(key, 2) - 0.5f) * 2f * amp);
                if (aIsLo) { ring.Add(p1); ring.Add(p2); }
                else { ring.Add(p2); ring.Add(p1); }
            }

            var s = new Shard { ring = ring.ToArray(), rand = (float)rnd.NextDouble() };

            // area and centroid
            float area = 0f; Vector2 c = Vector2.zero;
            for (int i = 0; i < s.ring.Length; i++)
            {
                Vector2 a = s.ring[i], b = s.ring[(i + 1) % s.ring.Length];
                float cross = a.x * b.y - b.x * a.y;
                area += cross; c += (a + b) * cross;
            }
            area *= 0.5f;
            if (Mathf.Abs(area) < 4f)
                return null;
            s.area = Mathf.Abs(area);
            s.centroid = c / (6f * area);

            // per-segment inward direction and the centroid's distance to the segment
            s.inward = new Vector2[s.ring.Length];
            s.depth = new float[s.ring.Length];
            for (int i = 0; i < s.ring.Length; i++)
            {
                Vector2 a = s.ring[i], b = s.ring[(i + 1) % s.ring.Length];
                Vector2 e = (b - a).normalized;
                Vector2 nrm = new Vector2(-e.y, e.x);
                if (Vector2.Dot(nrm, s.centroid - a) < 0f) nrm = -nrm;
                s.inward[i] = nrm;
                s.depth[i] = Mathf.Max(1f, Mathf.Abs(Vector2.Dot(s.centroid - a, nrm)));
            }

            // how much of it is stone
            s.coverage = Coverage(s);
            s.stone = s.coverage > 0.03f;
            return s;
        }

        float Coverage(Shard s)
        {
            if (coverageMask == null)
                return 1f;
            Vector2 min = s.ring[0], max = s.ring[0];
            foreach (Vector2 p in s.ring) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            int hit = 0, inside = 0;
            const int n = 7;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    Vector2 q = new Vector2(Mathf.Lerp(min.x, max.x, (i + 0.5f) / n), Mathf.Lerp(min.y, max.y, (j + 0.5f) / n));
                    if (!Contains(s.ring, q)) continue;
                    inside++;
                    if (coverageMask.GetPixelBilinear(q.x / slabSize.x + 0.5f, q.y / slabSize.y + 0.5f).r > 0.5f) hit++;
                }
            if (inside == 0)
                return coverageMask.GetPixelBilinear(s.centroid.x / slabSize.x + 0.5f, s.centroid.y / slabSize.y + 0.5f).r > 0.5f ? 1f : 0f;
            return hit / (float)inside;
        }

        static bool Contains(Vector2[] poly, Vector2 p)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    c = !c;
            return c;
        }

        // ------------------------------------------------------------------------------------------------------
        // Animation
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Back to the intact slab.</summary>
        public void Restore()
        {
            Crumbling = false;
            shadowFade = 1f;
            foreach (Shard s in shards)
            {
                s.loose = false;
                s.offset = Vector3.zero; s.angle = s.tx = s.ty = 0f; s.scale = 1f; s.dissolve = s.damage = 0f; s.alpha = 1f;
            }
            SetVerticesDirty();
        }

        /// <summary>Drop shadow strength 0..1 (the shader's _Shadow follows it).</summary>
        public float ShadowFade => shadowFade;

        /// <summary>Poses every shard for <paramref name="t"/> seconds after the crumble began.</summary>
        public void Crumble(float t)
        {
            Crumbling = true;
            shadowFade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.35f));

            foreach (Shard s in shards)
            {
                if (!s.stone)
                {
                    // margin only holds the soft shadow
                    s.alpha = shadowFade;
                    continue;
                }

                float since = t - s.releaseTime;
                if (since < 0f)
                {
                    // cracks open and glow, the stone trembles a little
                    float lead = Mathf.Clamp01((t - (s.releaseTime - crackLead)) / crackLead);
                    s.damage = lead * lead * (3f - 2f * lead);
                    float shake = s.damage * s.damage * 1.4f;
                    s.offset = new Vector3(Mathf.Sin(t * 71f + s.rand * 40f) * shake, Mathf.Cos(t * 83f + s.rand * 17f) * shake, 0f);
                    s.angle = Mathf.Sin(t * 57f + s.rand * 9f) * 0.003f * s.damage;
                    continue;
                }

                if (!s.loose)
                {
                    s.loose = true;
                    ShardReleased?.Invoke(new Release
                    {
                        center = s.centroid, area = s.area, coverage = s.coverage, outward = s.outward, speed = s.velocity.magnitude
                    });
                }

                float k = Mathf.Clamp01(since / s.life);
                s.damage = 1f;
                float fall = since * since;
                s.offset = new Vector3(s.velocity.x * since, s.velocity.y * since - 0.5f * gravity * fall, s.zSpeed * since);
                s.angle = s.spinZ * since;
                s.tx = s.tiltX * since;
                s.ty = s.tiltY * since;
                s.dissolve = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((k - 0.28f) / 0.72f));
                s.scale = 1f - 0.10f * s.dissolve;
                s.alpha = 1f - Smooth(0.86f, 1f, s.dissolve);
            }
            SetVerticesDirty();
        }

        /// <summary>Slab-local positions of the centres of shards that are falling and not yet mostly dust (for dust trails).</summary>
        public void CollectFallingCenters(List<Vector3> into)
        {
            foreach (Shard s in shards)
                if (s.stone && s.loose && s.dissolve < 0.65f)
                    into.Add(new Vector3(s.centroid.x + s.offset.x, s.centroid.y + s.offset.y, s.offset.z));
        }

        // ------------------------------------------------------------------------------------------------------
        // Mesh
        // ------------------------------------------------------------------------------------------------------

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (shards.Count == 0)
                BuildLayout(new Vector2(456f, -44f));   // editor preview before the first Show()
            int total = 0;
            foreach (Shard sh in shards)
            {
                if (sh.alpha <= 0.002f) continue;

                Quaternion rot = Quaternion.Euler(sh.tx * Mathf.Rad2Deg, sh.ty * Mathf.Rad2Deg, sh.angle * Mathf.Rad2Deg);
                float cos = Mathf.Cos(sh.angle), sin = Mathf.Sin(sh.angle);
                Color32 col = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(sh.alpha * 255f * color.a), 0, 255));
                col.r = (byte)(color.r * 255f); col.g = (byte)(color.g * 255f); col.b = (byte)(color.b * 255f);

                int n = sh.ring.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = sh.ring[i], b = sh.ring[(i + 1) % n];
                    Vector2 inward = sh.inward[i];
                    Vector2 inwardNow = new Vector2(inward.x * cos - inward.y * sin, inward.x * sin + inward.y * cos);

                    AddVertex(vh, sh, rot, a, 0f, inwardNow, cos, sin, col);
                    AddVertex(vh, sh, rot, b, 0f, inwardNow, cos, sin, col);
                    AddVertex(vh, sh, rot, sh.centroid, sh.depth[i], inwardNow, cos, sin, col);
                    vh.AddTriangle(total, total + 1, total + 2);
                    total += 3;
                }
            }
        }

        void AddVertex(VertexHelper vh, Shard sh, Quaternion rot, Vector2 rest, float borderDist, Vector2 inward, float cos, float sin, Color32 col)
        {
            Vector3 local = rot * ((Vector3)(rest - sh.centroid) * sh.scale);
            Vector3 pos = new Vector3(sh.centroid.x, sh.centroid.y, 0f) + sh.offset + local;
            var v = UIVertex.simpleVert;
            v.position = pos;
            v.color = col;
            v.uv0 = new Vector4(rest.x, rest.y, 0f, 0f);
            v.uv1 = new Vector4(borderDist, sh.rand, sh.dissolve, sh.damage);
            v.uv2 = new Vector4(inward.x, inward.y, sh.loose ? 1f : 0f, 0f);
            v.uv3 = new Vector4(cos, sin, 0f, 0f);
            vh.AddVert(v);
        }
    }
}
