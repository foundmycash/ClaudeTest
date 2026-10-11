using System.Collections.Generic;
using QuestOptimization;
using UnityEngine;
using UnityEngine.Rendering;

namespace HavocForest
{
    /// <summary>
    /// Draws the forest's placed objects with GPU instancing: every prefab's copies go out in a handful of draw calls per
    /// level of detail instead of one draw call each. Works per camera (game, VR, Scene view) and costs no allocations per
    /// frame.
    ///
    /// How it decides what to draw:
    ///   - instances are grouped in square cells; a cell is skipped when it lies outside a wide cone around the view
    ///     direction (wide enough for both eyes of a headset) or beyond the draw distance;
    ///   - the draw distance is where the fog becomes opaque (nothing behind fog is drawn), or the category's own limit;
    ///     the Scene view draws further (Scene View Draw Distance), as it also looks at the forest from outside the fog;
    ///   - each instance picks its level of detail by distance, from the prefab's LOD Group (screen sizes are turned into
    ///     distances for the given field of view), scaled by Quality Settings > LOD Bias.
    /// Prefabs with behaviour (lights, scripts...) are real GameObjects instead and are not drawn here.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ForestWorld))]
    [AddComponentMenu("Havoc/Forest Instanced Renderer")]
    public class ForestInstancedRenderer : MonoBehaviour
    {
        const int MaxPerDraw = 500;     // single-pass instanced stereo doubles the instance count; keep well under 1023

        [Tooltip("Size in metres of the culling cells.")]
        [Range(8f, 64f)] public float cellSize = 24f;

        [Tooltip("Half-angle in degrees of the cone around the view direction that is drawn. 70 covers both eyes of a Quest with margin for quick head turns.")]
        [Range(30f, 90f)] public float viewConeAngle = 70f;

        [Tooltip("Field of view used to turn LOD Group screen sizes into switch distances.")]
        [Range(40f, 120f)] public float lodFieldOfView = 90f;

        [Tooltip("Multiplier on every LOD switch distance (on top of Quality Settings > LOD Bias). Lower = cheaper, more popping.")]
        [Range(0.25f, 3f)] public float lodDistanceScale = 1f;

        [Tooltip("Multiplier on draw and LOD distances on the Quest 2 / Quest 1 (Low tier).")]
        [Range(0.3f, 1f)] public float lowTierDistanceScale = 0.85f;

        [Tooltip("Nothing is drawn beyond this, even without fog.")]
        [Min(10f)] public float maxDrawDistance = 250f;

        [Tooltip("Draw in the Scene view too.")]
        public bool drawInSceneView = true;

        [Tooltip("How far the Scene view draws, whatever the fog. The fog only hides what lies behind it for an eye inside the forest; seen from above or from outside the forest the ground shows, so the trees must too. Game cameras draw only as far as the fog lets them see.")]
        [Min(10f)] public float sceneViewDrawDistance = 1000f;

        /// <summary>Draw calls issued for the last game camera.</summary>
        public int LastDrawCalls { get; private set; }

        /// <summary>Instances drawn for the last game camera.</summary>
        public int LastInstances { get; private set; }

        /// <summary>Triangles drawn by the instanced objects for the last game camera (per eye in VR).</summary>
        public int LastTriangles { get; private set; }

        /// <summary>Instances handed to the renderer in total.</summary>
        public int TotalInstances { get; private set; }

        sealed class PartDraw
        {
            public Mesh mesh;
            public int submesh;
            public RenderParams rp;
            public Matrix4x4 local;
            public bool identity;
            public int triangles;
        }

        sealed class Group
        {
            public PartDraw[][] lods;           // per level: its parts
            public float[] lodDistSq;           // squared distance below which each level is used (last = max)
            public float maxDistance;
            public Matrix4x4[][] buffers;       // per level: matrices to draw this frame
            public int[] counts;
            public Matrix4x4[] scratch = new Matrix4x4[MaxPerDraw];
            public Matrix4x4[] matrices;        // world matrices of all instances, sorted by cell
            public Vector3[] positions;         // world positions, same order
        }

        struct CellGroup
        {
            public int group;
            public int start;
            public int count;
        }

        sealed class Cell
        {
            public Vector3 center;
            public float radius;
            public readonly List<CellGroup> groups = new List<CellGroup>();
        }

        Group[] groups = new Group[0];
        Cell[] cells = new Cell[0];
        Bounds worldBounds;
        ForestWorld world;
        bool dirty = true;
        Matrix4x4 builtWith;

        void OnEnable()
        {
            world = GetComponent<ForestWorld>();
            world.DataChanged += OnDataChanged;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            dirty = true;
        }

        void OnDisable()
        {
            if (world != null) world.DataChanged -= OnDataChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            groups = new Group[0];
            cells = new Cell[0];
        }

        void OnValidate()
        {
            dirty = true;
        }

        void OnDataChanged(ForestWorld w)
        {
            dirty = true;
        }

        /// <summary>Rebuild the draw lists (after editing prefabs or materials).</summary>
        public void Rebuild()
        {
            ForestPrefabInfo.ClearCache();
            dirty = true;
        }

        // ---------------------------------------------------------------- building the draw lists

        void Build()
        {
            dirty = false;
            builtWith = transform.localToWorldMatrix;
            groups = new Group[0];
            cells = new Cell[0];
            TotalInstances = 0;
            ForestData data = world != null ? world.data : null;
            if (data == null || data.batches == null) return;

            Matrix4x4 toWorld = transform.localToWorldMatrix;
            float lodBias = QualitySettings.lodBias * lodDistanceScale * TierScale();
            var groupList = new List<Group>();
            var perCell = new Dictionary<long, List<KeyValuePair<int, int>>>();   // cell -> (group, instance)
            bool anyBounds = false;
            int layer = gameObject.layer;

            foreach (ForestInstanceBatch batch in data.batches)
            {
                if (batch == null || batch.mode != ForestRenderMode.Instanced || batch.prefab == null || batch.Count == 0) continue;
                ForestPrefabInfo info = ForestPrefabInfo.Get(batch.prefab);
                if (info == null || !info.HasMeshes || !info.instancingReady) continue;

                var g = new Group();
                int levels = info.lods.Count;
                g.lods = new PartDraw[levels][];
                g.lodDistSq = new float[levels];
                g.buffers = new Matrix4x4[levels][];
                g.counts = new int[levels];
                float size = info.lodSize > 0f ? info.lodSize : Mathf.Max(info.bounds.size.x, Mathf.Max(info.bounds.size.y, info.bounds.size.z));
                for (int l = 0; l < levels; l++)
                {
                    ForestPrefabInfo.Lod lod = info.lods[l];
                    float d = ForestPrefabInfo.SwitchDistance(lod.screenHeight, size, lodFieldOfView) * lodBias * batch.lodScale;
                    g.lodDistSq[l] = d >= float.MaxValue * 0.5f ? float.MaxValue : d * d;
                    g.lods[l] = new PartDraw[lod.parts.Count];
                    for (int p = 0; p < lod.parts.Count; p++)
                    {
                        ForestPrefabInfo.Part part = lod.parts[p];
                        var rp = new RenderParams(part.material)
                        {
                            layer = layer,
                            shadowCastingMode = batch.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            receiveShadows = true,
                            lightProbeUsage = LightProbeUsage.Off,
                            reflectionProbeUsage = ReflectionProbeUsage.Off,
                            motionVectorMode = MotionVectorGenerationMode.Camera
                        };
                        g.lods[l][p] = new PartDraw
                        {
                            mesh = part.mesh, submesh = part.submesh, rp = rp, local = part.local, identity = part.local == Matrix4x4.identity,
                            triangles = (int)(part.mesh.GetIndexCount(part.submesh) / 3)
                        };
                    }
                    g.buffers[l] = new Matrix4x4[64];
                }
                g.maxDistance = batch.drawDistance > 0f ? batch.drawDistance : float.MaxValue;

                int n = batch.Count;
                g.matrices = new Matrix4x4[n];
                g.positions = new Vector3[n];
                int gi = groupList.Count;
                groupList.Add(g);
                var raw = new Matrix4x4[n];
                for (int i = 0; i < n; i++)
                {
                    raw[i] = toWorld * batch.LocalMatrix(i);
                    Vector3 pos = raw[i].GetColumn(3);
                    long key = CellKey(pos);
                    List<KeyValuePair<int, int>> list;
                    if (!perCell.TryGetValue(key, out list)) { list = new List<KeyValuePair<int, int>>(); perCell[key] = list; }
                    list.Add(new KeyValuePair<int, int>(gi, i));
                }
                g.matrices = raw;     // reordered below
                TotalInstances += n;
            }
            groups = groupList.ToArray();

            // Sort every group's instances by cell so a cell's share of a group is one contiguous range.
            var fill = new int[groups.Length];
            var sorted = new Matrix4x4[groups.Length][];
            for (int gi = 0; gi < groups.Length; gi++)
            {
                sorted[gi] = new Matrix4x4[groups[gi].matrices.Length];
                groups[gi].positions = new Vector3[groups[gi].matrices.Length];
            }
            var cellList = new List<Cell>();
            foreach (KeyValuePair<long, List<KeyValuePair<int, int>>> kv in perCell)
            {
                var cell = new Cell();
                Bounds cb = new Bounds();
                bool first = true;
                kv.Value.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                int idx = 0;
                while (idx < kv.Value.Count)
                {
                    int gi = kv.Value[idx].Key;
                    Group g = groups[gi];
                    int start = fill[gi];
                    float reach = MaxExtent(g);
                    while (idx < kv.Value.Count && kv.Value[idx].Key == gi)
                    {
                        Matrix4x4 m = g.matrices[kv.Value[idx].Value];
                        Vector3 pos = m.GetColumn(3);
                        sorted[gi][fill[gi]] = m;
                        g.positions[fill[gi]] = pos;
                        fill[gi]++;
                        float s = Mathf.Max(m.GetColumn(0).magnitude, Mathf.Max(m.GetColumn(1).magnitude, m.GetColumn(2).magnitude));
                        var ib = new Bounds(pos, Vector3.one * reach * s * 2f);
                        if (first) { cb = ib; first = false; } else cb.Encapsulate(ib);
                        idx++;
                    }
                    cell.groups.Add(new CellGroup { group = gi, start = start, count = fill[gi] - start });
                }
                cell.center = cb.center;
                cell.radius = cb.extents.magnitude;
                cellList.Add(cell);
                if (!anyBounds) { worldBounds = cb; anyBounds = true; } else worldBounds.Encapsulate(cb);
            }
            for (int gi = 0; gi < groups.Length; gi++) groups[gi].matrices = sorted[gi];
            cells = cellList.ToArray();
        }

        static float MaxExtent(Group g)
        {
            float r = 1f;
            foreach (PartDraw[] lod in g.lods)
                foreach (PartDraw p in lod)
                {
                    Bounds b = ForestPrefabInfo.TransformBounds(p.mesh.bounds, p.local);
                    r = Mathf.Max(r, b.center.magnitude + b.extents.magnitude);
                }
            return r;
        }

        long CellKey(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x / cellSize), z = Mathf.FloorToInt(p.z / cellSize);
            return (x << 32) ^ (z & 0xFFFFFFFFL);
        }

        float TierScale()
        {
            return QuestDevice.IsHeadset && QuestDevice.Tier == PerformanceTier.Low ? lowTierDistanceScale : 1f;
        }

        // ---------------------------------------------------------------- drawing

        void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (!isActiveAndEnabled) return;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
            if (cam.cameraType == CameraType.SceneView && !drawInSceneView) return;
            if (dirty || transform.localToWorldMatrix != builtWith) Build();
            if (cells.Length == 0) return;

            Vector3 camPos = cam.transform.position;
            Vector3 fwd = cam.transform.forward;
            float fogDistance = ForestAtmosphere.VolumetricDistance > 0f ? ForestAtmosphere.VolumetricDistance : RenderSettings.fog ? FogOpaqueDistance() : maxDrawDistance;
            float limit = cam.cameraType == CameraType.SceneView ? sceneViewDrawDistance : Mathf.Min(maxDrawDistance, fogDistance) * TierScale();
            float coneCos = Mathf.Cos(Mathf.Min(89f, cam.cameraType == CameraType.SceneView ? Mathf.Max(viewConeAngle, cam.fieldOfView * cam.aspect * 0.6f + 10f) : viewConeAngle) * Mathf.Deg2Rad);
            bool cone = !cam.orthographic;

            for (int gi = 0; gi < groups.Length; gi++)
            {
                int[] counts = groups[gi].counts;
                for (int l = 0; l < counts.Length; l++) counts[l] = 0;
            }

            for (int c = 0; c < cells.Length; c++)
            {
                Cell cell = cells[c];
                Vector3 v = cell.center - camPos;
                float dist = v.magnitude;
                if (dist - cell.radius > limit) continue;
                if (cone && dist > cell.radius)
                {
                    // the cell's bounding sphere against a cone widened by the sphere's angular size
                    float cosToCell = Vector3.Dot(v, fwd) / dist;
                    float sinWiden = cell.radius / dist;
                    float cosWiden = Mathf.Sqrt(Mathf.Max(0f, 1f - sinWiden * sinWiden));
                    float coneSin = Mathf.Sqrt(Mathf.Max(0f, 1f - coneCos * coneCos));
                    float cosLimit = coneCos * cosWiden - coneSin * sinWiden;   // cos(cone + widen)
                    if (cosToCell < cosLimit) continue;
                }

                for (int k = 0; k < cell.groups.Count; k++)
                {
                    CellGroup cg = cell.groups[k];
                    Group g = groups[cg.group];
                    float maxD = Mathf.Min(limit, g.maxDistance);
                    if (dist - cell.radius > maxD) continue;
                    float maxDSq = maxD * maxD;
                    float[] lodSq = g.lodDistSq;
                    int levels = lodSq.Length;
                    for (int i = cg.start, end = cg.start + cg.count; i < end; i++)
                    {
                        Vector3 d = g.positions[i] - camPos;
                        float d2 = d.x * d.x + d.y * d.y + d.z * d.z;
                        if (d2 > maxDSq) continue;
                        int l = 0;
                        while (l < levels - 1 && d2 >= lodSq[l]) l++;
                        Matrix4x4[] buf = g.buffers[l];
                        int n = g.counts[l];
                        if (n == buf.Length)
                        {
                            System.Array.Resize(ref g.buffers[l], buf.Length * 2);
                            buf = g.buffers[l];
                        }
                        buf[n] = g.matrices[i];
                        g.counts[l] = n + 1;
                    }
                }
            }

            int draws = 0, drawn = 0, tris = 0;
            for (int gi = 0; gi < groups.Length; gi++)
            {
                Group g = groups[gi];
                for (int l = 0; l < g.lods.Length; l++)
                {
                    int n = g.counts[l];
                    if (n == 0) continue;
                    drawn += n;
                    Matrix4x4[] buf = g.buffers[l];
                    foreach (PartDraw part in g.lods[l])
                    {
                        tris += part.triangles * n;
                        RenderParams rp = part.rp;
                        rp.camera = cam;
                        rp.worldBounds = worldBounds;
                        for (int start = 0; start < n; start += MaxPerDraw)
                        {
                            int count = Mathf.Min(MaxPerDraw, n - start);
                            if (part.identity)
                            {
                                Graphics.RenderMeshInstanced(rp, part.mesh, part.submesh, buf, count, start);
                            }
                            else
                            {
                                for (int i = 0; i < count; i++) g.scratch[i] = buf[start + i] * part.local;
                                Graphics.RenderMeshInstanced(rp, part.mesh, part.submesh, g.scratch, count, 0);
                            }
                            draws++;
                        }
                    }
                }
            }
            if (cam.cameraType == CameraType.Game)
            {
                LastDrawCalls = draws;
                LastInstances = drawn;
                LastTriangles = tris;
            }
        }

        static float FogOpaqueDistance()
        {
            switch (RenderSettings.fogMode)
            {
                case FogMode.Linear: return RenderSettings.fogEndDistance;
                case FogMode.Exponential: return 4.61f / Mathf.Max(0.0001f, RenderSettings.fogDensity);
                default: return 2.146f / Mathf.Max(0.0001f, RenderSettings.fogDensity);
            }
        }
    }
}
