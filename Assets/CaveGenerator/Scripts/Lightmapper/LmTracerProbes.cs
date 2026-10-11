using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HavocCave.Lightmapping
{
    /// <summary>
    /// Light probes for props. A prop is not lightmapped (it has no atlas texels), so instead the tracer measures, at one point in
    /// the middle of the prop, all the light that reaches it, the same way it does for a texel:
    ///   lamps    - each lamp that can see the point is one direction the light comes from (soft shadows, same falloff as the walls)
    ///   the rest - rays over the whole sphere. What they hit is lit like a texel is (next event estimation plus bounces), so the
    ///              floor, walls and other props bounce the lamps' light onto the prop; what they miss is the ambient colour.
    /// The result is a list of (direction, colour) samples. The Unity side turns them into spherical harmonics, which URP's
    /// lit shaders read as ambient light, so a crate lights up on the side that faces a lamp, with the floor's bounce below.
    /// </summary>
    internal sealed partial class LmTracer
    {
        /// <summary>One entry per probe: 6 floats per sample (unit direction towards the light, then linear rgb).</summary>
        public float[][] BakeProbes(CaveBakeProbe[] probes, CancellationToken token, int threads)
        {
            var result = new float[probes.Length][];
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = token };
            int candidates = grid.maxCandidates;
            Parallel.For(0, probes.Length, opts, () => new Ctx(candidates), (i, state, ctx) =>
            {
                result[i] = BakeProbe(probes[i], i, ctx);
                return ctx;
            }, ctx => { });
            return result;
        }

        float[] BakeProbe(CaveBakeProbe probe, int index, Ctx ctx)
        {
            float px = probe.position.x, py = probe.position.y, pz = probe.position.z;
            int own = probe.ignoreObject >= 0 ? probe.ignoreObject : LmBvh.NoObject;
            var rng = new LmRng((uint)index * 0x9E3779B1u + (uint)p.seed * 0x85EBCA6Bu + 0x68E31DA4u);
            float spin = rng.Next() * LmMath.TwoPi;

            int rays = Math.Max(8, p.probeRays);
            var samples = new List<float>(6 * (rays + 8));

            // ---- the lamps
            int start, count;
            grid.Find(px, py, pz, out start, out count);
            int dirCount = grid.directional.Length;
            for (int c = 0; c < dirCount + count; c++)
            {
                int li = c < dirCount ? grid.directional[c] : grid.Item(start + c - dirCount);
                LmLight L = grid.lights[li];
                if (own != LmBvh.NoObject && L.ignoreObj == own) continue;       // a fixture does not light itself

                float lx, ly, lz;
                if (L.type == 2) { lx = -L.dx; ly = -L.dy; lz = -L.dz; }
                else
                {
                    float tx = L.px - px, ty = L.py - py, tz = L.pz - pz;
                    float len = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    if (len < 1e-4f) continue;
                    lx = tx / len; ly = ty / len; lz = tz / len;
                }

                // Asking for a surface that faces the lamp head on gives the light without any cosine in it, which is what a direction sample is.
                float er, eg, eb, ex, ey, ez, dist;
                if (!L.Eval(px, py, pz, lx, ly, lz, lx, ly, lz, out er, out eg, out eb, out ex, out ey, out ez, out dist)) continue;
                float vis = Visibility(L, px, py, pz, ex, ey, ez, dist, p.shadowRays, spin, own, ctx);
                if (vis <= 0f) continue;
                samples.Add(ex); samples.Add(ey); samples.Add(ez);
                samples.Add(er * vis); samples.Add(eg * vis); samples.Add(eb * vis);
            }

            // ---- everything else: radiance from rays over the whole sphere
            // Every sample stands for the same slice of the sphere (4 pi / rays), and the diffuse light a surface takes from radiance L
            // is (1 / pi) * L * cos * solid angle, so each sample's colour is its radiance times 4 / rays.
            float weight = 4f / rays;
            int bounces = p.bounces;
            for (int k = 0; k < rays; k++)
            {
                // Equal area bands in z with a random height inside each band, turned by the golden angle: an even spread with no pattern.
                float z = 1f - 2f * (k + rng.Next()) / rays;
                float rr = (float)Math.Sqrt(Math.Max(0f, 1f - z * z));
                float phi = spin + k * 2.3999632f;
                float dx = rr * (float)Math.Cos(phi), dy = rr * (float)Math.Sin(phi), dz = z;

                float lr = 0f, lg = 0f, lb = 0f;
                float tHit, hu, hv;
                int slot;
                if (!bvh.Closest(px, py, pz, dx, dy, dz, TMin, FarAway, own, ctx.stack, out tHit, out slot, out hu, out hv))
                {
                    if (hasAmbient) { lr = ambR; lg = ambG; lb = ambB; }
                }
                else
                {
                    float open = Math.Min(1f, tHit / ambientRange);
                    if (hasAmbient) { lr = ambR * open; lg = ambG * open; lb = ambB * open; }

                    // follow the path, as a texel does
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
                    lr += sr * p.bounceIntensity; lg += sg * p.bounceIntensity; lb += sb * p.bounceIntensity;
                }

                if (lr <= 0f && lg <= 0f && lb <= 0f) continue;
                samples.Add(dx); samples.Add(dy); samples.Add(dz);
                samples.Add(lr * weight); samples.Add(lg * weight); samples.Add(lb * weight);
            }
            return samples.ToArray();
        }
    }
}
