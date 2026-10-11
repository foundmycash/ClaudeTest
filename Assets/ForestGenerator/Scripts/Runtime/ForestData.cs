using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    [System.Serializable]
    public struct ForestStats
    {
        public int seed;
        public float seconds;
        public int chunks;
        public int vertices;
        public int triangles;
        public float trailMetres;
        public float monsterTrailMetres;
        public int clearings;
        public int markers;
        public int instances;
        public int warnings;
        public string timings;
    }

    /// <summary>All copies of one prefab, with how they are drawn. Instances are 10 floats each: position xyz, rotation xyzw, scale xyz.</summary>
    [System.Serializable]
    public class ForestInstanceBatch
    {
        public const int Stride = 10;

        public GameObject prefab;
        public string category;
        public int categoryIndex;
        public int variant;

        [Tooltip("Instanced = drawn by the Forest Instanced Renderer; GameObjects = real prefab instances under the forest.")]
        public ForestRenderMode mode = ForestRenderMode.Instanced;

        public bool colliders = true;
        public bool castShadows;
        public float drawDistance;
        public float lodScale = 1f;

        [Tooltip("Extra value per instance for special categories (streetlights: 0 working, 1 flickering, 2 dead, +10 green).")]
        [HideInInspector] public float[] extra = new float[0];

        [HideInInspector] public float[] data = new float[0];

        public int Count { get { return data != null ? data.Length / Stride : 0; } }

        public void Get(int i, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            int o = i * Stride;
            position = new Vector3(data[o], data[o + 1], data[o + 2]);
            rotation = new Quaternion(data[o + 3], data[o + 4], data[o + 5], data[o + 6]);
            scale = new Vector3(data[o + 7], data[o + 8], data[o + 9]);
        }

        /// <summary>The instance's matrix in the forest's local space.</summary>
        public Matrix4x4 LocalMatrix(int i)
        {
            Vector3 p, s;
            Quaternion r;
            Get(i, out p, out r, out s);
            return Matrix4x4.TRS(p, r, s);
        }
    }

    /// <summary>
    /// Everything a generated forest needs at runtime, in one asset next to the scene (Assets/ForestGeneratorSaved/...):
    /// the height field, the terrain chunk meshes (as sub-assets), the trail network, clearings, markers, the depth map and
    /// references to the ground maps. Stored in binary so the big arrays stay small on disk and load fast.
    /// All positions are in the forest's local space (relative to the Forest object).
    /// </summary>
    [PreferBinarySerialization]
    public class ForestData : ScriptableObject
    {
        public int seed;
        public string contentHash;
        public ForestStats stats;

        [Header("Extent")]
        public Vector2 terrainMin;
        public Vector2 terrainSize;
        public Vector2 playableSize;

        [Tooltip("Layer of the generated objects and colliders.")]
        public int layer;

        [Header("Heights")]
        public int heightWidth;
        public int heightDepth;
        public float heightSpacing;
        [HideInInspector] public float[] heights;
        [HideInInspector] public bool[] flip;

        [Header("Ground maps")]
        public Texture2D control0;
        public Texture2D control1;
        public Texture2D lightMap;

        [Header("Layout")]
        public Vector3 spawnPosition;
        public float spawnYaw;
        public List<ForestTrail> trails = new List<ForestTrail>();
        public List<ForestClearing> clearings = new List<ForestClearing>();
        public List<ForestLandmarkSite> landmarks = new List<ForestLandmarkSite>();
        public List<ForestZoneSite> zoneSites = new List<ForestZoneSite>();
        [HideInInspector] public Vector2[] playableEdge;
        [HideInInspector] public Vector2[] wallLine;

        [Header("Containment")]
        [Tooltip("Metres outside the playable edge where the hidden wall starts, and its thickness.")]
        public float wallOffset;
        public float wallThickness;

        [Tooltip("Height (forest local) of the invisible ceiling. 0 = none.")]
        public float ceilingHeight;

        [Tooltip("The forest fingerprint the monster NavMesh was baked for (it is out of date when this differs from Content Hash).")]
        public string navMeshHash;

        [Header("Depth (dread)")]
        public int dreadWidth;
        public int dreadDepth;
        public float dreadCell;
        public Vector2 dreadOrigin;
        [HideInInspector] public float[] dread;

        [Header("Markers")]
        public List<ForestMarker> markers = new List<ForestMarker>();

        [Header("Placed objects")]
        public List<ForestInstanceBatch> batches = new List<ForestInstanceBatch>();

        [Tooltip("Places along the monster's routes where something solid is in the way (should be empty).")]
        public List<Vector3> clearanceViolations = new List<Vector3>();

        [System.NonSerialized] ForestHeightField heightField;
        [System.NonSerialized] ForestGrid dreadGrid;

        /// <summary>The height field, rebuilt from the stored arrays on first use.</summary>
        public ForestHeightField HeightField
        {
            get
            {
                if (heightField == null && heights != null && heights.Length == heightWidth * heightDepth && heightWidth > 1)
                {
                    var g = new ForestGrid(heightWidth, heightDepth, heightSpacing, terrainMin);
                    System.Array.Copy(heights, g.values, heights.Length);
                    bool[] f = flip != null && flip.Length == (heightWidth - 1) * (heightDepth - 1) ? flip : new bool[(heightWidth - 1) * (heightDepth - 1)];
                    heightField = new ForestHeightField(g, f);
                }
                return heightField;
            }
        }

        public ForestGrid DreadGrid
        {
            get
            {
                if (dreadGrid == null && dread != null && dread.Length == dreadWidth * dreadDepth && dreadWidth > 1)
                {
                    dreadGrid = new ForestGrid(dreadWidth, dreadDepth, dreadCell, dreadOrigin);
                    System.Array.Copy(dread, dreadGrid.values, dread.Length);
                }
                return dreadGrid;
            }
        }

        /// <summary>Ground height at a local position (exactly the rendered terrain surface).</summary>
        public float HeightAt(Vector2 local)
        {
            ForestHeightField hf = HeightField;
            return hf != null ? hf.SampleHeight(local) : 0f;
        }

        /// <summary>0..1: how deep in the forest a local position is.</summary>
        public float DreadAt(Vector2 local)
        {
            ForestGrid g = DreadGrid;
            return g != null ? g.Sample(local) : 0f;
        }

        /// <summary>Forget the cached grids (after the arrays were replaced).</summary>
        public void InvalidateCaches()
        {
            heightField = null;
            dreadGrid = null;
        }

        /// <summary>Copies the computed parts of a build into this asset (the textures and meshes are set by the generator).</summary>
        public void Fill(ForestBuildResult r, ForestSettings s)
        {
            seed = r.seed;
            contentHash = r.contentHash;
            terrainMin = r.heights.Min;
            terrainSize = r.heights.Size;
            playableSize = s.world.playableSize;
            layer = s.terrain.layer;

            heightWidth = r.heights.grid.width;
            heightDepth = r.heights.grid.height;
            heightSpacing = r.heights.grid.cell;
            heights = (float[])r.heights.grid.values.Clone();
            flip = (bool[])r.heights.flip.Clone();

            ForestLayout l = r.layout;
            spawnPosition = new Vector3(l.spawnPosition.x, r.heights.SampleHeight(l.spawnPosition), l.spawnPosition.y);
            spawnYaw = l.spawnYaw;
            trails = new List<ForestTrail>(l.trails);
            clearings = new List<ForestClearing>(l.clearings);
            landmarks = new List<ForestLandmarkSite>(l.landmarks);
            zoneSites = new List<ForestZoneSite>(l.zoneSites);
            playableEdge = r.shape.SampleEdge(256, 0f);
            wallLine = r.shape.SampleEdge(256, s.boundary.wallInset);
            wallOffset = s.boundary.wallInset;
            wallThickness = s.boundary.wallThickness;

            dreadWidth = l.dread.width;
            dreadDepth = l.dread.height;
            dreadCell = l.dread.cell;
            dreadOrigin = l.dread.origin;
            dread = (float[])l.dread.values.Clone();

            markers = new List<ForestMarker>(r.markers);
            clearanceViolations = r.scatter != null ? new List<Vector3>(r.scatter.clearanceViolations) : new List<Vector3>();
            InvalidateCaches();
        }

        /// <summary>Groups the placed objects by prefab and decides how each group is drawn.</summary>
        public void FillBatches(ForestBuildResult r, ForestAssetLibrary library, List<string> warnings)
        {
            batches = new List<ForestInstanceBatch>();
            if (r.scatter == null || library == null) return;
            var lists = new Dictionary<long, List<float>>();
            var extras = new Dictionary<long, List<float>>();
            var order = new List<long>();
            foreach (ForestInstance i in r.scatter.instances)
            {
                long key = ((long)i.category << 32) | (uint)i.variant;
                List<float> list;
                if (!lists.TryGetValue(key, out list))
                {
                    list = new List<float>();
                    lists[key] = list;
                    extras[key] = new List<float>();
                    order.Add(key);
                }
                list.Add(i.position.x); list.Add(i.position.y); list.Add(i.position.z);
                list.Add(i.rotation.x); list.Add(i.rotation.y); list.Add(i.rotation.z); list.Add(i.rotation.w);
                list.Add(i.scale.x); list.Add(i.scale.y); list.Add(i.scale.z);
                extras[key].Add(i.extra);
            }
            foreach (long key in order)
            {
                int ci = (int)(key >> 32), vi = (int)(key & 0xFFFFFFFF);
                ForestAssetCategory cat = library.categories[ci];
                GameObject prefab = cat.variants[vi].prefab;
                ForestPrefabInfo info = ForestPrefabInfo.Get(prefab);
                ForestRenderMode mode = cat.render;
                if (mode == ForestRenderMode.Auto) mode = info.needsGameObject || !info.instancingReady || !info.HasMeshes ? ForestRenderMode.GameObjects : ForestRenderMode.Instanced;
                if (mode == ForestRenderMode.Instanced && (!info.instancingReady || !info.HasMeshes))
                {
                    warnings.Add("'" + prefab.name + "' (" + cat.name + ") cannot be GPU-instanced (" + (info.HasMeshes ? info.instancingProblem : "no meshes") + "); placed as GameObjects instead.");
                    mode = ForestRenderMode.GameObjects;
                }
                batches.Add(new ForestInstanceBatch
                {
                    prefab = prefab,
                    category = cat.name,
                    categoryIndex = ci,
                    variant = vi,
                    mode = mode,
                    colliders = cat.colliders,
                    castShadows = cat.castShadows,
                    drawDistance = cat.drawDistance,
                    lodScale = cat.lodScale,
                    data = lists[key].ToArray(),
                    extra = extras[key].ToArray()
                });
            }
        }

        public int InstanceCount
        {
            get
            {
                int n = 0;
                if (batches != null) foreach (ForestInstanceBatch b in batches) n += b.Count;
                return n;
            }
        }
    }
}
