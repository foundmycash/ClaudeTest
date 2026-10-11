using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HavocCave
{
    /// <summary>Splits the cave's bounding box into chunks and keeps the ones that contain wall. Shared by Generate and the preview.</summary>
    public static class CaveChunking
    {
        public static void Plan(CaveLayout layout, CaveField field, float vs, out Vector3 grid,
                                List<Vector3Int> offsets, List<CaveField.Region> regions)
        {
            float chunkSize = CaveMesher.Cells * vs;
            grid = new Vector3(
                Mathf.Floor(layout.min.x / chunkSize),
                Mathf.Floor(layout.min.y / chunkSize),
                Mathf.Floor(layout.min.z / chunkSize)) * chunkSize;
            int nx = Mathf.CeilToInt((layout.max.x - grid.x) / chunkSize);
            int ny = Mathf.CeilToInt((layout.max.y - grid.y) / chunkSize);
            int nz = Mathf.CeilToInt((layout.max.z - grid.z) / chunkSize);

            Vector3 pad = Vector3.one * (vs * 2f);
            for (int iz = 0; iz < nz; iz++)
            for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                Vector3 origin = grid + new Vector3(ix, iy, iz) * chunkSize;
                CaveField.Region region = field.Query(origin - pad, origin + Vector3.one * chunkSize + pad);
                if (region.segCount == 0 && region.roomCount == 0) continue;     // nothing to mesh here (a room can reach past its tunnel)
                offsets.Add(new Vector3Int(ix * CaveMesher.Cells, iy * CaveMesher.Cells, iz * CaveMesher.Cells));
                regions.Add(region);
            }
        }
    }

    /// <summary>A quick, coarse model of a cave, built on a worker thread so the editor stays responsive.</summary>
    public sealed class CavePreviewData
    {
        public int seed;
        public CaveLayout layout;
        public Vector3[] vertices;       // layout space
        public Color32[] colors;
        public int[] triangles;
        public Vector3 boundsMin, boundsMax;
        public Vector3 spawnFloor;       // the point of the layout that ends up at the generator's position
        public float voxelSize;          // the voxel size a real Generate would use
        public bool polyLimited;
        public float estimatedTriangles; // what a real Generate would produce
        public float seconds;
        public float tunnelMeters;
        public int rooms, shafts, loops, formations, tunnels;
        public float yMin, yMax;
    }

    public static class CavePreviewBuilder
    {
        /// <summary>
        /// effective = the settings with Cave Size and Tunnel Scale already applied (CaveGenerator.BuildEffectiveSettings).
        /// previewTriangles is how detailed the preview model should be; it is kept coarse so it builds quickly.
        /// Returns null if cancelled.
        /// </summary>
        public static CavePreviewData Build(CaveSettings effective, int seed, bool usePolyCount, float polyCount, float previewTriangles, CancellationToken token)
        {
            return Build(effective, seed, usePolyCount, polyCount, previewTriangles, token, null);
        }

        /// <summary>spawn must be a copy (CaveSpawnCave.Clone) when this runs on a worker thread.</summary>
        public static CavePreviewData Build(CaveSettings effective, int seed, bool usePolyCount, float polyCount, float previewTriangles, CancellationToken token, CaveSpawnCave spawn)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            CaveSettings cfg = effective.Clone();
            CaveLayout layout = CaveLayout.Build(cfg, seed, spawn);
            if (token.IsCancellationRequested) return null;

            bool limited = false;
            float realVoxel = usePolyCount ? layout.VoxelSizeForTriangles(cfg, polyCount, out limited) : cfg.voxelSize;

            var data = new CavePreviewData
            {
                seed = seed,
                layout = layout,
                voxelSize = realVoxel,
                polyLimited = limited,
                estimatedTriangles = layout.EstimateTriangles(cfg, realVoxel),
                tunnelMeters = layout.totalLength,
                rooms = layout.rooms.Length,
                shafts = layout.shafts.Length,
                tunnels = layout.paths.Count,
                yMin = float.MaxValue,
                yMax = float.MinValue
            };
            for (int i = 0; i < layout.paths.Count; i++) if (layout.paths[i].isLoop) data.loops++;
            for (int i = 0; i < layout.blobs.Length; i++) if (layout.blobs[i].formation) data.formations++;

            // The model is meshed coarsely, but never finer than a real Generate would be.
            bool ignored;
            float vs = Mathf.Max(layout.VoxelSizeForTriangles(cfg, previewTriangles, out ignored), realVoxel);
            var field = new CaveField(layout, cfg, seed);

            Vector3 floor;
            data.spawnFloor = field.RayMarch(field.All(), layout.spawn, Vector3.down, 8f, out floor) ? floor : layout.spawn + Vector3.down * 1.4f;

            Vector3 grid;
            var offsets = new List<Vector3Int>();
            var regions = new List<CaveField.Region>();
            CaveChunking.Plan(layout, field, vs, out grid, offsets, regions);
            if (token.IsCancellationRequested) return null;

            int count = offsets.Count;
            var meshes = new CaveChunkMesh[count];
            var noLamps = new CaveLamp[0];
            var noRegions = new CaveField.Region[0];
            int threads = Mathf.Clamp(System.Environment.ProcessorCount - 1, 1, 8);     // SystemInfo can only be read on the main thread
            float eps = vs * 0.4f;
            Vector3 gridCopy = grid;
            try
            {
                var local = new ThreadLocal<CaveMesher>(() => new CaveMesher());
                try
                {
                    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token },
                        i => { meshes[i] = local.Value.Build(field, regions[i], gridCopy, offsets[i].x, offsets[i].y, offsets[i].z, vs, eps, noLamps, noRegions, 0f, 0f); });
                }
                finally
                {
                    local.Dispose();
                }
            }
            catch (System.OperationCanceledException)
            {
                return null;
            }

            int vc = 0, tc = 0;
            for (int i = 0; i < count; i++)
            {
                if (meshes[i] == null) continue;
                vc += meshes[i].vertices.Length;
                tc += meshes[i].triangles.Length;
                for (int v = 0; v < meshes[i].vertices.Length; v++)
                {
                    float y = meshes[i].origin.y + meshes[i].vertices[v].y;
                    if (y < data.yMin) data.yMin = y;
                    if (y > data.yMax) data.yMax = y;
                }
            }

            data.vertices = new Vector3[vc];
            data.colors = new Color32[vc];
            data.triangles = new int[tc];
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            float ySpan = Mathf.Max(1f, data.yMax - data.yMin);
            int vo = 0, to = 0;
            for (int i = 0; i < count; i++)
            {
                CaveChunkMesh m = meshes[i];
                if (m == null) continue;
                for (int v = 0; v < m.vertices.Length; v++)
                {
                    Vector3 p = m.origin + m.vertices[v];
                    data.vertices[vo + v] = p;
                    data.colors[vo + v] = Clay(m.normals[v], m.colors[v], (p.y - data.yMin) / ySpan);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
                for (int t = 0; t < m.triangles.Length; t++) data.triangles[to + t] = m.triangles[t] + vo;
                vo += m.vertices.Length;
                to += m.triangles.Length;
            }
            data.boundsMin = vc > 0 ? lo : layout.min;
            data.boundsMax = vc > 0 ? hi : layout.max;
            timer.Stop();
            data.seconds = (float)timer.Elapsed.TotalSeconds;
            return data;
        }

        static readonly Vector3 LightDir = new Vector3(0.35f, 0.8f, 0.45f).normalized;

        /// <summary>Clay look: fixed light from above, darker in tight corners, darker the lower the level, tan where the dirt path is.</summary>
        static Color32 Clay(Vector3 n, Color32 vertexColor, float height01)
        {
            float dirt = vertexColor.r / 255f;
            float ao = vertexColor.g / 255f;
            float ndl = Mathf.Max(0f, Vector3.Dot(n, LightDir));
            float hemi = 0.5f + 0.5f * n.y;
            float light = 0.3f + 0.5f * ndl + 0.25f * hemi;
            float shade = light * (0.5f + 0.5f * ao) * Mathf.Lerp(0.72f, 1.05f, height01);
            Color rock = new Color(0.62f, 0.60f, 0.56f);
            Color dirtColor = new Color(0.86f, 0.66f, 0.40f);
            Color c = Color.Lerp(rock, dirtColor, dirt) * shade;
            return new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
                               (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
                               (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255), 255);
        }
    }

    /// <summary>Draws the layout as a top-down map into a pixel array: tunnels, rooms and shafts, coloured by height.</summary>
    public static class CaveMapRenderer
    {
        struct Item
        {
            public float y;
            public int kind;      // 0 tunnel segment, 1 room, 2 shaft
            public int a, b;
        }

        static Color32 HeightColor(float t)
        {
            Color low = new Color(0.20f, 0.42f, 0.95f);
            Color mid = new Color(0.30f, 0.85f, 0.50f);
            Color high = new Color(0.98f, 0.72f, 0.20f);
            t = Mathf.Clamp01(t);
            Color c = t < 0.5f ? Color.Lerp(low, mid, t * 2f) : Color.Lerp(mid, high, (t - 0.5f) * 2f);
            return c;
        }

        /// <summary>The colour used for a height between yMin and yMax (for the legend).</summary>
        public static Color32 LegendColor(float t)
        {
            return HeightColor(t);
        }

        public static Color32[] Render(CaveLayout layout, int width, int height, out float yMin, out float yMax)
        {
            var px = new Color32[width * height];
            var bg = new Color32(30, 32, 38, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            yMin = float.MaxValue;
            yMax = float.MinValue;
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (CavePath path in layout.paths)
                for (int i = 0; i < path.pos.Count; i++)
                {
                    Vector3 p = path.pos[i];
                    float r = path.halfW[i] + 1f;
                    x0 = Mathf.Min(x0, p.x - r); x1 = Mathf.Max(x1, p.x + r);
                    z0 = Mathf.Min(z0, p.z - r); z1 = Mathf.Max(z1, p.z + r);
                    yMin = Mathf.Min(yMin, p.y - path.halfH[i]); yMax = Mathf.Max(yMax, p.y - path.halfH[i]);
                }
            foreach (CaveRoom rm in layout.rooms)
            {
                float r = rm.radius * rm.stretch * (1f + rm.warp * 0.5f) + 0.5f;
                x0 = Mathf.Min(x0, rm.floorCenter.x - r); x1 = Mathf.Max(x1, rm.floorCenter.x + r);
                z0 = Mathf.Min(z0, rm.floorCenter.z - r); z1 = Mathf.Max(z1, rm.floorCenter.z + r);
                yMin = Mathf.Min(yMin, rm.floorCenter.y); yMax = Mathf.Max(yMax, rm.floorCenter.y);
            }
            if (x0 > x1 || yMin > yMax) return px;
            if (yMax - yMin < 1f) yMax = yMin + 1f;

            float margin = 6f;
            x0 -= margin; x1 += margin; z0 -= margin; z1 += margin;
            float scale = Mathf.Min(width / (x1 - x0), height / (z1 - z0));
            float offX = (width - (x1 - x0) * scale) * 0.5f;
            float offY = (height - (z1 - z0) * scale) * 0.5f;
            float ymin = yMin, ymax = yMax;

            // faint grid, one line every 20 m
            var grid = new Color32(44, 47, 56, 255);
            for (float gx = Mathf.Ceil(x0 / 20f) * 20f; gx < x1; gx += 20f)
            {
                int cx = Mathf.RoundToInt((gx - x0) * scale + offX);
                if (cx >= 0 && cx < width) for (int y = 0; y < height; y++) px[y * width + cx] = grid;
            }
            for (float gz = Mathf.Ceil(z0 / 20f) * 20f; gz < z1; gz += 20f)
            {
                int cy = Mathf.RoundToInt((gz - z0) * scale + offY);
                if (cy >= 0 && cy < height) for (int x = 0; x < width; x++) px[cy * width + x] = grid;
            }

            var items = new List<Item>();
            for (int i = 0; i < layout.segments.Length; i++)
            {
                if (layout.segments[i].path == -1) continue;          // shafts are drawn as circles below
                items.Add(new Item { y = (layout.segments[i].a.y + layout.segments[i].b.y) * 0.5f - (layout.segments[i].ha + layout.segments[i].hb) * 0.5f, kind = 0, a = i });
            }
            for (int i = 0; i < layout.rooms.Length; i++)
                items.Add(new Item { y = layout.rooms[i].floorCenter.y + 0.2f, kind = 1, a = i });
            for (int i = 0; i < layout.shafts.Length; i++)
                items.Add(new Item { y = Mathf.Min(layout.shafts[i].top.y, layout.shafts[i].bottom.y) + 0.4f, kind = 2, a = i });
            items.Sort((p, q) => p.y.CompareTo(q.y));

            Color32 outline = new Color32(12, 13, 16, 255);
            foreach (Item it in items)
            {
                Color32 fill = HeightColor((it.y - ymin) / (ymax - ymin));
                if (it.kind == 0)
                {
                    CaveSegment sg = layout.segments[it.a];
                    float rad = Mathf.Max(1.4f, (sg.wa + sg.wb) * 0.5f * scale);
                    Vector2 a = new Vector2((sg.a.x - x0) * scale + offX, (sg.a.z - z0) * scale + offY);
                    Vector2 b = new Vector2((sg.b.x - x0) * scale + offX, (sg.b.z - z0) * scale + offY);
                    Capsule(px, width, height, a, b, rad + 1.3f, outline, 1f);
                    Capsule(px, width, height, a, b, rad, fill, 1f);
                }
                else if (it.kind == 1)
                {
                    CaveRoom rm = layout.rooms[it.a];
                    Vector2 c = new Vector2((rm.floorCenter.x - x0) * scale + offX, (rm.floorCenter.z - z0) * scale + offY);
                    bool special = rm.kind == (int)CaveRoomKind.Pit || rm.kind == (int)CaveRoomKind.Chimney;
                    Color32 rf = Color32.Lerp(fill, new Color32(255, 255, 255, 255), 0.18f);
                    Ellipse(px, width, height, c, rm, scale, 1.4f, outline);
                    Ellipse(px, width, height, c, rm, scale, 0f, rf);
                    if (special) Disc(px, width, height, c, Mathf.Max(2f, rm.radius * scale * 0.35f), new Color32(20, 20, 24, 90));
                }
                else
                {
                    CaveShaft sh = layout.shafts[it.a];
                    Vector2 c = new Vector2((sh.top.x - x0) * scale + offX, (sh.top.z - z0) * scale + offY);
                    float rad = Mathf.Max(3f, sh.radius * scale);
                    Disc(px, width, height, c, rad + 1.5f, outline);
                    Disc(px, width, height, c, rad, sh.up ? new Color32(210, 240, 255, 255) : new Color32(120, 30, 30, 255));
                    Disc(px, width, height, c, rad * 0.45f, sh.up ? new Color32(120, 190, 235, 255) : new Color32(30, 8, 8, 255));
                }
            }

            // spawn: a white ring with a green middle, and a line pointing the way the player faces
            Vector2 sp = new Vector2((layout.spawn.x - x0) * scale + offX, (layout.spawn.z - z0) * scale + offY);
            Vector2 tip = sp + new Vector2(layout.spawnForward.x, layout.spawnForward.z) * 14f;
            Capsule(px, width, height, sp, tip, 1.6f, new Color32(255, 255, 255, 255), 1f);
            Disc(px, width, height, sp, 5.5f, new Color32(255, 255, 255, 255));
            Disc(px, width, height, sp, 3.8f, new Color32(40, 200, 90, 255));
            return px;
        }

        static void Blend(Color32[] px, int i, Color32 c, float alpha)
        {
            alpha *= c.a / 255f;
            if (alpha <= 0f) return;
            Color32 d = px[i];
            px[i] = new Color32((byte)(d.r + (c.r - d.r) * alpha), (byte)(d.g + (c.g - d.g) * alpha), (byte)(d.b + (c.b - d.b) * alpha), 255);
        }

        static void Disc(Color32[] px, int w, int h, Vector2 c, float r, Color32 col)
        {
            Capsule(px, w, h, c, c, r, col, 1f);
        }

        static void Capsule(Color32[] px, int w, int h, Vector2 a, Vector2 b, float r, Color32 col, float strength)
        {
            int xa = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - r - 1f));
            int xb = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + r + 1f));
            int ya = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - r - 1f));
            int yb = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + r + 1f));
            Vector2 ab = b - a;
            float l2 = ab.sqrMagnitude;
            for (int y = ya; y <= yb; y++)
            for (int x = xa; x <= xb; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float t = l2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
                float d = (p - (a + ab * t)).magnitude;
                float cover = Mathf.Clamp01(r - d + 0.5f);
                if (cover > 0f) Blend(px, y * w + x, col, cover * strength);
            }
        }

        static void Ellipse(Color32[] px, int w, int h, Vector2 c, CaveRoom rm, float scale, float grow, Color32 col)
        {
            float rx = rm.radius * rm.stretch * scale;
            float rz = rm.radius * scale;
            float reach = Mathf.Max(rx, rz) * (1f + rm.warp * 0.5f) + grow + 2f;
            int xa = Mathf.Max(0, Mathf.FloorToInt(c.x - reach));
            int xb = Mathf.Min(w - 1, Mathf.CeilToInt(c.x + reach));
            int ya = Mathf.Max(0, Mathf.FloorToInt(c.y - reach));
            int yb = Mathf.Min(h - 1, Mathf.CeilToInt(c.y + reach));
            for (int y = ya; y <= yb; y++)
            for (int x = xa; x <= xb; x++)
            {
                float dx = x + 0.5f - c.x;
                float dz = y + 0.5f - c.y;
                float lx = dx * rm.cosYaw + dz * rm.sinYaw;
                float lz = -dx * rm.sinYaw + dz * rm.cosYaw;
                float ang = Mathf.Atan2(lz, lx);
                float lump = 1f + rm.warp * 0.45f * Mathf.Sin(ang * 3f + rm.noiseSeed * 0.37f) * 0.8f + rm.warp * 0.3f * Mathf.Sin(ang * 5f + rm.noiseSeed * 0.11f);
                float ex = rx * lump + grow;
                float ez = rz * lump + grow;
                float k = Mathf.Sqrt(lx * lx / (ex * ex) + lz * lz / (ez * ez));
                // convert "k = 1 at the edge" to a pixel distance for anti-aliasing
                float dist = (k - 1f) * Mathf.Min(ex, ez);
                float cover = Mathf.Clamp01(0.5f - dist);
                if (cover > 0f) Blend(px, y * w + x, col, cover);
            }
        }
    }
}
