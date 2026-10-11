using System;
using System.Collections.Generic;

namespace HavocCave.Lightmapping
{
    /// <summary>A light prepared for tracing: unit vectors, cosines and plain floats.</summary>
    internal sealed class LmLight
    {
        public int type;                // 0 point, 1 spot, 2 directional
        public float px, py, pz;
        public float dx, dy, dz;        // unit: spot axis / direction a directional light travels
        public float r, g, b;           // colour * intensity
        public float range, range2;
        public float radius;
        public float cosOuter, cosInner;
        public bool windowed;
        public int ignoreObj;

        const float NearField = 0.35f;      // metres

        public static LmLight From(CaveBakeLight s)
        {
            var l = new LmLight
            {
                type = (int)s.type,
                px = s.position.x, py = s.position.y, pz = s.position.z,
                r = Math.Max(0f, s.color.r), g = Math.Max(0f, s.color.g), b = Math.Max(0f, s.color.b),
                range = Math.Max(0.01f, s.range),
                radius = Math.Max(0f, s.radius),
                windowed = s.windowed,
                ignoreObj = s.ignoreObject >= 0 ? s.ignoreObject : LmBvh.NoObject      // -1 means "no fixture", but -1 is also the cave: it must keep shadowing
            };
            l.range2 = l.range * l.range;
            float dl = (float)Math.Sqrt(s.direction.x * s.direction.x + s.direction.y * s.direction.y + s.direction.z * s.direction.z);
            if (dl < 1e-6f) { l.dx = 0f; l.dy = -1f; l.dz = 0f; }
            else { l.dx = s.direction.x / dl; l.dy = s.direction.y / dl; l.dz = s.direction.z / dl; }
            float outer = Math.Max(1f, Math.Min(179f, s.spotAngle)) * 0.5f * (float)Math.PI / 180f;
            float inner = Math.Max(0f, Math.Min(s.innerSpotAngle, s.spotAngle - 0.1f)) * 0.5f * (float)Math.PI / 180f;
            l.cosOuter = (float)Math.Cos(outer);
            l.cosInner = (float)Math.Cos(inner);
            if (l.cosInner <= l.cosOuter + 1e-4f) l.cosInner = l.cosOuter + 1e-4f;
            return l;
        }

        /// <summary>
        /// The light this surface point receives if nothing were in the way: irradiance (er, eg, eb) and the unit
        /// direction towards the light. False when the light cannot reach the point at all.
        /// </summary>
        public bool Eval(float x, float y, float z, float nx, float ny, float nz, float gx, float gy, float gz,
                         out float er, out float eg, out float eb, out float lx, out float ly, out float lz, out float dist)
        {
            er = eg = eb = 0f;
            lx = ly = lz = 0f;
            dist = 0f;

            float att;
            if (type == 2)
            {
                lx = -dx; ly = -dy; lz = -dz;
                dist = 1e5f;
                att = 1f;
            }
            else
            {
                float tx = px - x, ty = py - y, tz = pz - z;
                float d2 = tx * tx + ty * ty + tz * tz;
                if (d2 >= range2 || d2 < 1e-8f) return false;
                float d = (float)Math.Sqrt(d2);
                float inv = 1f / d;
                lx = tx * inv; ly = ty * inv; lz = tz * inv;
                dist = d;

                if (windowed)
                {
                    float f = 1f - d / range;
                    att = f * f;
                }
                else
                {
                    // Physical: the light falls off with the square of the distance (the same curve URP uses for point lights),
                    // faded to nothing at the range. A real bulb is not a point, so the distance is padded a little: that keeps
                    // the surfaces right next to a lamp from burning out.
                    float q = d2 / range2;
                    float f = 1f - q * q;
                    if (f <= 0f) return false;
                    att = f * f / (d2 + NearField * NearField);
                }

                if (type == 1)
                {
                    float c = -(lx * dx + ly * dy + lz * dz);
                    if (c <= cosOuter) return false;
                    float s = (c - cosOuter) / (cosInner - cosOuter);
                    if (s < 1f) att *= s * s * (3f - 2f * s);
                }
            }

            float cosN = nx * lx + ny * ly + nz * lz;
            if (cosN <= 0f) return false;
            if (gx * lx + gy * ly + gz * lz <= 0f) return false;
            att *= cosN;
            er = r * att; eg = g * att; eb = b * att;
            return er > 0f || eg > 0f || eb > 0f;
        }
    }

    /// <summary>Uniform grid that answers "which lights can reach this point" with a short list.</summary>
    internal sealed class LmLightGrid
    {
        public readonly LmLight[] lights;
        public int[] directional;       // indices of directional lights, which reach everywhere
        public int maxCandidates;       // the most lights Find can return plus the directional ones

        float minX, minY, minZ, invCell;
        int nx, ny, nz;
        int[] cellStart;
        int[] items;

        public LmLightGrid(LmLight[] lights, float[] sceneMin, float[] sceneMax)
        {
            this.lights = lights;
            var dirs = new List<int>();
            float maxRange = 1f;
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type == 2) dirs.Add(i);
                else maxRange = Math.Max(maxRange, lights[i].range);
            }
            directional = dirs.ToArray();

            minX = sceneMin[0]; minY = sceneMin[1]; minZ = sceneMin[2];
            float sx = sceneMax[0] - minX, sy = sceneMax[1] - minY, sz = sceneMax[2] - minZ;
            float cell = Math.Max(2f, maxRange);
            // keep the grid a sensible size
            while (((long)Math.Ceiling(sx / cell) + 1) * ((long)Math.Ceiling(sy / cell) + 1) * ((long)Math.Ceiling(sz / cell) + 1) > 1000000L) cell *= 1.5f;
            invCell = 1f / cell;
            nx = (int)Math.Ceiling(sx * invCell) + 1;
            ny = (int)Math.Ceiling(sy * invCell) + 1;
            nz = (int)Math.Ceiling(sz * invCell) + 1;

            int cells = nx * ny * nz;
            var counts = new int[cells + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                var cursor = pass == 1 ? new int[cells] : null;
                for (int i = 0; i < lights.Length; i++)
                {
                    LmLight l = lights[i];
                    if (l.type == 2) continue;
                    int x0 = Clamp((int)Math.Floor((l.px - l.range - minX) * invCell), nx);
                    int x1 = Clamp((int)Math.Floor((l.px + l.range - minX) * invCell), nx);
                    int y0 = Clamp((int)Math.Floor((l.py - l.range - minY) * invCell), ny);
                    int y1 = Clamp((int)Math.Floor((l.py + l.range - minY) * invCell), ny);
                    int z0 = Clamp((int)Math.Floor((l.pz - l.range - minZ) * invCell), nz);
                    int z1 = Clamp((int)Math.Floor((l.pz + l.range - minZ) * invCell), nz);
                    for (int z = z0; z <= z1; z++)
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++)
                            {
                                int c = x + nx * (y + ny * z);
                                if (pass == 0) counts[c + 1]++;
                                else items[counts[c] + cursor[c]++] = i;
                            }
                }
                if (pass == 0)
                {
                    int most = 0;
                    for (int c = 0; c < cells; c++) most = Math.Max(most, counts[c + 1]);
                    for (int c = 0; c < cells; c++) counts[c + 1] += counts[c];
                    items = new int[counts[cells]];
                    maxCandidates = most + directional.Length;
                }
            }
            cellStart = counts;
        }

        static int Clamp(int v, int n)
        {
            return v < 0 ? 0 : (v >= n ? n - 1 : v);
        }

        /// <summary>The lights whose range overlaps the grid cell that holds (x, y, z): items[start .. start + count).</summary>
        public void Find(float x, float y, float z, out int start, out int count)
        {
            int cx = Clamp((int)Math.Floor((x - minX) * invCell), nx);
            int cy = Clamp((int)Math.Floor((y - minY) * invCell), ny);
            int cz = Clamp((int)Math.Floor((z - minZ) * invCell), nz);
            int c = cx + nx * (cy + ny * cz);
            start = cellStart[c];
            count = cellStart[c + 1] - start;
        }

        public int Item(int index)
        {
            return items[index];
        }
    }
}
