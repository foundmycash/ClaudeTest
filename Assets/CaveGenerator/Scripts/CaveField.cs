using System.Collections.Generic;
using UnityEngine;

namespace HavocCave
{
    /// <summary>
    /// The cave as a signed field: negative = open air, positive = rock, zero = the wall surface.
    /// The field is immutable after construction, so many threads can read it at once.
    /// </summary>
    public sealed class CaveField
    {
        /// <summary>Value returned far away from every tunnel.</summary>
        public const float Far = 100f;

        /// <summary>The part of the segment list and rock list that matters for one box of space.</summary>
        public sealed class Region
        {
            public int[] seg;
            public int segCount;
            public int[] blob;
            public int blobCount;
            public int[] room;
            public int roomCount;
        }

        readonly CaveSegment[] segs;
        readonly CaveBlob[] blobs;
        readonly CaveRoom[] rooms;
        readonly float amp;
        readonly float floorRough;
        readonly float squareness;
        readonly float invNoiseSize;
        readonly float pathHalf;
        readonly float pathFade;
        readonly float pathWobble;
        readonly float strataAmp;
        readonly float strataFreq;
        readonly int seed;
        readonly CaveLayout layout;
        readonly bool hasSpawn;
        readonly int spawnSeed;
        Region all;

        public CaveField(CaveLayout layout, CaveSettings s, int seed)
        {
            this.layout = layout;
            hasSpawn = layout.hasSpawnCave;
            spawnSeed = layout.spawnCaveSeed;
            segs = layout.segments;
            blobs = layout.blobs;
            rooms = layout.rooms;
            amp = s.roughness;
            floorRough = s.floorRoughness;
            squareness = s.squareness;
            invNoiseSize = 1f / Mathf.Max(0.5f, s.roughnessScale);
            pathHalf = s.pathWidth * 0.5f;
            pathFade = Mathf.Max(0.05f, s.pathFade);
            pathWobble = s.pathWobble;
            strataAmp = s.strata;
            strataFreq = 1f / Mathf.Max(1f, s.roughnessScale * 0.5f);
            this.seed = seed;
        }

        /// <summary>Everything. Use for one-off queries (prop placement). Main thread only.</summary>
        public Region All()
        {
            if (all == null) all = Query(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(1e6f, 1e6f, 1e6f));
            return all;
        }

        public Region Query(Vector3 min, Vector3 max)
        {
            var si = new List<int>();
            for (int i = 0; i < segs.Length; i++)
            {
                if (segs[i].max.x < min.x || segs[i].min.x > max.x ||
                    segs[i].max.y < min.y || segs[i].min.y > max.y ||
                    segs[i].max.z < min.z || segs[i].min.z > max.z) continue;
                si.Add(i);
            }

            var bi = new List<int>();
            for (int i = 0; i < blobs.Length; i++)
            {
                if (blobs[i].max.x < min.x || blobs[i].min.x > max.x ||
                    blobs[i].max.y < min.y || blobs[i].min.y > max.y ||
                    blobs[i].max.z < min.z || blobs[i].min.z > max.z) continue;
                bi.Add(i);
            }

            var ri = new List<int>();
            for (int i = 0; i < rooms.Length; i++)
            {
                if (rooms[i].max.x < min.x || rooms[i].min.x > max.x ||
                    rooms[i].max.y < min.y || rooms[i].min.y > max.y ||
                    rooms[i].max.z < min.z || rooms[i].min.z > max.z) continue;
                ri.Add(i);
            }

            return new Region
            {
                seg = si.ToArray(), segCount = si.Count,
                blob = bi.ToArray(), blobCount = bi.Count,
                room = ri.ToArray(), roomCount = ri.Count
            };
        }

        static float SmoothMax(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Max(a, b) + h * h * k * 0.25f;
        }

        static float SmoothMin(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Min(a, b) - h * h * k * 0.25f;
        }

        /// <summary>
        /// Distance-like value for one room: negative inside. A dome (half an ellipsoid standing on the floor)
        /// cut off by a flat floor, with the floor edge rounded. y is the height above the floor.
        /// </summary>
        float RoomDistance(CaveRoom rm, Vector3 p, out float y)
        {
            float dx = p.x - rm.floorCenter.x;
            float dz = p.z - rm.floorCenter.z;
            y = p.y - rm.floorCenter.y;

            // Into the room's own axes: x along the long side.
            float lx = dx * rm.cosYaw + dz * rm.sinYaw;
            float lz = -dx * rm.sinYaw + dz * rm.cosYaw;
            float rz = rm.radius;
            float rx = rz * rm.stretch;
            float h = rm.height;

            // Lumpy caverns: the outline and the roof wander with low frequency noise.
            if (rm.warp > 0f)
            {
                float f = 1.4f / rz;
                float n1 = CaveNoise.Value(p.x * f, p.y * f * 0.6f, p.z * f, rm.noiseSeed);
                float n2 = CaveNoise.Value(p.x * f * 0.8f + 31.7f, 0f, p.z * f * 0.8f - 12.3f, rm.noiseSeed + 7);
                float m = 1f + rm.warp * n1;
                rx *= m;
                rz *= m;
                h *= 1f + rm.warp * 0.5f * n2;
            }

            float nx = lx * lx / (rx * rx);
            float nz = lz * lz / (rz * rz);
            float k2 = Mathf.Sqrt(nx + nz);
            float k4 = Mathf.Sqrt(Mathf.Sqrt(nx * nx + nz * nz));
            float u = k2 + (k4 - k2) * rm.squareness;       // 0 in the middle, 1 at the wall
            float v = y / h;
            float k = Mathf.Sqrt(u * u + v * v);
            // First-order distance (see the tunnel code): long halls and tall cathedrals keep a firm wall.
            float k1 = Mathf.Sqrt(lx * lx / (rx * rx * rx * rx) + lz * lz / (rz * rz * rz * rz) + y * y / (h * h * h * h));
            float dome = k1 > 1e-5f ? k * (k - 1f) / k1 : -Mathf.Min(Mathf.Min(rx, rz), h);
            return SmoothMax(dome, -y, 0.8f);
        }

        public float Density(Region r, Vector3 p)
        {
            float dh, dy, hh;
            return Sample(r, p, out dh, out dy, out hh);
        }

        /// <summary>
        /// Field value at p. Also reports how far p is horizontally (dh) and vertically (dy) from the
        /// centre line of the tunnel that owns it, and that tunnel's half height.
        /// </summary>
        public float Sample(Region r, Vector3 p, out float dh, out float dy, out float halfH)
        {
            return SampleCore(r, p, true, out dh, out dy, out halfH);
        }

        /// <summary>The field without the wall roughness: several times cheaper. Good enough for line-of-sight tests.</summary>
        public float DensityFast(Region r, Vector3 p)
        {
            float dh, dy, hh;
            return SampleCore(r, p, false, out dh, out dy, out hh);
        }

        /// <summary>True if nothing solid is in the way between two points in open air.</summary>
        public bool Visible(Region r, Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.3f) return true;
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 0.6f), 2, 40);
            for (int i = 1; i < steps; i++)
                if (DensityFast(r, from + d * ((float)i / steps)) >= -0.05f) return false;
            return true;
        }

        float SampleCore(Region r, Vector3 p, bool withNoise, out float dh, out float dy, out float halfH)
        {
            float best = Far;
            float bestMin = 1f;
            dh = 0f;
            dy = 0f;
            halfH = 1f;
            bool found = false;

            for (int n = 0; n < r.segCount; n++)
            {
                int index = r.seg[n];
                if (p.x < segs[index].min.x || p.x > segs[index].max.x ||
                    p.y < segs[index].min.y || p.y > segs[index].max.y ||
                    p.z < segs[index].min.z || p.z > segs[index].max.z) continue;

                float t = ((p.x - segs[index].a.x) * segs[index].ab.x +
                           (p.y - segs[index].a.y) * segs[index].ab.y +
                           (p.z - segs[index].a.z) * segs[index].ab.z) * segs[index].invLen2;
                t = t < 0f ? 0f : (t > 1f ? 1f : t);

                float dx = p.x - (segs[index].a.x + segs[index].ab.x * t);
                float ddy = p.y - (segs[index].a.y + segs[index].ab.y * t);
                float dz = p.z - (segs[index].a.z + segs[index].ab.z * t);
                float w = segs[index].wa + (segs[index].wb - segs[index].wa) * t;
                float h = segs[index].ha + (segs[index].hb - segs[index].ha) * t;

                // Super-ellipse cross section: squareness 0 = ellipse, 1 = rounded box.
                float nx = (dx * dx + dz * dz) / (w * w);
                float ny = (ddy * ddy) / (h * h);
                float k2 = Mathf.Sqrt(nx + ny);
                float k4 = Mathf.Sqrt(Mathf.Sqrt(nx * nx + ny * ny));
                float k = k2 + (k4 - k2) * squareness;
                float mn = w < h ? w : h;

                // Divide by the slope of k so the field is a true distance near the wall in every direction. Without this
                // a tall narrow tunnel has a "soft" ceiling, and wall roughness can pinch off bubbles of air above it.
                float k1 = Mathf.Sqrt((dx * dx + dz * dz) / (w * w * w * w) + (ddy * ddy) / (h * h * h * h));
                float d = k1 > 1e-5f ? k * (k - 1f) / k1 : -mn;

                found = true;
                if (d < best)
                {
                    best = d;
                    bestMin = mn;
                    dh = Mathf.Sqrt(dx * dx + dz * dz);
                    dy = ddy;
                    halfH = h;
                }
            }

            // Rooms: flat floor, domed ceiling. They blend smoothly into the tunnels around them.
            float roomBest = Far;
            float roomY = 0f;
            float roomUnit = 1f;
            bool roomFound = false;
            for (int n = 0; n < r.roomCount; n++)
            {
                CaveRoom rm = rooms[r.room[n]];
                if (p.x < rm.min.x || p.x > rm.max.x || p.y < rm.min.y || p.y > rm.max.y || p.z < rm.min.z || p.z > rm.max.z) continue;
                float ry;
                float rd = RoomDistance(rm, p, out ry);
                roomFound = true;
                if (rd < roomBest)
                {
                    roomBest = rd;
                    roomY = ry;
                    roomUnit = Mathf.Min(rm.radius, rm.height) * 0.5f;
                }
            }

            if (!found && !roomFound) return Far;
            bool roomOwns = roomFound && (!found || roomBest < best);
            if (roomFound)
            {
                if (!found) best = roomBest;
                else
                {
                    // Blend the tunnel into the room, but not at floor level: there both floors are at the same height and the
                    // blend would dig a trench thinner than a voxel, which turns into holes in the mesh.
                    float k = 1.5f * CaveNoise.Smooth(0.3f, 1.2f, roomY);
                    best = k < 0.01f ? Mathf.Min(best, roomBest) : SmoothMin(best, roomBest, k);
                }
            }
            if (roomOwns) bestMin = roomUnit;

            // Rocks sticking out of the walls, merged in smoothly.
            for (int n = 0; n < r.blobCount; n++)
            {
                CaveBlob b = blobs[r.blob[n]];
                if (p.x < b.min.x || p.x > b.max.x || p.y < b.min.y || p.y > b.max.y || p.z < b.min.z || p.z > b.max.z) continue;
                if (b.box)
                {
                    // A ledge or step: a rounded box, joined to the wall with a small fillet so its edges stay sharp.
                    float bx = p.x - b.center.x;
                    float by = p.y - b.center.y;
                    float bz = p.z - b.center.z;
                    float lx = bx * b.cosY + bz * b.sinY;
                    float lz = -bx * b.sinY + bz * b.cosY;
                    float qx = Mathf.Abs(lx) - (b.half.x - b.corner);
                    float qy = Mathf.Abs(by) - (b.half.y - b.corner);
                    float qz = Mathf.Abs(lz) - (b.half.z - b.corner);
                    float ox = qx > 0f ? qx : 0f;
                    float oy = qy > 0f ? qy : 0f;
                    float oz = qz > 0f ? qz : 0f;
                    float inside = Mathf.Max(qx, Mathf.Max(qy, qz));
                    float sdf = Mathf.Sqrt(ox * ox + oy * oy + oz * oz) + (inside < 0f ? inside : 0f) - b.corner;
                    if (sdf > b.smooth) continue;
                    best = SmoothMax(best, -sdf, b.smooth);
                    continue;
                }
                float dx = p.x - b.center.x;
                float dyb = p.y - b.center.y;
                float dz = p.z - b.center.z;
                float rad = b.radius;
                if (b.invLen2 > 0f)
                {
                    // A formation: distance to a tapered line, thinner in the middle for pillars.
                    float t = (dx * b.axis.x + dyb * b.axis.y + dz * b.axis.z) * b.invLen2;
                    t = t < 0f ? 0f : (t > 1f ? 1f : t);
                    dx -= b.axis.x * t;
                    dyb -= b.axis.y * t;
                    dz -= b.axis.z * t;
                    rad = b.radius + (b.radiusEnd - b.radius) * t;
                    if (b.waist > 0f) rad *= 1f - b.waist * 4f * t * (1f - t);
                }
                float dist = Mathf.Sqrt(dx * dx + dyb * dyb + dz * dz);
                if (dist > rad + CaveLayout.BlobSmooth) continue;
                best = SmoothMax(best, rad - dist, CaveLayout.BlobSmooth);
            }

            // Lumpy walls. The floor gets a calmer share of the roughness, and small tunnels
            // are never roughened enough to close up.
            if (!withNoise) return best;
            float a = Mathf.Min(amp, 0.3f * bestMin);
            float floorBlend = roomOwns ? 1f - CaveNoise.Smooth(0.1f, 1.5f, roomY) : CaveNoise.Smooth(0.15f, 0.55f, -dy / halfH);
            a *= 1f + (floorRough - 1f) * floorBlend;
            // Near the spawn cave the wall bumps come from the spawn cave's own seed, so its shape is the same for every map seed.
            float spawnW = hasSpawn ? layout.SpawnWeight(p) : 0f;
            float noise = 0f;
            if (spawnW < 1f) noise = Mathf.Clamp(CaveNoise.Fbm(p.x * invNoiseSize, p.y * invNoiseSize * 1.15f, p.z * invNoiseSize, seed) * 1.6f, -1f, 1f);
            if (spawnW > 0f)
            {
                float fixedNoise = Mathf.Clamp(CaveNoise.Fbm(p.x * invNoiseSize, p.y * invNoiseSize * 1.15f, p.z * invNoiseSize, spawnSeed) * 1.6f, -1f, 1f);
                noise = spawnW >= 1f ? fixedNoise : noise + (fixedNoise - noise) * spawnW;
            }
            float result = best + noise * a;

            // Layering: gentle horizontal ridges in the walls, like sediment. Not on the floor.
            if (strataAmp > 0.001f)
            {
                float bandNoise = spawnW < 1f ? CaveNoise.Value(p.x * invNoiseSize * 0.5f, 0f, p.z * invNoiseSize * 0.5f, seed + 211) : 0f;
                if (spawnW > 0f)
                {
                    float fixedBand = CaveNoise.Value(p.x * invNoiseSize * 0.5f, 0f, p.z * invNoiseSize * 0.5f, spawnSeed + 211);
                    bandNoise = spawnW >= 1f ? fixedBand : bandNoise + (fixedBand - bandNoise) * spawnW;
                }
                float bands = p.y * strataFreq + 0.8f * bandNoise;
                float sa = Mathf.Min(strataAmp, 0.2f * bestMin) * (1f - floorBlend);
                result += Mathf.Sin(bands * 6.2831853f) * sa;
            }
            return result;
        }

        /// <summary>Slope of the field at p (tetrahedron sampling, 4 lookups). Points into the rock.</summary>
        public Vector3 Gradient(Region r, Vector3 p, float eps)
        {
            float f0 = Density(r, new Vector3(p.x + eps, p.y - eps, p.z - eps));
            float f1 = Density(r, new Vector3(p.x - eps, p.y - eps, p.z + eps));
            float f2 = Density(r, new Vector3(p.x - eps, p.y + eps, p.z - eps));
            float f3 = Density(r, new Vector3(p.x + eps, p.y + eps, p.z + eps));
            return new Vector3(f0 - f1 - f2 + f3, -f0 - f1 + f2 + f3, -f0 + f1 - f2 + f3) / (4f * eps);
        }

        /// <summary>Unit normal pointing out of the rock into the open air.</summary>
        public Vector3 Normal(Region r, Vector3 p, float eps)
        {
            Vector3 g = Gradient(r, p, eps);
            float len = g.magnitude;
            return len > 1e-6f ? g / -len : Vector3.up;
        }

        /// <summary>
        /// Moves p onto the true wall surface (where the field is zero). Surface Nets puts vertices in the middle of
        /// a voxel, which on coarse meshes can be half a voxel off the real wall. Never moves more than maxMove.
        /// </summary>
        public Vector3 SnapToSurface(Region r, Vector3 p, float maxMove)
        {
            for (int i = 0; i < 2; i++)
            {
                float f = Density(r, p);
                Vector3 g = Gradient(r, p, 0.15f);
                float g2 = g.sqrMagnitude;
                if (g2 < 0.12f || g2 > 9f) break;     // slope of the field is unreliable here: leave the vertex alone
                Vector3 step = g * (-f / g2);
                float m = step.magnitude;
                if (m > maxMove) step *= maxMove / m;
                p += step;
            }
            return p;
        }

        /// <summary>1 on the dirt path (and the middle of every room), 0 on bare rock, smooth in between.</summary>
        public float DirtMask(Region r, Vector3 p)
        {
            float wobble = CaveNoise.Value(p.x * 0.28f, p.y * 0.28f, p.z * 0.28f, seed + 91) * pathWobble;
            float mask = 0f;

            float nearest = float.MaxValue;
            for (int n = 0; n < r.segCount; n++)
            {
                int i = r.seg[n];
                if (segs[i].path == -1) continue;     // shafts are rock walls all round: no dirt on them
                if (p.x < segs[i].min.x || p.x > segs[i].max.x || p.y < segs[i].min.y || p.y > segs[i].max.y ||
                    p.z < segs[i].min.z || p.z > segs[i].max.z) continue;
                float t = ((p.x - segs[i].a.x) * segs[i].ab.x + (p.y - segs[i].a.y) * segs[i].ab.y + (p.z - segs[i].a.z) * segs[i].ab.z) * segs[i].invLen2;
                t = t < 0f ? 0f : (t > 1f ? 1f : t);
                float dx = p.x - (segs[i].a.x + segs[i].ab.x * t);
                float dz = p.z - (segs[i].a.z + segs[i].ab.z * t);
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < nearest) nearest = d;
            }
            if (nearest < float.MaxValue)
                mask = 1f - CaveNoise.Smooth(pathHalf - pathFade * 0.5f, pathHalf + pathFade * 0.5f, nearest + wobble);

            for (int n = 0; n < r.roomCount; n++)
            {
                CaveRoom rm = rooms[r.room[n]];
                if (p.x < rm.min.x || p.x > rm.max.x || p.z < rm.min.z || p.z > rm.max.z) continue;
                float ddx = p.x - rm.floorCenter.x;
                float ddz = p.z - rm.floorCenter.z;
                float dx = (ddx * rm.cosYaw + ddz * rm.sinYaw) / (rm.radius * rm.stretch);
                float dz = (-ddx * rm.sinYaw + ddz * rm.cosYaw) / rm.radius;
                float u = Mathf.Sqrt(dx * dx + dz * dz);
                mask = Mathf.Max(mask, 1f - CaveNoise.Smooth(0.45f, 0.85f, u + wobble * 0.08f));
            }
            return mask;
        }

        /// <summary>Cheap ambient occlusion: how enclosed is the air just in front of this wall point? 1 = open.</summary>
        public float Occlusion(Region r, Vector3 p, Vector3 normal, float scale)
        {
            float occ = 0f;
            float weight = 1f;
            float h = 0.2f * scale;
            for (int i = 0; i < 3; i++)
            {
                float free = -Density(r, p + normal * h);
                occ += (h - free) * weight;
                weight *= 0.65f;
                h *= 2.2f;
            }
            return Mathf.Clamp01(1f - occ * 0.9f);
        }

        /// <summary>Walk from o along dir until the field turns to rock. o must be in open air.</summary>
        public bool RayMarch(Region r, Vector3 o, Vector3 dir, float maxDist, out Vector3 hit)
        {
            hit = o;
            if (Density(r, o) >= 0f) return false;

            const float step = 0.1f;
            float prev = 0f;
            for (float t = step; t <= maxDist + 1e-3f; t += step)
            {
                if (Density(r, o + dir * t) >= 0f)
                {
                    float lo = prev;
                    float hi = t;
                    for (int i = 0; i < 8; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (Density(r, o + dir * mid) >= 0f) hi = mid; else lo = mid;
                    }
                    hit = o + dir * ((lo + hi) * 0.5f);
                    return true;
                }
                prev = t;
            }
            return false;
        }
    }
}
