using System.Collections.Generic;
using UnityEngine;

namespace HavocCave
{
    /// <summary>One straight piece of tunnel. Width and height are half-extents and are interpolated a -> b.</summary>
    public struct CaveSegment
    {
        public Vector3 a, b, ab;
        public float invLen2;
        public float wa, wb, ha, hb;
        public int path;              // index into CaveLayout.paths, -1 for a vertical shaft, -2 for a hole of the spawn cave
        public Vector3 min, max;      // bounds padded by how far this segment can influence the surface
    }

    /// <summary>
    /// A solid piece of rock added to the walls, floor or ceiling. Either a round boulder, or a tapered capsule
    /// (stalagmite, stalactite, pillar) running from center to center + axis.
    /// </summary>
    public struct CaveBlob
    {
        public Vector3 center;      // a boulder's middle, or the first end of a formation
        public Vector3 axis;        // from center to the other end (zero for a boulder)
        public float invLen2;       // 1 / |axis|^2, 0 for a boulder
        public float radius;        // radius at center
        public float radiusEnd;     // radius at the other end
        public float waist;         // 0..1: how much a pillar thins out in the middle
        public Vector3 min, max;    // bounds, including the smooth join with the wall
        public bool formation;

        // A rounded box (a ledge or a step in the spawn cave). center is the middle of the box.
        public bool box;
        public Vector3 half;        // half extents
        public float cosY, sinY;    // rotation about the vertical axis
        public float corner;        // how much the box edges are rounded
        public float smooth;        // how softly it merges into the wall

        public static CaveBlob Box(Vector3 center, Vector3 half, float yawRadians, float corner, float smooth)
        {
            float reach = Mathf.Sqrt(half.x * half.x + half.z * half.z) + smooth + 0.5f;
            float up = half.y + smooth + 0.5f;
            return new CaveBlob
            {
                center = center, half = half, box = true, cosY = Mathf.Cos(yawRadians), sinY = Mathf.Sin(yawRadians),
                corner = Mathf.Min(corner, Mathf.Min(half.x, Mathf.Min(half.y, half.z)) * 0.9f), smooth = smooth,
                radius = Mathf.Max(half.x, half.z), radiusEnd = Mathf.Max(half.x, half.z),
                min = center - new Vector3(reach, up, reach), max = center + new Vector3(reach, up, reach)
            };
        }

        public static CaveBlob Sphere(Vector3 c, float r)
        {
            float pad = r + CaveLayout.BlobSmooth + 0.5f;
            return new CaveBlob
            {
                center = c, radius = r, radiusEnd = r,
                min = c - Vector3.one * pad, max = c + Vector3.one * pad
            };
        }

        public static CaveBlob Capsule(Vector3 a, Vector3 b, float ra, float rb, float waist)
        {
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            float pad = Mathf.Max(ra, rb) + CaveLayout.BlobSmooth + 0.5f;
            return new CaveBlob
            {
                center = a, axis = ab, invLen2 = l2 > 1e-6f ? 1f / l2 : 0f,
                radius = ra, radiusEnd = rb, waist = waist, formation = true,
                min = Vector3.Min(a, b) - Vector3.one * pad, max = Vector3.Max(a, b) + Vector3.one * pad
            };
        }

        /// <summary>Roughly how much wall area this adds.</summary>
        public float SurfaceArea()
        {
            if (box) return 4f * half.x * half.z + 2f * (half.x + half.z) * 2f * Mathf.Min(half.y * 2f, 0.6f);   // top and the part of the sides above the floor
            if (invLen2 <= 0f) return Mathf.PI * radius * radius;      // about half of a round rock is exposed
            float len = axis.magnitude;
            float avg = (radius + radiusEnd) * 0.5f * (1f - waist * 0.67f);
            return 2f * Mathf.PI * avg * len * 0.8f;
        }
    }

    /// <summary>A lamp that lights the cave walls (baked into the mesh at generation time).</summary>
    public struct CaveLamp
    {
        public Vector3 position;
        /// <summary>The way the fixture shines (the open side). Zero = all round, like a glass lantern.</summary>
        public Vector3 direction;
    }

    public enum CaveRoomKind
    {
        Dome = 0,        // plain round room
        Hall = 1,        // long and narrow
        Cathedral = 2,   // very tall
        Cavern = 3,      // lumpy and irregular
        Pit = 4,         // the chamber at the bottom of a pit shaft
        Chimney = 5,     // the chamber at the top of a chimney shaft
        Spawn = 6        // the room you design yourself (CaveSpawnCave)
    }

    /// <summary>
    /// A room: flat floor, domed ceiling, elliptical in plan. floorCenter is the middle of the floor.
    /// radius is the shorter half-width; the longer one is radius * stretch, along the direction given by yaw.
    /// </summary>
    public struct CaveRoom
    {
        public Vector3 floorCenter;
        public float radius;
        public float height;
        public float squareness;
        public float stretch;         // 1 = round
        public float cosYaw, sinYaw;  // direction of the long axis in the XZ plane
        public float warp;            // 0 = clean ellipse, ~0.35 = lumpy cavern
        public int noiseSeed;
        public int kind;              // CaveRoomKind
        public Vector3 min, max;      // padded bounds

        public float LongRadius { get { return radius * stretch; } }
        public CaveRoomKind Kind { get { return (CaveRoomKind)kind; } }
    }

    /// <summary>A vertical shaft: a pit down from a room floor, or a chimney up from a room ceiling.</summary>
    public struct CaveShaft
    {
        public Vector3 top, bottom;
        public float radius;
        public bool up;
    }

    /// <summary>A smoothed tunnel centre line.</summary>
    public sealed class CavePath
    {
        public bool isMain;
        public bool isLoop;
        public int parent = -1;
        public readonly List<Vector3> pos = new List<Vector3>();
        public readonly List<float> halfW = new List<float>();
        public readonly List<float> halfH = new List<float>();
        public readonly List<float> dist = new List<float>();   // cumulative arc length

        public float Length { get { return dist.Count > 0 ? dist[dist.Count - 1] : 0f; } }

        public void Sample(float s, out Vector3 p, out Vector3 tangent, out float w, out float h)
        {
            int i = 0;
            while (i < dist.Count - 2 && dist[i + 1] < s) i++;
            float len = dist[i + 1] - dist[i];
            float t = len > 1e-4f ? Mathf.Clamp01((s - dist[i]) / len) : 0f;
            p = Vector3.Lerp(pos[i], pos[i + 1], t);
            tangent = (pos[i + 1] - pos[i]).normalized;
            w = Mathf.Lerp(halfW[i], halfW[i + 1], t);
            h = Mathf.Lerp(halfH[i], halfH[i + 1], t);
        }
    }

    /// <summary>The abstract shape of the cave: tunnel centre lines, rooms, shafts and rock formations. No meshes yet.</summary>
    public sealed class CaveLayout
    {
        public const float BlobSmooth = 0.6f;
        const float SegmentLength = 2f;

        public readonly List<CavePath> paths = new List<CavePath>();
        public CaveSegment[] segments;
        public CaveBlob[] blobs;
        public CaveRoom[] rooms = new CaveRoom[0];
        public CaveShaft[] shafts = new CaveShaft[0];
        public Vector3 min, max;        // padded bounds of everything that can touch the surface
        public Vector3 spawn;           // centre line point where the player starts
        public Vector3 spawnForward;
        public float spawnYaw;          // degrees: which way the player faces at the start (towards the main tunnel)
        public float totalLength;

        // The spawn cave. Inside this sphere the wall noise comes from the spawn cave's own seed, so the shape never changes with the map seed.
        public bool hasSpawnCave;
        public Vector3 spawnCaveCenter;
        public float spawnCaveInner;
        public int spawnCaveSeed;
        const float SpawnCaveFade = 8f;
        struct HoleSeg { public Vector3 a, b; public float r; }
        readonly List<HoleSeg> holeSegs = new List<HoleSeg>();

        /// <summary>1 inside the spawn cave, fading to 0 over a few metres outside it.</summary>
        public float SpawnWeight(Vector3 p)
        {
            if (!hasSpawnCave) return 0f;
            float d = (p - spawnCaveCenter).magnitude;
            return 1f - CaveNoise.Smooth(spawnCaveInner, spawnCaveInner + SpawnCaveFade, d);
        }

        bool InSpawnZone(Vector3 p, float r)
        {
            return hasSpawnCave && (p - spawnCaveCenter).magnitude < spawnCaveInner + SpawnCaveFade * 0.5f + r;
        }

        sealed class Exit
        {
            public Vector3 start;      // the far end of the hole: where the generated tunnel begins
            public float yaw;
            public Vector2 half;       // half width, half height of the hole
        }

        /// <summary>Keeps the wandering tunnels out of the spawn cave (except through its exits).</summary>
        sealed class Obstacle
        {
            readonly Vector3 center;
            readonly float rx, rz, cos, sin, y0, y1, exitR2;
            readonly Vector3[] exits;

            public Obstacle(CaveRoom room, float margin, List<Exit> list)
            {
                center = room.floorCenter;
                rx = room.radius * room.stretch * (1f + room.warp) + margin;
                rz = room.radius * (1f + room.warp) + margin;
                cos = room.cosYaw;
                sin = room.sinYaw;
                y0 = room.floorCenter.y - 6f;
                y1 = room.floorCenter.y + room.height + 6f;
                exitR2 = margin * margin * 4f;
                exits = new Vector3[list.Count];
                for (int i = 0; i < exits.Length; i++) exits[i] = list[i].start;
            }

            public bool Blocks(Vector3 p)
            {
                if (p.y < y0 || p.y > y1) return false;
                float dx = p.x - center.x;
                float dz = p.z - center.z;
                float lx = dx * cos + dz * sin;
                float lz = -dx * sin + dz * cos;
                if (lx * lx / (rx * rx) + lz * lz / (rz * rz) >= 1f) return false;
                for (int i = 0; i < exits.Length; i++)
                    if ((p - exits[i]).sqrMagnitude < exitR2) return false;
                return true;
            }
        }

        sealed class Coarse
        {
            public readonly List<Vector3> p = new List<Vector3>();
            public readonly List<float> w = new List<float>();   // half width
            public readonly List<float> h = new List<float>();   // half height
            public bool main;
            public bool loop;
            public bool reached;
            public int parent = -1;
        }

        static float Rand(System.Random r, float a, float b)
        {
            return a + (float)r.NextDouble() * (b - a);
        }

        public static CaveLayout Build(CaveSettings s, int seed)
        {
            return Build(s, seed, null);
        }

        /// <summary>
        /// spawn = the room you designed (CaveSpawnCave), or null for a random spawn room. The spawn cave's shape does
        /// not depend on the seed; the cave grows out of its exits.
        /// </summary>
        public static CaveLayout Build(CaveSettings s, int seed, CaveSpawnCave spawn)
        {
            var rng = new System.Random(seed);
            var all = new List<Coarse>();
            bool authored = spawn != null && spawn.enabled;

            var layout = new CaveLayout();
            var exits = new List<Exit>();
            Obstacle ob = null;
            CaveRoom spawnRoom = default(CaveRoom);
            if (authored)
            {
                spawnRoom = layout.SetUpSpawnCave(spawn, s, exits);
                ob = new Obstacle(spawnRoom, s.tunnelSpacing * 0.5f + 2f, exits);
            }

            // Main tunnel. A random spawn room is made on the tunnel itself, so the tunnel starts two steps "behind" the spawn;
            // the spawn cave you design is its own room and the tunnel starts at the end of its first exit.
            Vector3 start = authored ? exits[0].start : new Vector3(0f, 0f, -2f * s.stepLength);
            float startYaw = authored ? exits[0].yaw : 0f;
            Vector2 startHalf = authored ? exits[0].half : Vector2.zero;
            float mainLength = s.mainTunnelLength + (authored ? 0f : 2f * s.stepLength);
            Coarse main = Walk(rng, s, seed, all, start, startYaw, mainLength, 1f, authored ? 3 : 4, null, 0f, true, false, Vector3.zero, startHalf, ob);
            main.main = true;
            all.Add(main);

            // Every other exit of the spawn cave starts a side tunnel.
            for (int e = 1; e < exits.Count; e++)
            {
                float exitLen = s.branchLength * Rand(rng, 0.8f, 1.2f);
                float exitBias = s.levelRange > 0.01f ? Rand(rng, 0.3f, 0.9f) * (rng.Next(2) == 0 ? -1f : 1f) : 0f;
                Coarse side = Walk(rng, s, seed, all, exits[e].start, exits[e].yaw, exitLen, 0.85f, 3, new[] { exits[e].start }, exitBias, false, false, Vector3.zero, exits[e].half, ob);
                if (side.p.Count < 4) continue;
                side.parent = 0;
                all.Add(side);
            }

            // Branches. Each picks a level of its own, so some climb and some drop.
            for (int b = 0; b < s.branchCount; b++)
            {
                int parentIndex = (all.Count == 1 || rng.NextDouble() < 0.6) ? 0 : rng.Next(all.Count);
                Coarse parent = all[parentIndex];
                if (parent.p.Count < 8) continue;

                int idx = rng.Next(3, parent.p.Count - 2);
                Vector3 origin = parent.p[idx];
                Vector3 tan = parent.p[idx + 1] - parent.p[idx - 1];
                float baseYaw = Mathf.Atan2(tan.x, tan.z) * Mathf.Rad2Deg;
                float side = rng.Next(2) == 0 ? -1f : 1f;
                float yaw = baseYaw + side * Rand(rng, 55f, 115f);
                float len = s.branchLength * Rand(rng, 0.6f, 1.3f);
                float bias = s.levelRange > 0.01f ? Rand(rng, 0.4f, 1f) * (rng.Next(2) == 0 ? -1f : 1f) : 0f;

                Coarse br = Walk(rng, s, seed, all, origin, yaw, len, 0.85f, 1, new[] { origin }, bias, false, false, Vector3.zero, Vector2.zero, ob);
                if (br.p.Count < 4) continue;
                br.parent = parentIndex;
                all.Add(br);
            }

            // Loops: a tunnel that sets out from one place and steers round to meet another.
            for (int l = 0; l < s.loops; l++)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    int ai = rng.Next(all.Count);
                    int bi = rng.Next(all.Count);
                    Coarse A = all[ai];
                    Coarse B = all[bi];
                    if (A.p.Count < 10 || B.p.Count < 10) continue;
                    int ia = rng.Next(4, A.p.Count - 3);
                    int ib = rng.Next(4, B.p.Count - 3);
                    if (ai == bi && Mathf.Abs(ia - ib) < 14) continue;

                    Vector3 from = A.p[ia];
                    Vector3 to = B.p[ib];
                    float gap = Vector3.Distance(from, to);
                    if (gap < s.tunnelSpacing * 2.2f || gap > Mathf.Max(60f, s.mainTunnelLength * 0.55f)) continue;

                    Vector3 tan = A.p[ia + 1] - A.p[ia - 1];
                    float side = rng.Next(2) == 0 ? -1f : 1f;
                    float yaw = Mathf.Atan2(tan.x, tan.z) * Mathf.Rad2Deg + side * Rand(rng, 60f, 100f);
                    Coarse lp = Walk(rng, s, seed, all, from, yaw, 0f, 0.85f, 1, new[] { from, to }, 0f, false, true, to, Vector2.zero, ob);
                    if (!lp.reached || lp.p.Count < 5) continue;
                    lp.loop = true;
                    lp.parent = ai;
                    all.Add(lp);
                    break;
                }
            }

            for (int i = 0; i < all.Count; i++)
                layout.paths.Add(Smooth(all[i], s));

            if (authored)
            {
                layout.spawn = spawnRoom.floorCenter + new Vector3(spawn.spawnPoint.x, 1.4f, spawn.spawnPoint.y);
                layout.spawnYaw = exits[0].yaw;
                float yr = exits[0].yaw * Mathf.Deg2Rad;
                layout.spawnForward = new Vector3(Mathf.Sin(yr), 0f, Mathf.Cos(yr));
            }
            else
            {
                layout.spawn = main.p[2];
                Vector3 fwd = main.p[3] - main.p[1];
                fwd.y = 0f;
                layout.spawnForward = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            }

            layout.BuildRooms(rng, s, authored, spawnRoom);
            layout.BuildShafts(rng, s);
            layout.BuildBlobs(rng, s);
            CaveBlob[] rocks = layout.blobs;
            layout.blobs = new CaveBlob[0];                          // judge everything against the bare tunnels and rooms
            layout.BuildSegments(s);

            var field = new CaveField(layout, s, seed);
            List<CaveBlob> kept = layout.KeepAnchoredBlobs(field, rocks);
            kept.AddRange(layout.BuildFormations(field, rng, s));
            if (authored) kept.AddRange(layout.BuildLedges(spawn));
            layout.blobs = kept.ToArray();
            layout.ExpandBounds();
            return layout;
        }

        /// <summary>
        /// Turns the CaveSpawnCave into a room, hole tubes and exits, and marks the area around it as seed-independent.
        /// Everything here depends only on the spawn cave's own settings.
        /// </summary>
        CaveRoom SetUpSpawnCave(CaveSpawnCave spawn, CaveSettings s, List<Exit> exits)
        {
            Vector3 sz = new Vector3(Mathf.Max(2.5f, spawn.size.x), Mathf.Max(3f, spawn.size.y), Mathf.Max(2.5f, spawn.size.z));
            float rShort = Mathf.Min(sz.x, sz.z);
            float rLong = Mathf.Max(sz.x, sz.z);
            float yaw = sz.x >= sz.z ? 0f : Mathf.PI * 0.5f;                       // the room's long axis
            CaveRoom room = MakeRoom(Vector3.zero, rShort, sz.y, rLong / rShort, yaw, spawn.lumpiness, CaveRoomKind.Spawn,
                                     spawn.squareness, spawn.shapeSeed, s.roughness + 3.5f);

            Vector3 center = new Vector3(0f, sz.y * 0.5f, 0f);
            float inner = Mathf.Max(rLong * (1f + spawn.lumpiness), sz.y);
            if (spawn.holes != null)
            {
                foreach (CaveSpawnHole h in spawn.holes)
                {
                    if (h == null) continue;
                    Vector3 a = h.Start;
                    Vector3 b = h.End;
                    holeSegs.Add(new HoleSeg { a = a, b = b, r = h.radius });
                    inner = Mathf.Max(inner, Vector3.Distance(center, b) + h.radius);
                    if (h.leadsToCave) exits.Add(new Exit { start = b, yaw = h.yaw, half = new Vector2(h.radius, h.radius * 0.85f) });
                }
            }
            if (exits.Count == 0)
            {
                // A spawn cave with no way out would give an empty map: add the default exit.
                var d = new CaveSpawnHole();
                holeSegs.Add(new HoleSeg { a = d.Start, b = d.End, r = d.radius });
                exits.Add(new Exit { start = d.End, yaw = d.yaw, half = new Vector2(d.radius, d.radius * 0.85f) });
                inner = Mathf.Max(inner, Vector3.Distance(center, d.End) + d.radius);
            }
            if (spawn.ledges != null)
            {
                foreach (CaveSpawnLedge l in spawn.ledges)
                    if (l != null) inner = Mathf.Max(inner, Vector3.Distance(center, new Vector3(l.position.x, 0f, l.position.y)) + Mathf.Max(l.size.x, l.size.y));
            }

            hasSpawnCave = true;
            spawnCaveCenter = center;
            spawnCaveInner = inner + 2f;
            spawnCaveSeed = spawn.shapeSeed * 7919 + 13;
            return room;
        }

        /// <summary>The ledges and steps of the spawn cave as rounded rock boxes. Each is pushed down below the floor so it is always attached.</summary>
        List<CaveBlob> BuildLedges(CaveSpawnCave spawn)
        {
            var list = new List<CaveBlob>();
            if (spawn.ledges == null) return list;
            const float Down = 1.2f;
            foreach (CaveSpawnLedge l in spawn.ledges)
            {
                if (l == null) continue;
                int steps = Mathf.Max(1, l.steps);
                float w = Mathf.Max(0.4f, l.size.x);
                float d = Mathf.Max(0.4f, l.size.y);
                float h = Mathf.Max(0.1f, l.height);
                float tread = d / steps;
                float yawRad = -l.yaw * Mathf.Deg2Rad;
                float c = Mathf.Cos(yawRad);
                float sn = Mathf.Sin(yawRad);
                for (int i = 0; i < steps; i++)
                {
                    float zMin = -d * 0.5f + i * tread;
                    float zMax = d * 0.5f;
                    float top = h * (i + 1);
                    float lz = (zMin + zMax) * 0.5f;
                    Vector3 world = new Vector3(l.position.x - lz * sn, (top - Down) * 0.5f, l.position.y + lz * c);
                    Vector3 half = new Vector3(w * 0.5f, (top + Down) * 0.5f, (zMax - zMin) * 0.5f);
                    list.Add(CaveBlob.Box(world, half, yawRad, 0.05f, 0.18f));
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- walking

        static Coarse Walk(System.Random rng, CaveSettings s, int seed, List<Coarse> all,
                           Vector3 start, float yaw, float length, float widthScale, int straightSteps,
                           Vector3[] anchors, float levelBias, bool isMain, bool hasGoal, Vector3 goal,
                           Vector2 startHalf, Obstacle ob)
        {
            var c = new Coarse();
            int steps = hasGoal
                ? Mathf.CeilToInt(Vector3.Distance(start, goal) / s.stepLength * 2.2f) + 8
                : Mathf.Max(3, Mathf.RoundToInt(length / s.stepLength));
            float phase = Rand(rng, 0f, 100f);
            float turn = 0f;
            float slope = 0f;
            float baseY = start.y;
            int selfGap = Mathf.CeilToInt(s.tunnelSpacing / s.stepLength) + 1;
            Vector3 pos = start;
            float v = s.shapeVariety;

            for (int i = 0; i <= steps; i++)
            {
                // Cross section: it drifts between the min and max, and with Shape Variety it also swings into
                // tall narrow fissures and wide low galleries.
                float wn = 0.5f + 0.5f * CaveNoise.Value(i * 0.4f + phase, 1.7f, 0f, seed);
                float hn = 0.5f + 0.5f * CaveNoise.Value(i * 0.4f + phase, 9.3f, 4f, seed + 5);
                float width = Mathf.Lerp(s.tunnelWidth.x, s.tunnelWidth.y, wn) * widthScale;
                float height = Mathf.Lerp(s.tunnelHeight.x, s.tunnelHeight.y, hn) * Mathf.Lerp(1f, widthScale, 0.5f);
                if (v > 0.001f)
                {
                    float n1 = CaveNoise.Value(i * 0.22f + phase, 5.3f, 2.1f, seed + 31);
                    float n2 = CaveNoise.Value(i * 0.22f + phase, 8.9f, 6.2f, seed + 47);
                    float n3 = CaveNoise.Value(i * 0.14f + phase, 3.7f, 9.1f, seed + 59);
                    float fissure = v * CaveNoise.Smooth(0.18f, 0.55f, n3);
                    float gallery = v * CaveNoise.Smooth(0.18f, 0.55f, -n3);
                    width *= Mathf.Exp(v * 0.45f * n1) * Mathf.Lerp(1f, 0.5f, fissure) * Mathf.Lerp(1f, 1.7f, gallery);
                    height *= Mathf.Exp(v * 0.5f * n2) * Mathf.Lerp(1f, 1.9f, fissure) * Mathf.Lerp(1f, 0.8f, gallery);
                }
                width = Mathf.Max(width, 1.9f);       // a person always fits
                height = Mathf.Max(height, 2.4f);
                if (startHalf.x > 0f && i < 3)
                {
                    // Leaving a hole of the spawn cave: start exactly as wide and tall as the hole and ease into the normal size.
                    float k = i / 3f;
                    width = Mathf.Lerp(startHalf.x * 2f, width, k);
                    height = Mathf.Lerp(startHalf.y * 2f, height, k);
                }

                c.p.Add(pos);
                c.w.Add(width * 0.5f);
                c.h.Add(height * 0.5f);
                if (i == steps) break;

                if (hasGoal && (goal - pos).magnitude < s.stepLength * 1.4f)
                {
                    c.p.Add(goal);
                    c.w.Add(width * 0.5f);
                    c.h.Add(height * 0.5f);
                    c.reached = true;
                    break;
                }

                // Which height this tunnel is heading for. The main tunnel wanders slowly up and down.
                float levelTarget = baseY + levelBias * s.levelRange;
                if (isMain) levelTarget += s.levelRange * 0.55f * CaveNoise.Value(i * 0.045f + phase, 4.4f, 0f, seed + 71);

                bool placed = false;
                for (int attempt = 0; attempt < 8 && !placed; attempt++)
                {
                    float t;
                    if (i < straightSteps)
                    {
                        t = 0f;
                    }
                    else if (hasGoal)
                    {
                        Vector3 to = goal - pos;
                        float goalYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                        float delta = Mathf.DeltaAngle(yaw, goalYaw);
                        float lim = s.turnAmount * 1.5f;
                        t = Mathf.Clamp(Mathf.Clamp(delta, -lim, lim) + Rand(rng, -1f, 1f) * s.turnAmount * 0.35f, -lim, lim);
                    }
                    else
                    {
                        t = Mathf.Clamp(turn + Rand(rng, -1f, 1f) * s.turnAmount * 0.5f, -s.turnAmount, s.turnAmount);
                    }
                    float candYaw = yaw + t;
                    if (attempt > 0)
                        candYaw += (rng.Next(2) == 0 ? -1f : 1f) * Rand(rng, 20f, 45f) * attempt * 0.5f;   // steer away from whatever blocked us

                    float sl;
                    if (i < straightSteps)
                    {
                        sl = 0f;
                    }
                    else if (hasGoal)
                    {
                        float horiz = Mathf.Max(s.stepLength, new Vector2(goal.x - pos.x, goal.z - pos.z).magnitude);
                        sl = Mathf.Clamp((goal.y - pos.y) / horiz, -s.slope, s.slope);
                    }
                    else
                    {
                        sl = Mathf.Clamp(slope + Rand(rng, -1f, 1f) * s.slope * 0.5f + (levelTarget - pos.y) * 0.04f, -s.slope, s.slope);
                    }

                    float rad = candYaw * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Sin(rad), sl, Mathf.Cos(rad)).normalized;
                    Vector3 cand = pos + dir * s.stepLength;
                    if (ob != null && ob.Blocks(cand)) continue;
                    if (!IsFree(cand, c, all, s, selfGap, anchors)) continue;

                    turn = t;
                    yaw = candYaw;
                    slope = sl;
                    pos = cand;
                    placed = true;
                }
                if (!placed) break;   // boxed in: this tunnel ends here
            }
            return c;
        }

        static bool IsFree(Vector3 cand, Coarse self, List<Coarse> all, CaveSettings s, int selfGap, Vector3[] anchors)
        {
            float min2 = s.tunnelSpacing * s.tunnelSpacing;
            float ignoreR = s.tunnelSpacing * 2.2f;
            float ignore2 = ignoreR * ignoreR;

            for (int a = 0; a < all.Count; a++)
            {
                List<Vector3> pts = all[a].p;
                for (int i = 0; i < pts.Count; i++)
                {
                    if ((pts[i] - cand).sqrMagnitude >= min2) continue;
                    if (NearSameAnchor(cand, pts[i], anchors, ignore2)) continue;   // the junction we grew out of, or the one we are joining
                    return false;
                }
            }

            for (int i = 0; i < self.p.Count - selfGap; i++)
            {
                if ((self.p[i] - cand).sqrMagnitude >= min2) continue;
                if (NearSameAnchor(cand, self.p[i], anchors, ignore2)) continue;
                return false;
            }
            return true;
        }

        static bool NearSameAnchor(Vector3 a, Vector3 b, Vector3[] anchors, float ignore2)
        {
            if (anchors == null) return false;
            for (int k = 0; k < anchors.Length; k++)
                if ((a - anchors[k]).sqrMagnitude < ignore2 && (b - anchors[k]).sqrMagnitude < ignore2) return true;
            return false;
        }

        // ---------------------------------------------------------------- rooms

        static CaveRoom MakeRoom(Vector3 floor, float radius, float height, float stretch, float yaw, float warp,
                                 CaveRoomKind kind, float squareness, int noiseSeed, float pad)
        {
            var r = new CaveRoom
            {
                floorCenter = floor, radius = radius, height = height, squareness = squareness,
                stretch = Mathf.Max(1f, stretch), cosYaw = Mathf.Cos(yaw), sinYaw = Mathf.Sin(yaw),
                warp = warp, noiseSeed = noiseSeed, kind = (int)kind
            };
            float horiz = radius * r.stretch * (1f + warp) + pad;
            float up = height * (1f + 0.5f * warp) + pad;
            r.min = new Vector3(floor.x - horiz, floor.y - pad, floor.z - horiz);
            r.max = new Vector3(floor.x + horiz, floor.y + up, floor.z + horiz);
            return r;
        }

        static float ReachOf(CaveRoom r)
        {
            return r.radius * r.stretch * (1f + r.warp * 0.5f);
        }

        /// <summary>
        /// Rooms: one at the spawn, one every roomSpacing metres along the main tunnel, one at the end of every tunnel
        /// (and one half way along each loop). Each room's floor is level with the tunnel floor where it sits, so the
        /// tunnel walks straight in. A room that would cut across a different tunnel is shrunk, or skipped.
        /// </summary>
        void BuildRooms(System.Random rng, CaveSettings s, bool authored, CaveRoom spawnRoom)
        {
            var list = new List<CaveRoom>();
            if (authored) list.Add(spawnRoom);          // the spawn cave you designed is always room 0
            float pad = s.roughness + 3.5f;
            for (int pi = 0; pi < paths.Count; pi++)
            {
                CavePath path = paths[pi];
                float len = path.Length;
                var stops = new List<float>();
                if (path.isMain)
                {
                    float spawnArc = 2f * s.stepLength;
                    if (!authored) stops.Add(spawnArc);
                    for (float d = s.roomSpacing; d < len - 14f; d += s.roomSpacing)
                        if (Mathf.Abs(d - spawnArc) > s.roomSpacing * 0.5f) stops.Add(d);
                    if (len > 24f) stops.Add(len);
                }
                else if (path.isLoop)
                {
                    if (len > 30f) stops.Add(len * 0.5f);
                }
                else if (len > 24f)
                {
                    stops.Add(len);
                }

                for (int k = 0; k < stops.Count; k++)
                {
                    Vector3 p, tan;
                    float hw, hh;
                    path.Sample(stops[k], out p, out tan, out hw, out hh);
                    Vector3 floor = new Vector3(p.x, p.y - hh, p.z);
                    bool isSpawn = !authored && path.isMain && k == 0;

                    // Roll every random number first, so the stream does not depend on which size ends up fitting.
                    float rr = Rand(rng, s.roomRadius.x, s.roomRadius.y);
                    float rh = Rand(rng, s.roomHeight.x, s.roomHeight.y);
                    float roll = (float)rng.NextDouble();
                    float pick = (float)rng.NextDouble();
                    float rs = (float)rng.NextDouble();
                    float ry = Rand(rng, -1f, 1f);
                    int noiseSeed = rng.Next(1, 1000000);

                    CaveRoomKind kind = CaveRoomKind.Dome;
                    if (!isSpawn && roll < s.roomVariety * 0.75f)
                        kind = pick < 0.34f ? CaveRoomKind.Hall : (pick < 0.66f ? CaveRoomKind.Cathedral : CaveRoomKind.Cavern);

                    float along = Mathf.PI * 0.5f - Mathf.Atan2(tan.x, tan.z);   // long axis parallel to the tunnel
                    for (int shrink = 0; shrink < 3; shrink++)
                    {
                        float scale = (path.isMain ? 1f : 0.85f) * (shrink == 0 ? 1f : (shrink == 1 ? 0.8f : 0.65f));
                        CaveRoom room = CreateRoom(kind, floor, rr * scale, rh * Mathf.Lerp(1f, scale, 0.5f), rs, ry, along, noiseSeed, s, pad);
                        if (!RoomFits(room, list, pi, stops[k])) continue;
                        list.Add(room);
                        break;
                    }
                }
            }
            rooms = list.ToArray();
        }

        static CaveRoom CreateRoom(CaveRoomKind kind, Vector3 floor, float rr, float rh, float rs, float ry, float along,
                                   int noiseSeed, CaveSettings s, float pad)
        {
            switch (kind)
            {
                case CaveRoomKind.Hall:
                    return MakeRoom(floor, rr * 0.7f, rh * 0.9f, 1.8f + rs * 0.9f, along + (ry > 0f ? 0f : Mathf.PI * 0.5f) + ry * 0.25f,
                                    0.1f, kind, 0.1f, noiseSeed, pad);
                case CaveRoomKind.Cathedral:
                    return MakeRoom(floor, rr * 0.85f, Mathf.Min(rh * (2f + rs * 0.8f), 16f), 1f + 0.3f * rs, ry * Mathf.PI,
                                    0.12f, kind, 0.1f, noiseSeed, pad);
                case CaveRoomKind.Cavern:
                    return MakeRoom(floor, rr * 1.1f, rh * 1.25f, 1f + 0.5f * rs, ry * Mathf.PI,
                                    0.34f + 0.08f * rs, kind, 0.1f, noiseSeed, pad);
                default:
                    return MakeRoom(floor, rr, rh, 1f, 0f, 0f, CaveRoomKind.Dome, s.roomSquareness, noiseSeed, pad);
            }
        }

        /// <summary>
        /// Does this room clash with the rooms already placed, or cut across a tunnel that is not at its own floor level?
        /// A tunnel that arrives at the same floor level simply opens into the room, which is what we want.
        /// </summary>
        bool RoomFits(CaveRoom rm, List<CaveRoom> placed, int ownPath, float ownArc)
        {
            float reach = ReachOf(rm);
            for (int j = 0; j < placed.Count; j++)
            {
                CaveRoom o = placed[j];
                bool vertical = rm.floorCenter.y < o.floorCenter.y + o.height + 1.5f && o.floorCenter.y < rm.floorCenter.y + rm.height + 1.5f;
                if (!vertical) continue;
                float dx = o.floorCenter.x - rm.floorCenter.x;
                float dz = o.floorCenter.z - rm.floorCenter.z;
                if (Mathf.Sqrt(dx * dx + dz * dz) < (reach + ReachOf(o)) * 0.85f) return false;
            }

            for (int pj = 0; pj < paths.Count; pj++)
            {
                CavePath path = paths[pj];
                for (int i = 0; i < path.pos.Count; i++)
                {
                    if (pj == ownPath && Mathf.Abs(path.dist[i] - ownArc) < reach * 1.5f + 8f) continue;
                    Vector3 q = path.pos[i];
                    float dx = q.x - rm.floorCenter.x;
                    float dz = q.z - rm.floorCenter.z;
                    float r = reach + path.halfW[i] + 1f;
                    if (dx * dx + dz * dz > r * r) continue;

                    float qFloor = q.y - path.halfH[i];
                    float qTop = q.y + path.halfH[i];
                    if (qTop < rm.floorCenter.y - 0.5f || qFloor > rm.floorCenter.y + rm.height + 0.5f) continue;   // passes well above or below
                    if (Mathf.Abs(qFloor - rm.floorCenter.y) < 1.6f) continue;                                       // arrives at floor level: fine
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- shafts

        /// <summary>Is the vertical column free of other tunnels, rooms and shafts?</summary>
        bool ColumnFree(List<CaveRoom> roomList, List<CaveShaft> shaftList, int ignoreRoom, Vector3 center, float horiz, float yMin, float yMax)
        {
            for (int pj = 0; pj < paths.Count; pj++)
            {
                CavePath path = paths[pj];
                for (int i = 0; i < path.pos.Count; i++)
                {
                    Vector3 q = path.pos[i];
                    if (q.y + path.halfH[i] < yMin || q.y - path.halfH[i] > yMax) continue;
                    float dx = q.x - center.x;
                    float dz = q.z - center.z;
                    float r = horiz + path.halfW[i];
                    if (dx * dx + dz * dz < r * r) return false;
                }
            }
            for (int j = 0; j < roomList.Count; j++)
            {
                if (j == ignoreRoom) continue;
                CaveRoom o = roomList[j];
                if (o.floorCenter.y + o.height < yMin || o.floorCenter.y > yMax) continue;
                float dx = o.floorCenter.x - center.x;
                float dz = o.floorCenter.z - center.z;
                float r = horiz + ReachOf(o);
                if (dx * dx + dz * dz < r * r) return false;
            }
            for (int j = 0; j < shaftList.Count; j++)
            {
                CaveShaft o = shaftList[j];
                if (Mathf.Max(o.top.y, o.bottom.y) < yMin || Mathf.Min(o.top.y, o.bottom.y) > yMax) continue;
                float dx = o.top.x - center.x;
                float dz = o.top.z - center.z;
                float r = horiz + o.radius + 2f;
                if (dx * dx + dz * dz < r * r) return false;
            }
            return true;
        }

        /// <summary>
        /// Vertical shafts. A pit drops from a room floor into a lower chamber; a chimney climbs out of a room ceiling
        /// into a small chamber above. Both are placed only where nothing else is in the way.
        /// </summary>
        void BuildShafts(System.Random rng, CaveSettings s)
        {
            var list = new List<CaveShaft>();
            if (s.shafts > 0 && rooms.Length > 1)
            {
                var roomList = new List<CaveRoom>(rooms);
                float pad = s.roughness + 3.5f;
                float scale = Mathf.Clamp(0.5f * (s.tunnelWidth.x + s.tunnelWidth.y) / 4.3f, 0.7f, 2.2f);   // shafts grow with the tunnels

                for (int n = 0; n < s.shafts; n++)
                {
                    for (int attempt = 0; attempt < 14; attempt++)
                    {
                        int hostIndex = rng.Next(1, rooms.Length);                  // never the spawn room
                        CaveRoom host = rooms[hostIndex];
                        if (host.radius < 4.2f) continue;

                        bool up = rng.NextDouble() < 0.45;
                        float r = Mathf.Min(Rand(rng, 1.7f, 2.7f) * scale, host.radius * 0.4f);
                        float ang = Rand(rng, 0f, Mathf.PI * 2f);
                        float off = Rand(rng, 0.15f, 0.5f);
                        float lx = Mathf.Cos(ang) * off * host.radius * host.stretch;
                        float lz = Mathf.Sin(ang) * off * host.radius;
                        Vector3 pos = new Vector3(host.floorCenter.x + lx * host.cosYaw - lz * host.sinYaw,
                                                  host.floorCenter.y,
                                                  host.floorCenter.z + lx * host.sinYaw + lz * host.cosYaw);
                        float floorY = host.floorCenter.y;
                        int chamberSeed = rng.Next(1, 1000000);

                        if (!up)
                        {
                            float depth = Rand(rng, 8f, 15f);
                            float cr = Rand(rng, 4.5f, 6.5f) * Mathf.Lerp(1f, scale, 0.5f);
                            float ch = Rand(rng, 4f, 5.5f);
                            float cFloor = floorY - depth;
                            if (!ColumnFree(roomList, list, hostIndex, pos, r + 2.5f, cFloor - 1f, floorY - 0.3f)) continue;
                            if (!ColumnFree(roomList, list, -1, new Vector3(pos.x, cFloor, pos.z), cr + 2f, cFloor - 1f, cFloor + ch + 1f)) continue;

                            roomList.Add(MakeRoom(new Vector3(pos.x, cFloor, pos.z), cr, ch, 1f, 0f, 0.15f, CaveRoomKind.Pit, 0.2f, chamberSeed, pad));
                            list.Add(new CaveShaft { top = new Vector3(pos.x, floorY + 1.6f, pos.z), bottom = new Vector3(pos.x, cFloor + 1.8f, pos.z), radius = r, up = false });
                        }
                        else
                        {
                            float rise = Rand(rng, 8f, 14f);
                            float cr = Rand(rng, 3.5f, 5.5f) * Mathf.Lerp(1f, scale, 0.5f);
                            float ch = Rand(rng, 3.5f, 5f);
                            float cFloor = floorY + host.height + rise;
                            float baseY = floorY + host.height * 0.4f;
                            // Only the rock above the room matters: the room's own tunnel is down at floor level.
                            if (!ColumnFree(roomList, list, hostIndex, pos, r + 2.5f, floorY + host.height + 0.5f, cFloor + ch + 1f)) continue;

                            roomList.Add(MakeRoom(new Vector3(pos.x, cFloor, pos.z), cr, ch, 1f, 0f, 0.15f, CaveRoomKind.Chimney, 0.2f, chamberSeed, pad));
                            list.Add(new CaveShaft { top = new Vector3(pos.x, cFloor + 1.8f, pos.z), bottom = new Vector3(pos.x, baseY, pos.z), radius = r, up = true });
                        }
                        break;
                    }
                }
                rooms = roomList.ToArray();
            }
            shafts = list.ToArray();
        }

        // ---------------------------------------------------------------- smoothing

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        static CavePath Smooth(Coarse c, CaveSettings s)
        {
            var path = new CavePath { isMain = c.main, isLoop = c.loop, parent = c.parent };
            int n = c.p.Count;
            int sub = Mathf.Max(1, Mathf.RoundToInt(s.stepLength / SegmentLength));

            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p0 = c.p[Mathf.Max(i - 1, 0)];
                Vector3 p1 = c.p[i];
                Vector3 p2 = c.p[i + 1];
                Vector3 p3 = c.p[Mathf.Min(i + 2, n - 1)];
                for (int k = 0; k < sub; k++)
                {
                    float t = (float)k / sub;
                    float e = t * t * (3f - 2f * t);
                    path.pos.Add(CatmullRom(p0, p1, p2, p3, t));
                    path.halfW.Add(Mathf.Lerp(c.w[i], c.w[i + 1], e));
                    path.halfH.Add(Mathf.Lerp(c.h[i], c.h[i + 1], e));
                }
            }
            path.pos.Add(c.p[n - 1]);
            path.halfW.Add(c.w[n - 1]);
            path.halfH.Add(c.h[n - 1]);

            float acc = 0f;
            path.dist.Add(0f);
            for (int i = 1; i < path.pos.Count; i++)
            {
                acc += (path.pos[i] - path.pos[i - 1]).magnitude;
                path.dist.Add(acc);
            }
            return path;
        }

        // ---------------------------------------------------------------- rocks & segments

        void BuildBlobs(System.Random rng, CaveSettings s)
        {
            var list = new List<CaveBlob>();
            if (s.rocksPer10m > 0.001f)
            {
                float interval = 10f / s.rocksPer10m;
                for (int pi = 0; pi < paths.Count; pi++)
                {
                    CavePath path = paths[pi];
                    float next = interval * Rand(rng, 0.3f, 1f);
                    for (int i = 1; i < path.pos.Count; i++)
                    {
                        if (path.dist[i] < next) continue;
                        next += interval * Rand(rng, 0.5f, 1.5f);

                        Vector3 dir = path.pos[i] - path.pos[i - 1];
                        dir.y = 0f;
                        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
                        Vector3 right = Vector3.Cross(Vector3.up, dir);
                        Vector3 p = path.pos[i];
                        float hw = path.halfW[i];
                        float hh = path.halfH[i];

                        bool mound = rng.NextDouble() < 0.3;
                        float r = Rand(rng, s.rockRadius.x, s.rockRadius.y) * (mound ? 0.7f : 1f);
                        Vector3 center;
                        if (mound)
                        {
                            center = p + right * (Rand(rng, -0.5f, 0.5f) * hw) + Vector3.down * (hh - r * 0.15f);
                        }
                        else
                        {
                            float side = rng.Next(2) == 0 ? -1f : 1f;
                            float intrusion = Mathf.Min(Rand(rng, 0.25f, 0.55f) * r, 0.35f * hw);
                            center = p + right * (side * (hw + r - intrusion)) + Vector3.up * (Rand(rng, -0.8f, 0.5f) * hh);
                        }
                        list.Add(CaveBlob.Sphere(center, r));
                    }
                }
            }
            blobs = list.ToArray();
        }

        // ---------------------------------------------------------------- size / poly count estimates

        /// <summary>Measured: triangles produced per (square metre of wall / voxel size squared). See EstimateTriangles.</summary>
        public const float TrianglesPerVoxelArea = 2.7f;

        /// <summary>Smallest voxel size the poly count setting will pick, in metres.</summary>
        public const float MinVoxelSize = 0.15f;

        static float Perimeter(float a, float b, float squareness)
        {
            float ellipse = Mathf.PI * (3f * (a + b) - Mathf.Sqrt((3f * a + b) * (a + 3f * b)));   // Ramanujan
            float box = 4f * (a + b) * 0.93f;                                                      // a rounded box is a little shorter than a sharp one
            return Mathf.Lerp(ellipse, box, squareness);
        }

        /// <summary>
        /// Approximate area in square metres of the cave walls (tunnel surface plus the rocks sticking out of it).
        /// Wall that lies inside another tunnel, where a side tunnel meets its parent, is not counted.
        /// </summary>
        public float EstimateSurfaceArea(CaveSettings s)
        {
            float area = 0f;
            for (int i = 0; i < segments.Length; i++)
            {
                float w = (segments[i].wa + segments[i].wb) * 0.5f;
                float h = (segments[i].ha + segments[i].hb) * 0.5f;
                float len = segments[i].ab.magnitude;

                // Is this piece of tunnel buried inside a different tunnel?
                Vector3 mid = (segments[i].a + segments[i].b) * 0.5f;
                bool buried = false;
                for (int j = 0; j < segments.Length && !buried; j++)
                {
                    if (segments[j].path == segments[i].path) continue;
                    Vector3 ab = segments[j].ab;
                    float t = Mathf.Clamp01(Vector3.Dot(mid - segments[j].a, ab) * segments[j].invLen2);
                    float room = Mathf.Min(Mathf.Min(segments[j].wa, segments[j].wb), Mathf.Min(segments[j].ha, segments[j].hb));
                    buried = (mid - (segments[j].a + ab * t)).sqrMagnitude < room * room * 0.64f;
                }
                for (int j = 0; j < rooms.Length && !buried; j++)
                {
                    Vector3 d = mid - rooms[j].floorCenter;
                    float rr = rooms[j].radius * Mathf.Sqrt(rooms[j].stretch);
                    buried = d.x * d.x + d.z * d.z < rr * rr * 0.64f && d.y > -0.5f && d.y < rooms[j].height * 0.7f;
                }
                if (!buried) area += len * Perimeter(w, h, s.squareness);
            }
            for (int i = 0; i < rooms.Length; i++)
            {
                float rr = rooms[i].radius * Mathf.Sqrt(rooms[i].stretch);       // the equivalent round room
                float lumps = 1f + rooms[i].warp * 0.5f;
                area += (Mathf.PI * rr * rr +                                    // floor
                         Mathf.PI * rr * (rr + rooms[i].height) * 0.9f) * lumps; // dome
            }
            for (int i = 0; i < blobs.Length; i++)
                area += blobs[i].SurfaceArea();
            return area;
        }

        /// <summary>Roughly how many triangles the cave mesh will have at this voxel size.</summary>
        public float EstimateTriangles(CaveSettings s, float voxelSize)
        {
            return TrianglesPerVoxelArea * EstimateSurfaceArea(s) / (voxelSize * voxelSize);
        }

        /// <summary>
        /// The voxel size that gives roughly targetTriangles triangles for this cave. It is never allowed to get so
        /// coarse that a tunnel is only a couple of voxels tall: past that the walls turn into big flat facets,
        /// textures smear and thin rock breaks into slivers. In that case limited is true and you get more
        /// triangles than you asked for.
        /// </summary>
        public float VoxelSizeForTriangles(CaveSettings s, float targetTriangles, out bool limited)
        {
            float wanted = Mathf.Sqrt(TrianglesPerVoxelArea * EstimateSurfaceArea(s) / Mathf.Max(100f, targetTriangles));
            float coarsest = Mathf.Clamp(0.4f * s.tunnelHeight.x, 0.6f, 1.5f);
            limited = wanted > coarsest;
            return Mathf.Clamp(wanted, MinVoxelSize, coarsest);
        }

        void BuildSegments(CaveSettings s)
        {
            var list = new List<CaveSegment>();
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            totalLength = 0f;

            for (int pi = 0; pi < paths.Count; pi++)
            {
                CavePath path = paths[pi];
                totalLength += path.Length;
                for (int i = 0; i < path.pos.Count - 1; i++)
                {
                    var seg = new CaveSegment
                    {
                        path = pi,
                        a = path.pos[i], b = path.pos[i + 1],
                        wa = path.halfW[i], wb = path.halfW[i + 1],
                        ha = path.halfH[i], hb = path.halfH[i + 1]
                    };
                    AddSegment(list, seg, s);
                }
            }

            for (int i = 0; i < shafts.Length; i++)
            {
                float r = shafts[i].radius;
                AddSegment(list, new CaveSegment { path = -1, a = shafts[i].top, b = shafts[i].bottom, wa = r, wb = r, ha = r, hb = r }, s);
            }

            for (int i = 0; i < holeSegs.Count; i++)
            {
                float r = holeSegs[i].r;
                AddSegment(list, new CaveSegment { path = -2, a = holeSegs[i].a, b = holeSegs[i].b, wa = r, wb = r, ha = r * 0.85f, hb = r * 0.85f }, s);
            }
            segments = list.ToArray();
        }

        void AddSegment(List<CaveSegment> list, CaveSegment seg, CaveSettings s)
        {
            seg.ab = seg.b - seg.a;
            float len2 = seg.ab.sqrMagnitude;
            if (len2 < 1e-6f) return;
            seg.invLen2 = 1f / len2;

            // How far from the centre line can the surface be? Roughness and layering push it out a bit more.
            float maxDim = Mathf.Max(Mathf.Max(seg.wa, seg.wb), Mathf.Max(seg.ha, seg.hb));
            float minDim = Mathf.Min(Mathf.Min(seg.wa, seg.wb), Mathf.Min(seg.ha, seg.hb));
            float reach = maxDim * (1f + (s.roughness + s.strata) / minDim) + 1.5f;
            seg.min = Vector3.Min(seg.a, seg.b) - Vector3.one * reach;
            seg.max = Vector3.Max(seg.a, seg.b) + Vector3.one * reach;
            list.Add(seg);

            min = Vector3.Min(min, seg.min);
            max = Vector3.Max(max, seg.max);
        }

        void ExpandBounds()
        {
            for (int i = 0; i < blobs.Length; i++)
            {
                min = Vector3.Min(min, blobs[i].min);
                max = Vector3.Max(max, blobs[i].max);
            }
            for (int i = 0; i < rooms.Length; i++)
            {
                min = Vector3.Min(min, rooms[i].min);
                max = Vector3.Max(max, rooms[i].max);
            }
        }

        // ---------------------------------------------------------------- anchoring and formations

        static readonly Vector3[] AnchorProbes = BuildAnchorProbes();

        static Vector3[] BuildAnchorProbes()
        {
            var list = new List<Vector3>
            {
                Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
            };
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
                list.Add(new Vector3(x, y, z).normalized);
            return list.ToArray();
        }

        /// <summary>
        /// A boulder is placed against a tunnel wall, but where another tunnel opens into that wall (a junction, or a
        /// room) the wall isn't there, and the boulder would hang in mid air as a detached island of rock.
        /// Keep only boulders that have solid rock behind them: probe the boulder's surface against the bare tunnels.
        /// </summary>
        List<CaveBlob> KeepAnchoredBlobs(CaveField field, CaveBlob[] candidates)
        {
            CaveField.Region region = field.All();
            var kept = new List<CaveBlob>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (InSpawnZone(candidates[i].center, candidates[i].radius)) continue;     // the spawn cave looks the same every time
                int solid = 0;
                for (int k = 0; k < AnchorProbes.Length; k++)
                {
                    Vector3 probe = candidates[i].center + AnchorProbes[k] * (candidates[i].radius * 0.9f);
                    if (field.Density(region, probe) > 0.15f) solid++;
                }
                if (solid >= 3) kept.Add(candidates[i]);
            }
            return kept;
        }

        /// <summary>Is a formation at p (horizontal radius r) clear of tunnels, shafts and other formations?</summary>
        bool FormationClear(Vector3 p, float r, List<Vector4> taken)
        {
            if (InSpawnZone(p, r)) return false;
            for (int i = 0; i < taken.Count; i++)
            {
                float dx = taken[i].x - p.x;
                float dz = taken[i].y - p.z;
                float d = r + taken[i].z + 1f;
                if (dx * dx + dz * dz < d * d) return false;
            }
            for (int i = 0; i < shafts.Length; i++)
            {
                float dx = shafts[i].top.x - p.x;
                float dz = shafts[i].top.z - p.z;
                float d = r + shafts[i].radius + 1.6f;
                if (dx * dx + dz * dz < d * d && p.y > Mathf.Min(shafts[i].top.y, shafts[i].bottom.y) - 3f && p.y < Mathf.Max(shafts[i].top.y, shafts[i].bottom.y) + 3f) return false;
            }
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].path < 0) continue;
                if (p.x < segments[i].min.x || p.x > segments[i].max.x || p.z < segments[i].min.z || p.z > segments[i].max.z) continue;
                float t = Mathf.Clamp01(Vector3.Dot(p - segments[i].a, segments[i].ab) * segments[i].invLen2);
                Vector3 q = segments[i].a + segments[i].ab * t;
                float w = Mathf.Lerp(segments[i].wa, segments[i].wb, t);
                float h = Mathf.Lerp(segments[i].ha, segments[i].hb, t);
                if (Mathf.Abs(p.y - q.y) > h + 4f) continue;
                float dx = p.x - q.x;
                float dz = p.z - q.z;
                float d = w + r + 1.3f;
                if (dx * dx + dz * dz < d * d) return false;
            }
            return true;
        }

        /// <summary>Finds a spot inside a room that is open air, and the floor and ceiling above and below it.</summary>
        static bool SampleRoomSpot(CaveField field, CaveField.Region region, CaveRoom rm, System.Random rng, out Vector3 floorHit, out Vector3 ceilHit)
        {
            floorHit = ceilHit = Vector3.zero;
            float ang = Rand(rng, 0f, Mathf.PI * 2f);
            float rad = Mathf.Sqrt((float)rng.NextDouble()) * 0.88f;
            float lx = Mathf.Cos(ang) * rad * rm.radius * rm.stretch;
            float lz = Mathf.Sin(ang) * rad * rm.radius;
            Vector3 probe = new Vector3(rm.floorCenter.x + lx * rm.cosYaw - lz * rm.sinYaw,
                                        rm.floorCenter.y + 1.2f,
                                        rm.floorCenter.z + lx * rm.sinYaw + lz * rm.cosYaw);
            if (field.Density(region, probe) >= -0.2f) return false;
            if (!field.RayMarch(region, probe, Vector3.down, 3.5f, out floorHit)) return false;
            return field.RayMarch(region, probe, Vector3.up, rm.height * 1.7f + 4f, out ceilHit);
        }

        /// <summary>
        /// Pillars, stalagmites and stalactites. Every formation is pushed into the rock at its base, and is only kept
        /// if that base really is inside solid rock, so nothing hangs in mid air.
        /// </summary>
        List<CaveBlob> BuildFormations(CaveField field, System.Random rng, CaveSettings s)
        {
            var list = new List<CaveBlob>();
            float f = s.formations;
            if (f < 0.01f) return list;
            var taken = new List<Vector4>();     // x, z, radius
            const float TipRadius = 0.35f;       // a blunt tip: anything thinner than a voxel breaks up
            const float Sink = 1f;               // how far the base is pushed into the rock

            for (int ri = 0; ri < rooms.Length; ri++)
            {
                CaveRoom rm = rooms[ri];
                if (rm.kind == (int)CaveRoomKind.Spawn) continue;       // the spawn cave is the same every time: no random formations in it
                CaveField.Region region = field.Query(rm.min, rm.max);
                float area = Mathf.PI * rm.radius * rm.radius * rm.stretch;
                bool grand = rm.kind == (int)CaveRoomKind.Cathedral || rm.kind == (int)CaveRoomKind.Cavern;
                float pillarRate = grand ? 1f / 55f : (rm.kind == (int)CaveRoomKind.Hall ? 1f / 110f : (rm.kind == (int)CaveRoomKind.Dome ? 1f / 170f : 0f));
                int pillars = Mathf.Min(8, Mathf.RoundToInt(f * area * pillarRate * Rand(rng, 0.5f, 1.3f)));
                int mites = Mathf.Min(14, Mathf.RoundToInt(f * area / 36f * Rand(rng, 0.5f, 1.2f)));
                int tites = Mathf.Min(14, Mathf.RoundToInt(f * area / 36f * Rand(rng, 0.5f, 1.2f)));
                if (rm.kind >= (int)CaveRoomKind.Pit) { mites = Mathf.Min(mites, 3); tites = Mathf.Min(tites, 5); }

                for (int n = 0, tries = 0; n < pillars && tries < pillars * 12; tries++)
                {
                    Vector3 fl, ce;
                    if (!SampleRoomSpot(field, region, rm, rng, out fl, out ce)) continue;
                    float gap = ce.y - fl.y;
                    if (gap < 3.2f || gap > 20f) continue;
                    float rad = Rand(rng, 0.8f, 1.5f) * (grand ? 1.15f : 1f) * 1.3f;
                    if (!FormationClear(fl, rad, taken)) continue;
                    Vector3 a = fl + Vector3.down * Sink;
                    Vector3 b = ce + Vector3.up * Sink;
                    if (field.Density(region, a) < 0.3f || field.Density(region, b) < 0.3f) continue;
                    list.Add(CaveBlob.Capsule(a, b, rad, rad, 0.4f));
                    taken.Add(new Vector4(fl.x, fl.z, rad, 0f));
                    n++;
                }

                for (int n = 0, tries = 0; n < mites && tries < mites * 12; tries++)
                {
                    Vector3 fl, ce;
                    if (!SampleRoomSpot(field, region, rm, rng, out fl, out ce)) continue;
                    float gap = ce.y - fl.y;
                    if (gap < 3f) continue;
                    float h = Rand(rng, 0.7f, Mathf.Max(0.8f, Mathf.Min(2.8f, gap * 0.5f)));
                    float rf = Rand(rng, 0.45f, 0.95f) * (1f + 0.3f * h / 2.8f);
                    if (!FormationClear(fl, rf, taken)) continue;
                    float ra = rf + (rf - TipRadius) * Sink / h;
                    Vector3 a = fl + Vector3.down * Sink;
                    if (field.Density(region, a) < 0.3f) continue;
                    list.Add(CaveBlob.Capsule(a, fl + Vector3.up * h, ra, TipRadius, 0f));
                    taken.Add(new Vector4(fl.x, fl.z, rf, 0f));
                    n++;
                }

                for (int n = 0, tries = 0; n < tites && tries < tites * 12; tries++)
                {
                    Vector3 fl, ce;
                    if (!SampleRoomSpot(field, region, rm, rng, out fl, out ce)) continue;
                    float gap = ce.y - fl.y;
                    if (gap < 3.6f) continue;
                    float h = Rand(rng, 0.7f, Mathf.Max(0.8f, Mathf.Min(3.2f, gap - 2.4f)));
                    float rf = Rand(rng, 0.45f, 0.95f) * (1f + 0.3f * h / 3.2f);
                    if (!FormationClear(ce, rf, taken)) continue;
                    float ra = rf + (rf - TipRadius) * Sink / h;
                    Vector3 a = ce + Vector3.up * Sink;
                    if (field.Density(region, a) < 0.3f) continue;
                    list.Add(CaveBlob.Capsule(a, ce + Vector3.down * h, ra, TipRadius, 0f));
                    taken.Add(new Vector4(ce.x, ce.z, rf, 0f));
                    n++;
                }
            }

            // A few small ones along the tunnels: stalactites in the middle, stalagmites against the walls.
            float spacing = Mathf.Lerp(40f, 9f, f);
            for (int pi = 0; pi < paths.Count; pi++)
            {
                CavePath path = paths[pi];
                float next = spacing * Rand(rng, 0.3f, 1f);
                for (int i = 1; i < path.pos.Count; i++)
                {
                    if (path.dist[i] < next) continue;
                    next += spacing * Rand(rng, 0.6f, 1.4f);
                    if (path.isMain && path.dist[i] < 10f) continue;
                    if (list.Count > 220) break;

                    Vector3 dir = path.pos[i] - path.pos[i - 1];
                    dir.y = 0f;
                    dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
                    Vector3 right = Vector3.Cross(Vector3.up, dir);
                    float hw = path.halfW[i];
                    float hh = path.halfH[i];

                    bool mite = rng.NextDouble() < 0.4;
                    float lat = mite ? (rng.Next(2) == 0 ? -1f : 1f) * Rand(rng, 0.65f, 0.88f) * hw : Rand(rng, -0.45f, 0.45f) * hw;
                    Vector3 probe = path.pos[i] + right * lat + Vector3.down * (hh * 0.35f);
                    CaveField.Region region = field.Query(probe - Vector3.one * (hh * 2f + 6f), probe + Vector3.one * (hh * 2f + 6f));
                    if (field.Density(region, probe) >= -0.2f) continue;
                    Vector3 fl, ce;
                    if (!field.RayMarch(region, probe, Vector3.down, hh * 2f + 2f, out fl)) continue;
                    if (!field.RayMarch(region, probe, Vector3.up, hh * 2f + 2f, out ce)) continue;
                    float gap = ce.y - fl.y;

                    if (mite)
                    {
                        if (gap < 3f) continue;
                        float h = Rand(rng, 0.6f, Mathf.Max(0.7f, Mathf.Min(1.8f, gap * 0.45f)));
                        float rf = Rand(rng, 0.4f, 0.8f);
                        if (!FormationClear(fl, rf, taken)) continue;
                        float ra = rf + (rf - TipRadius) * Sink / h;
                        Vector3 a = fl + Vector3.down * Sink;
                        if (field.Density(region, a) < 0.3f) continue;
                        list.Add(CaveBlob.Capsule(a, fl + Vector3.up * h, ra, TipRadius, 0f));
                        taken.Add(new Vector4(fl.x, fl.z, rf, 0f));
                    }
                    else
                    {
                        if (gap < 3.6f) continue;
                        float h = Rand(rng, 0.5f, Mathf.Max(0.6f, Mathf.Min(1.5f, gap - 2.3f)));
                        float rf = Rand(rng, 0.35f, 0.7f);
                        if (!FormationClear(ce, rf, taken)) continue;
                        float ra = rf + (rf - TipRadius) * Sink / h;
                        Vector3 a = ce + Vector3.up * Sink;
                        if (field.Density(region, a) < 0.3f) continue;
                        list.Add(CaveBlob.Capsule(a, ce + Vector3.down * h, ra, TipRadius, 0f));
                        taken.Add(new Vector4(ce.x, ce.z, rf, 0f));
                    }
                }
            }
            return list;
        }
    }
}
