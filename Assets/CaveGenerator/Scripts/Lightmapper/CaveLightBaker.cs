using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HavocCave.Lightmapping
{
    /// <summary>
    /// The cave lightmapper. Give it the chunk meshes the mesher made, the lights and any props that should cast shadows,
    /// and it
    ///   1. unwraps the meshes (adds UV2, splitting vertices where charts meet) and packs them into one atlas,
    ///   2. traces the light for every texel of that atlas,
    ///   3. denoises and encodes it.
    /// It never touches the Unity engine, so Run can be called on a worker thread. The chunk meshes are changed in place.
    /// </summary>
    public sealed class CaveLightBaker
    {
        const int Pad = 2;                  // texels of gutter round every chart
        const int Dilate = 2;               // texels the colour is spread beyond a chart's edge
        const float ChartCosine = 0.55f;    // a chart may bend this far (cosine) away from its first triangle; 0.55 is 57 degrees
        const float ChartExtent = 16f;      // longest side of a chart in metres

        // read by the main thread while a worker thread writes them: a stale value for a frame is harmless
        volatile string stage = "";
        volatile float stageStart, stageEnd;
        volatile LmTracer tracer;

        /// <summary>What the bake is doing right now, for a progress bar.</summary>
        public string Stage { get { return stage; } }

        /// <summary>0..1, rough.</summary>
        public float Progress
        {
            get
            {
                LmTracer t = tracer;
                if (t != null) return Mathf.Lerp(stageStart, stageEnd, t.Progress);
                return stageStart;
            }
        }

        void SetStage(string name, float from, float to)
        {
            stage = name;
            stageStart = from;
            stageEnd = to;
        }

        /// <summary>
        /// Bakes lightmaps for the chunks. Throws OperationCanceledException when the token is cancelled.
        /// p.texelsPerMeter is the density asked for; the result says what fitted.
        /// </summary>
        public CaveBakeResult Run(CaveChunkMesh[] chunks, CaveBakeLight[] bakeLights, CaveBakeOccluder[] occluders, LmParams p, int threads, CancellationToken token)
        {
            return Run(chunks, bakeLights, occluders, null, p, threads, token);
        }

        /// <summary>As above, and also measures the light at each probe (one per prop): the result's probes array has one entry for each.</summary>
        public CaveBakeResult Run(CaveChunkMesh[] chunks, CaveBakeLight[] bakeLights, CaveBakeOccluder[] occluders, CaveBakeProbe[] probes, LmParams p, int threads, CancellationToken token)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            threads = Math.Max(1, threads);
            var opts = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token };
            if (occluders == null) occluders = new CaveBakeOccluder[0];

            // ---------------------------------------------------------------- 1. unwrap
            SetStage("Unwrapping", 0f, 0.05f);
            
            
            var charts = new LmChunkCharts[chunks.Length];
            Parallel.For(0, chunks.Length, opts, i => { charts[i] = LmUnwrap.BuildCharts(chunks[i], i, ChartCosine, ChartExtent, token); });

            var all = new List<LmChart>();
            var chartBase = new int[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
            {
                chartBase[i] = all.Count;
                all.AddRange(charts[i].charts);
            }

            int atlasW, atlasH;
            float density;
            if (!LmUnwrap.Pack(all, p.texelsPerMeter, Pad, p.maxSize, out atlasW, out atlasH, out density))
                throw new InvalidOperationException("The cave could not be packed into a " + p.maxSize + " texel lightmap.");

            Parallel.For(0, chunks.Length, opts, i => { LmUnwrap.Emit(chunks[i], charts[i], atlasW, atlasH, density, Pad); });

            // ---------------------------------------------------------------- 2. the scene
            SetStage("Building scene", 0.05f, 0.1f);
            var triOffset = new int[chunks.Length + 1];
            for (int i = 0; i < chunks.Length; i++) triOffset[i + 1] = triOffset[i] + chunks[i].triangles.Length / 3;
            int caveTris = triOffset[chunks.Length];
            int occTris = 0;
            for (int i = 0; i < occluders.Length; i++) occTris += occluders[i].triangles.Length / 9;
            int total = caveTris + occTris;

            var scene = new LmScene
            {
                pos = new float[total * 9],
                nrm = new float[total * 9],
                mask = new float[total * 3],
                alb = new int[total],
                obj = new int[total],
                caveTriangles = caveTris,
                albedoTable = new float[Math.Max(1, occluders.Length) * 3]
            };
            var triChart = new int[caveTris];
            for (int i = 0; i < total; i++) { scene.alb[i] = -1; scene.obj[i] = -1; }

            Parallel.For(0, chunks.Length, opts, ci =>
            {
                CaveChunkMesh m = chunks[ci];
                Vector3 org = m.origin;
                int baseTri = triOffset[ci];
                int nTri = m.triangles.Length / 3;
                for (int t = 0; t < nTri; t++)
                {
                    int gt = baseTri + t;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = m.triangles[t * 3 + k];
                        Vector3 v = m.vertices[vi];
                        Vector3 n = m.normals[vi];
                        int o = gt * 9 + k * 3;
                        scene.pos[o] = v.x + org.x; scene.pos[o + 1] = v.y + org.y; scene.pos[o + 2] = v.z + org.z;
                        scene.nrm[o] = n.x; scene.nrm[o + 1] = n.y; scene.nrm[o + 2] = n.z;
                        scene.mask[gt * 3 + k] = m.colors[vi].r * (1f / 255f);
                    }
                }
                // triangles were written chart by chart, so a triangle's chart is found from its position
                var cc = charts[ci];
                for (int c = 0; c < cc.charts.Count; c++)
                {
                    LmChart ch = cc.charts[c];
                    for (int t = ch.first; t < ch.first + ch.count; t++) triChart[baseTri + t] = chartBase[ci] + c;
                }
            });

            int cursor = caveTris;
            for (int i = 0; i < occluders.Length; i++)
            {
                CaveBakeOccluder oc = occluders[i];
                scene.albedoTable[i * 3] = oc.albedo.r; scene.albedoTable[i * 3 + 1] = oc.albedo.g; scene.albedoTable[i * 3 + 2] = oc.albedo.b;
                int nTri = oc.triangles.Length / 9;
                for (int t = 0; t < nTri; t++)
                {
                    Array.Copy(oc.triangles, t * 9, scene.pos, cursor * 9, 9);
                    scene.alb[cursor] = i;
                    scene.obj[cursor] = oc.objectId;
                    cursor++;
                }
            }

            // bounds of the cave
            float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
            for (int t = 0; t < caveTris * 3; t++)
                for (int a = 0; a < 3; a++)
                {
                    float v = scene.pos[t * 3 + a];
                    if (v < mn[a]) mn[a] = v;
                    if (v > mx[a]) mx[a] = v;
                }
            scene.min = mn;
            scene.max = mx;

            token.ThrowIfCancellationRequested();
            scene.bvh = LmBvh.Build(scene.pos, scene.obj, total, token);

            var lights = new LmLight[bakeLights != null ? bakeLights.Length : 0];
            for (int i = 0; i < lights.Length; i++) lights[i] = LmLight.From(bakeLights[i]);
            var grid = new LmLightGrid(lights, mn, mx);

            // ---------------------------------------------------------------- 3. texels
            SetStage("Rasterizing", 0.1f, 0.12f);
            var atlas = new LmAtlas(atlasW, atlasH);
            Parallel.For(0, chunks.Length, opts, ci =>
            {
                CaveChunkMesh m = chunks[ci];
                int baseTri = triOffset[ci];
                int nTri = m.triangles.Length / 3;
                float sx = atlasW, sy = atlasH;
                for (int t = 0; t < nTri; t++)
                {
                    Vector2 a = m.uv2[m.triangles[t * 3]], b = m.uv2[m.triangles[t * 3 + 1]], c = m.uv2[m.triangles[t * 3 + 2]];
                    atlas.Rasterize(baseTri + t, a.x * sx, a.y * sy, b.x * sx, b.y * sy, c.x * sx, c.y * sy);
                }
            });
            atlas.CollectValid();

            // ---------------------------------------------------------------- 4. trace
            SetStage("Baking light", 0.12f, 0.85f);
            tracer = new LmTracer(scene, atlas, grid, p);
            tracer.Run(token, threads);

            float[][] probeSamples = null;
            if (probes != null && probes.Length > 0)
            {
                SetStage("Lighting props", 0.85f, 0.85f);
                probeSamples = tracer.BakeProbes(probes, token, threads);
            }

            // ---------------------------------------------------------------- 5. clean up and encode
            SetStage("Denoising", 0.85f, 0.85f);
            float[] ind, dirI;
            LmPost.Denoise(atlas, triChart, tracer.normal, tracer.indirect, tracer.dirIndirect, p.denoise, threads, token, out ind, out dirI);
            if (!ReferenceEquals(ind, tracer.indirect)) tracer.ReleaseIndirect();      // denoising made new buffers; drop the raw ones

            SetStage("Encoding", 0.93f, 0.93f);
            float[] color, dir;
            LmPost.Finish(atlas, tracer.normal, tracer.direct, ind, tracer.dirDirect, dirI, p.directional, Dilate, threads, token, out color, out dir);
            int texels = atlasW * atlasH;
            var result = new CaveBakeResult
            {
                width = atlasW,
                height = atlasH,
                color = LmPost.EncodeColor(color, texels),
                direction = dir != null ? LmPost.EncodeDirection(dir, texels) : null,
                probes = probeSamples,
                charts = all.Count,
                texels = atlas.validCount,
                triangles = caveTris,
                lights = lights.Length,
                occluderTriangles = occTris,
                texelsPerMeter = density
            };
            tracer = null;
            SetStage("Done", 1f, 1f);
            result.seconds = (float)timer.Elapsed.TotalSeconds;
            return result;
        }
    }
}
