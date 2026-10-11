using System;
using System.Threading;
using System.Threading.Tasks;

namespace HavocCave.Lightmapping
{
    /// <summary>Everything the tracer needs to know about the scene, as flat arrays.</summary>
    internal sealed class LmScene
    {
        public float[] pos;             // 9 per triangle: three corners, cave space
        public float[] nrm;             // 9 per triangle: smooth corner normals (unused for occluders)
        public float[] mask;            // 3 per triangle: dirt mask at each corner (0 rock .. 1 dirt)
        public int[] alb;               // -1 = cave surface, otherwise an index into albedoTable
        public float[] albedoTable;     // rgb triples
        public int[] obj;               // object id per triangle, -1 = cave
        public int caveTriangles;       // the first caveTriangles triangles are the cave and have texels
        public float[] min = new float[3];
        public float[] max = new float[3];
        public LmBvh bvh;
    }

    /// <summary>
    /// Path tracer. For every covered texel it gathers the irradiance on the surface:
    ///   direct   - each lamp that reaches the texel, with soft shadows (several shadow rays towards the lamp's bulb)
    ///   indirect - cosine weighted rays that bounce off the rock; each hit is lit by one lamp chosen in proportion to
    ///              its contribution (next event estimation), then the path carries on for the remaining bounces
    ///   ambient  - the same first rays measure how open the texel is, for ambient occlusion
    /// Direct and indirect are kept apart so only the noisy indirect part gets denoised afterwards. A dominant light direction is
    /// accumulated alongside, weighted by how much light each direction brings.
    /// Every texel has its own random stream seeded from its index, so the result does not depend on thread timing.
    /// </summary>
    internal sealed partial class LmTracer
    {
        const float Bias = 0.02f;           // how far rays start above the surface (metres)
        const float TMin = 0.0005f;
        const float FarAway = 1e5f;

        readonly LmScene scene;
        readonly LmBvh bvh;
        readonly LmAtlas atlas;
        readonly LmLightGrid grid;
        readonly LmParams p;
        readonly float rockR, rockG, rockB, dirtR, dirtG, dirtB;
        readonly float ambR, ambG, ambB;
        readonly bool hasAmbient;
        readonly float ambientRange;

        public readonly float[] direct;     // 3 per texel
        public float[] indirect;            // 3 per texel (includes ambient)
        public readonly float[] dirDirect;  // 3 per texel: weighted sum of the directions lamps shine from
        public float[] dirIndirect;
        public readonly sbyte[] normal;     // 3 per texel, * 127

        int done;
        public float Progress { get { return atlas.validCount == 0 ? 1f : Math.Min(1f, (float)Volatile.Read(ref done) / atlas.validCount); } }

        sealed class Ctx
        {
            public readonly int[] stack = new int[LmBvh.StackSize];
            public readonly int[] cand;
            public Ctx(int n) { cand = new int[Math.Max(1, n)]; }
        }

        public LmTracer(LmScene scene, LmAtlas atlas, LmLightGrid grid, LmParams p)
        {
            this.scene = scene;
            bvh = scene.bvh;
            this.atlas = atlas;
            this.grid = grid;
            this.p = p;
            rockR = p.rockAlbedo.r; rockG = p.rockAlbedo.g; rockB = p.rockAlbedo.b;
            dirtR = p.dirtAlbedo.r; dirtG = p.dirtAlbedo.g; dirtB = p.dirtAlbedo.b;
            ambR = p.ambient.r; ambG = p.ambient.g; ambB = p.ambient.b;
            hasAmbient = ambR > 0f || ambG > 0f || ambB > 0f;
            ambientRange = p.ambientRange;

            int n = atlas.w * atlas.h;
            direct = new float[n * 3];
            indirect = new float[n * 3];
            dirDirect = new float[n * 3];
            dirIndirect = new float[n * 3];
            normal = new sbyte[n * 3];
        }

        /// <summary>The raw indirect buffers are not needed once they have been denoised into new ones.</summary>
        public void ReleaseIndirect()
        {
            indirect = null;
            dirIndirect = null;
        }

        public void Run(CancellationToken token, int threads)
        {
            const int Block = 64;
            int blocks = (atlas.validCount + Block - 1) / Block;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = token };
            int candidates = grid.maxCandidates;
            Parallel.For(0, blocks, opts, () => new Ctx(candidates), (b, state, ctx) =>
            {
                int from = b * Block;
                int to = Math.Min(atlas.validCount, from + Block);
                for (int i = from; i < to; i++) BakeTexel(atlas.valid[i], ctx);
                Interlocked.Add(ref done, to - from);
                return ctx;
            }, ctx => { });
        }

        // ------------------------------------------------------------------ one texel

        void BakeTexel(int texel, Ctx ctx)
        {
            int t = atlas.tri[texel];
            float b1 = atlas.b1[texel], b2 = atlas.b2[texel], b0 = 1f - b1 - b2;
            float[] pos = scene.pos;
            float[] nrm = scene.nrm;
            int o = t * 9;

            float px = b0 * pos[o] + b1 * pos[o + 3] + b2 * pos[o + 6];
            float py = b0 * pos[o + 1] + b1 * pos[o + 4] + b2 * pos[o + 7];
            float pz = b0 * pos[o + 2] + b1 * pos[o + 5] + b2 * pos[o + 8];

            // geometric and smooth normals, both pointing out of the rock into the cave
            float e1x = pos[o + 3] - pos[o], e1y = pos[o + 4] - pos[o + 1], e1z = pos[o + 5] - pos[o + 2];
            float e2x = pos[o + 6] - pos[o], e2y = pos[o + 7] - pos[o + 1], e2z = pos[o + 8] - pos[o + 2];
            float gx = e1y * e2z - e1z * e2y, gy = e1z * e2x - e1x * e2z, gz = e1x * e2y - e1y * e2x;
            float gl = (float)Math.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gl < 1e-12f) { gx = 0f; gy = 1f; gz = 0f; } else { gx /= gl; gy /= gl; gz /= gl; }

            float nx = b0 * nrm[o] + b1 * nrm[o + 3] + b2 * nrm[o + 6];
            float ny = b0 * nrm[o + 1] + b1 * nrm[o + 4] + b2 * nrm[o + 7];
            float nz = b0 * nrm[o + 2] + b1 * nrm[o + 5] + b2 * nrm[o + 8];
            float nl = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-6f) { nx = gx; ny = gy; nz = gz; } else { nx /= nl; ny /= nl; nz /= nl; }
            if (nx * gx + ny * gy + nz * gz < 0f) { gx = -gx; gy = -gy; gz = -gz; }

            int k3 = texel * 3;
            normal[k3] = (sbyte)Math.Round(nx * 127f);
            normal[k3 + 1] = (sbyte)Math.Round(ny * 127f);
            normal[k3 + 2] = (sbyte)Math.Round(nz * 127f);

            var rng = new LmRng((uint)texel * 0x9E3779B1u + (uint)p.seed * 0x85EBCA6Bu);
            float ox = px + gx * Bias, oy = py + gy * Bias, oz = pz + gz * Bias;

            // ---- direct light from every lamp that reaches this texel
            float dr = 0f, dg = 0f, db = 0f, ddx = 0f, ddy = 0f, ddz = 0f;
            float spin = rng.Next() * LmMath.TwoPi;
            int start, count;
            grid.Find(px, py, pz, out start, out count);
            int dirCount = grid.directional.Length;
            for (int c = 0; c < dirCount + count; c++)
            {
                int li = c < dirCount ? grid.directional[c] : grid.Item(start + c - dirCount);
                LmLight L = grid.lights[li];
                float er, eg, eb, lx, ly, lz, dist;
                if (!L.Eval(px, py, pz, nx, ny, nz, gx, gy, gz, out er, out eg, out eb, out lx, out ly, out lz, out dist)) continue;
                float vis = Visibility(L, ox, oy, oz, lx, ly, lz, dist, p.shadowRays, spin, LmBvh.NoObject, ctx);
                if (vis <= 0f) continue;
                er *= vis; eg *= vis; eb *= vis;
                dr += er; dg += eg; db += eb;
                float w = LmMath.Lum(er, eg, eb);
                ddx += lx * w; ddy += ly * w; ddz += lz * w;
            }

            // ---- bounced light and ambient occlusion
            float ir = 0f, ig = 0f, ib = 0f, idx = 0f, idy = 0f, idz = 0f, open = 0f;
            int g1 = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, p.rays)));
            int total = g1 * g1;
            int bounces = p.bounces;
            float ambLum = LmMath.Lum(ambR, ambG, ambB);
            if (bounces > 0 || hasAmbient)
            {
                float invG = 1f / g1;
                for (int sy = 0; sy < g1; sy++)
                    for (int sx = 0; sx < g1; sx++)
                    {
                        float dx, dy, dz;
                        CosineDirection(nx, ny, nz, (sx + rng.Next()) * invG, (sy + rng.Next()) * invG, out dx, out dy, out dz);
                        if (dx * gx + dy * gy + dz * gz <= 0.02f)
                        {
                            // Points below the true surface (the smooth normal leans away from the triangle): no light, but not an occluder either.
                            open += 1f;
                            continue;
                        }

                        float tHit, hu, hv;
                        int slot;
                        if (!bvh.Closest(ox, oy, oz, dx, dy, dz, TMin, FarAway, ctx.stack, out tHit, out slot, out hu, out hv))
                        {
                            open += 1f;
                            if (hasAmbient) { idx += dx * ambLum; idy += dy * ambLum; idz += dz * ambLum; }
                            continue;
                        }

                        float vis = Math.Min(1f, tHit / ambientRange);
                        open += vis;
                        if (hasAmbient) { float wa = ambLum * vis; idx += dx * wa; idy += dy * wa; idz += dz * wa; }
                        if (bounces <= 0) continue;

                        // follow the path
                        float sr = 0f, sg = 0f, sb = 0f;
                        float tr = 1f, tg = 1f, tb = 1f;
                        float cdx = dx, cdy = dy, cdz = dz;
                        for (int depth = 0; depth < bounces; depth++)
                        {
                            float qx, qy, qz, hgx, hgy, hgz, hnx, hny, hnz, ar, ag, ab;
                            HitData(slot, hu, hv, cdx, cdy, cdz, out qx, out qy, out qz, out hgx, out hgy, out hgz, out hnx, out hny, out hnz, out ar, out ag, out ab);
                            tr *= ar; tg *= ag; tb *= ab;
                            if (tr + tg + tb < 0.01f) break;

                            float er, eg, eb;
                            Nee(qx + hgx * Bias, qy + hgy * Bias, qz + hgz * Bias, qx, qy, qz, hnx, hny, hnz, hgx, hgy, hgz, ctx, ref rng, out er, out eg, out eb);
                            sr += tr * er; sg += tg * eg; sb += tb * eb;

                            if (depth == bounces - 1) break;
                            float ndx, ndy, ndz;
                            CosineDirection(hnx, hny, hnz, rng.Next(), rng.Next(), out ndx, out ndy, out ndz);
                            if (ndx * hgx + ndy * hgy + ndz * hgz <= 0.02f) break;
                            if (!bvh.Closest(qx + hgx * Bias, qy + hgy * Bias, qz + hgz * Bias, ndx, ndy, ndz, TMin, FarAway, ctx.stack, out tHit, out slot, out hu, out hv)) break;
                            cdx = ndx; cdy = ndy; cdz = ndz;
                        }

                        ir += sr; ig += sg; ib += sb;
                        float w = LmMath.Lum(sr, sg, sb);
                        idx += dx * w; idy += dy * w; idz += dz * w;
                    }
            }

            float inv = p.bounceIntensity / total;
            float openAvg = open / total;
            direct[k3] = dr; direct[k3 + 1] = dg; direct[k3 + 2] = db;
            indirect[k3] = ir * inv + ambR * openAvg;
            indirect[k3 + 1] = ig * inv + ambG * openAvg;
            indirect[k3 + 2] = ib * inv + ambB * openAvg;
            dirDirect[k3] = ddx; dirDirect[k3 + 1] = ddy; dirDirect[k3 + 2] = ddz;
            float s = p.bounceIntensity / total;
            dirIndirect[k3] = idx * s; dirIndirect[k3 + 1] = idy * s; dirIndirect[k3 + 2] = idz * s;
        }

        // ------------------------------------------------------------------ lights

        /// <summary>
        /// Fraction (0..1) of the lamp's bulb that can be seen from the origin. Rays aim at points spread over a disc the size of the bulb.
        /// ignoreB is a second object that never blocks (the prop a probe sits in), or LmBvh.NoObject.
        /// </summary>
        float Visibility(LmLight L, float ox, float oy, float oz, float lx, float ly, float lz, float dist, int rays, float spin, int ignoreB, Ctx ctx)
        {
            if (L.type == 2)
                return bvh.Occluded(ox, oy, oz, lx, ly, lz, TMin, FarAway, L.ignoreObj, ignoreB, ctx.stack) ? 0f : 1f;

            if (L.radius <= 1e-4f || rays <= 1)
            {
                float dx = L.px - ox, dy = L.py - oy, dz = L.pz - oz;
                float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-5f) return 1f;
                float il = 1f / len;
                return bvh.Occluded(ox, oy, oz, dx * il, dy * il, dz * il, TMin, len - 0.02f, L.ignoreObj, ignoreB, ctx.stack) ? 0f : 1f;
            }

            // a frame perpendicular to the direction of the lamp
            float ax, ay, az;
            if (Math.Abs(lx) < 0.9f) { ax = 0f; ay = -lz; az = ly; } else { ax = lz; ay = 0f; az = -lx; }
            float al = (float)Math.Sqrt(ax * ax + ay * ay + az * az);
            ax /= al; ay /= al; az /= al;
            float bx = ly * az - lz * ay, by = lz * ax - lx * az, bz = lx * ay - ly * ax;

            int clear = 0;
            float invRays = 1f / rays;
            for (int k = 0; k < rays; k++)
            {
                float r = (float)Math.Sqrt((k + 0.5f) * invRays) * L.radius;
                float ang = spin + k * 2.3999632f;
                float cu = (float)Math.Cos(ang) * r, cv = (float)Math.Sin(ang) * r;
                float tx = L.px + ax * cu + bx * cv - ox;
                float ty = L.py + ay * cu + by * cv - oy;
                float tz = L.pz + az * cu + bz * cv - oz;
                float len = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (len < 1e-5f) { clear++; continue; }
                float il = 1f / len;
                if (!bvh.Occluded(ox, oy, oz, tx * il, ty * il, tz * il, TMin, len - 0.02f, L.ignoreObj, ignoreB, ctx.stack)) clear++;

                if (k == 2 && rays > 3)
                {
                    // Three rays agree: the lamp is fully lit or fully hidden here, which is almost everywhere.
                    if (clear == 0) return 0f;
                    if (clear == 3) return 1f;
                }
            }
            return clear * invRays;
        }

        /// <summary>
        /// Next event estimation: the direct light at a bounce point, estimated with a single shadow ray towards one lamp that
        /// is picked with a probability proportional to how much light it would bring.
        /// </summary>
        void Nee(float ox, float oy, float oz, float qx, float qy, float qz, float nx, float ny, float nz, float gx, float gy, float gz,
                 Ctx ctx, ref LmRng rng, out float er, out float eg, out float eb)
        {
            er = eg = eb = 0f;
            int start, count;
            grid.Find(qx, qy, qz, out start, out count);
            int dirCount = grid.directional.Length;
            int n = 0;
            float sum = 0f;
            for (int c = 0; c < dirCount + count; c++)
            {
                int li = c < dirCount ? grid.directional[c] : grid.Item(start + c - dirCount);
                float ar, ag, ab, lx, ly, lz, dist;
                if (!grid.lights[li].Eval(qx, qy, qz, nx, ny, nz, gx, gy, gz, out ar, out ag, out ab, out lx, out ly, out lz, out dist)) continue;
                ctx.cand[n++] = li;
                sum += LmMath.Lum(ar, ag, ab);
            }
            if (n == 0 || sum <= 1e-9f) return;

            // pick one
            float pick = rng.Next() * sum;
            int chosen = ctx.cand[n - 1];
            float acc = 0f;
            float chosenW = 0f;
            for (int i = 0; i < n; i++)
            {
                float ar, ag, ab, lx, ly, lz, dist;
                grid.lights[ctx.cand[i]].Eval(qx, qy, qz, nx, ny, nz, gx, gy, gz, out ar, out ag, out ab, out lx, out ly, out lz, out dist);
                float w = LmMath.Lum(ar, ag, ab);
                acc += w;
                if (pick <= acc || i == n - 1) { chosen = ctx.cand[i]; chosenW = w; break; }
            }

            LmLight L = grid.lights[chosen];
            float cr, cg, cb, dx, dy, dz, d;
            if (!L.Eval(qx, qy, qz, nx, ny, nz, gx, gy, gz, out cr, out cg, out cb, out dx, out dy, out dz, out d)) return;

            // a random point on the bulb
            float tx = dx * d, ty = dy * d, tz = dz * d;
            if (L.type != 2 && L.radius > 1e-4f)
            {
                float ax, ay, az;
                if (Math.Abs(dx) < 0.9f) { ax = 0f; ay = -dz; az = dy; } else { ax = dz; ay = 0f; az = -dx; }
                float al = (float)Math.Sqrt(ax * ax + ay * ay + az * az);
                ax /= al; ay /= al; az /= al;
                float bx = dy * az - dz * ay, by = dz * ax - dx * az, bz = dx * ay - dy * ax;
                float r = (float)Math.Sqrt(rng.Next()) * L.radius;
                float ang = rng.Next() * LmMath.TwoPi;
                float cu = (float)Math.Cos(ang) * r, cv = (float)Math.Sin(ang) * r;
                tx += ax * cu + bx * cv;
                ty += ay * cu + by * cv;
                tz += az * cu + bz * cv;
            }
            // the shadow ray starts a little above the surface, so aim from there
            tx += qx - ox; ty += qy - oy; tz += qz - oz;
            float len = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (len < 1e-5f) return;
            float il = 1f / len;
            if (bvh.Occluded(ox, oy, oz, tx * il, ty * il, tz * il, TMin, L.type == 2 ? FarAway : len - 0.02f, L.ignoreObj, ctx.stack)) return;

            float scale = sum / chosenW;      // 1 / probability of having picked this lamp
            er = cr * scale; eg = cg * scale; eb = cb * scale;
        }

        // ------------------------------------------------------------------ surface at a hit

        void HitData(int slot, float hu, float hv, float dx, float dy, float dz,
                     out float qx, out float qy, out float qz, out float gx, out float gy, out float gz,
                     out float nx, out float ny, out float nz, out float ar, out float ag, out float ab)
        {
            int t = bvh.TriangleOfSlot(slot);
            float[] pos = scene.pos;
            int o = t * 9;
            float w0 = 1f - hu - hv;
            qx = w0 * pos[o] + hu * pos[o + 3] + hv * pos[o + 6];
            qy = w0 * pos[o + 1] + hu * pos[o + 4] + hv * pos[o + 7];
            qz = w0 * pos[o + 2] + hu * pos[o + 5] + hv * pos[o + 8];

            float e1x = pos[o + 3] - pos[o], e1y = pos[o + 4] - pos[o + 1], e1z = pos[o + 5] - pos[o + 2];
            float e2x = pos[o + 6] - pos[o], e2y = pos[o + 7] - pos[o + 1], e2z = pos[o + 8] - pos[o + 2];
            gx = e1y * e2z - e1z * e2y; gy = e1z * e2x - e1x * e2z; gz = e1x * e2y - e1y * e2x;
            float gl = (float)Math.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gl < 1e-12f) { gx = 0f; gy = 1f; gz = 0f; } else { gx /= gl; gy /= gl; gz /= gl; }
            if (gx * dx + gy * dy + gz * dz > 0f) { gx = -gx; gy = -gy; gz = -gz; }       // face the ray

            int alb = scene.alb[t];
            if (alb >= 0)
            {
                nx = gx; ny = gy; nz = gz;
                ar = scene.albedoTable[alb * 3]; ag = scene.albedoTable[alb * 3 + 1]; ab = scene.albedoTable[alb * 3 + 2];
                return;
            }

            float[] nrm = scene.nrm;
            nx = w0 * nrm[o] + hu * nrm[o + 3] + hv * nrm[o + 6];
            ny = w0 * nrm[o + 1] + hu * nrm[o + 4] + hv * nrm[o + 7];
            nz = w0 * nrm[o + 2] + hu * nrm[o + 5] + hv * nrm[o + 8];
            float nl = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-6f) { nx = gx; ny = gy; nz = gz; } else { nx /= nl; ny /= nl; nz /= nl; }
            if (nx * gx + ny * gy + nz * gz < 0f) { nx = -nx; ny = -ny; nz = -nz; }

            // The same blend the shader makes: dirt where the path mask says so and the surface faces up.
            float[] mask = scene.mask;
            float m = w0 * mask[t * 3] + hu * mask[t * 3 + 1] + hv * mask[t * 3 + 2];
            float f = (ny - 0.3f) / 0.5f;
            f = f < 0f ? 0f : (f > 1f ? 1f : f);
            m *= f * f * (3f - 2f * f);
            ar = rockR + (dirtR - rockR) * m;
            ag = rockG + (dirtG - rockG) * m;
            ab = rockB + (dirtB - rockB) * m;
        }

        /// <summary>A cosine weighted direction around (nx, ny, nz) from two numbers in [0, 1).</summary>
        static void CosineDirection(float nx, float ny, float nz, float u1, float u2, out float dx, out float dy, out float dz)
        {
            float r = (float)Math.Sqrt(u1);
            float phi = LmMath.TwoPi * u2;
            float x = r * (float)Math.Cos(phi), y = r * (float)Math.Sin(phi);
            float z = (float)Math.Sqrt(Math.Max(0f, 1f - u1));

            float tx, ty, tz;
            if (Math.Abs(nx) < 0.9f) { tx = 0f; ty = -nz; tz = ny; } else { tx = nz; ty = 0f; tz = -nx; }
            float tl = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
            tx /= tl; ty /= tl; tz /= tl;
            float bx = ny * tz - nz * ty, by = nz * tx - nx * tz, bz = nx * ty - ny * tx;

            dx = x * tx + y * bx + z * nx;
            dy = x * ty + y * by + z * ny;
            dz = x * tz + y * bz + z * nz;
        }
    }
}
