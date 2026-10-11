using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using HavocCave.Lightmapping;

namespace HavocCave
{
    [System.Serializable]
    public struct CaveStats
    {
        public int seed;
        public int chunks;
        public int vertices;
        public int triangles;
        public int props;
        public float seconds;
        public float voxelSize;      // the voxel size that was used (picked automatically when Use Poly Count is on)
        public float tunnelMeters;   // total length of all tunnels
        public bool polyCountLimited; // Poly Count was too low to keep the tunnels intact, so the cave got more triangles than asked for

        // lightmap bake (all zero when no lightmap was baked)
        public int lightmapWidth;
        public int lightmapHeight;
        public int lightmapCharts;
        public int lightmapTexels;       // texels that cover cave surface
        public int bakedLights;
        public float lightmapTexelsPerMeter;
        public float bakeSeconds;
    }

    /// <summary>
    /// Builds a mine-style cave under this object. The spawn point (floor of the first room) is this object's position.
    ///
    ///   Generate()          rebuild with the current seed
    ///   Generate(seed)      rebuild with a specific seed (use this to give every player the same cave)
    ///   GenerateNewSeed()   pick a random seed and rebuild
    ///   Clear()             remove the cave
    ///
    /// In play mode the heavy work runs on background threads and the rest is spread across frames,
    /// so generating does not freeze the game. In edit mode it runs in one go.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Cave Generator")]
    public class CaveGenerator : MonoBehaviour
    {
        public const string RootName = "__GeneratedCave";
        const string ChunkPrefix = "CaveChunk_";
        const string PropMaterialPrefix = "CaveProp_";
        const string BakedMaterialName = PropMaterialPrefix + "CaveBaked";       // the prefix makes it a generated material: freed and saved like the prop materials
        static readonly string[] LightmapTextureProperties = { CaveLightmapUnity.ColorProperty, CaveLightmapUnity.DirectionProperty };

        [Header("Seed")]
        [Tooltip("Same seed + same settings = same cave.")]
        public int seed = 1337;

        [Tooltip("Build the cave when play starts. Turn off if you want to call Generate(seed) yourself, e.g. after syncing the seed over the network.")]
        public bool generateOnStart = true;

        [Header("Quick Settings")]
        [Tooltip("Overall size of the cave. 1 = the size set in Cave Settings below. Bigger means longer tunnels and more side tunnels and rooms (2 = about four times the tunnel length).")]
        [Range(0.25f, 3f)] public float caveSize = 1f;

        [Tooltip("How wide and tall the tunnels are, including the roughness of the walls and the dirt path. 1 = the sizes set in Cave Settings below.")]
        [Range(0.5f, 2.5f)] public float tunnelScale = 1f;

        [Tooltip("Roughly how many triangles the cave mesh gets. The voxel size is picked for you to match, so this stays about the same however big the cave is. Turn off Use Poly Count to set the voxel size yourself.")]
        [Min(1000f)] public float polyCount = 60000f;

        [Tooltip("On: Poly Count decides the detail. Off: the Voxel Size in Cave Settings is used as it is.")]
        public bool usePolyCount = true;

        [Header("Cave Settings")]
        [Tooltip("The detailed shape of the cave. Cave Size and Tunnel Scale above are multiplied on top of these.")]
        public CaveSettings settings = new CaveSettings();

        [Header("Spawn Cave")]
        [Tooltip("The room every cave starts in, designed by you. Its shape is the same for every seed. Edit it with the handles in the Scene view (select this object), or with the fields here. Presets never change it.")]
        public CaveSpawnCave spawnCave = new CaveSpawnCave();

        [Header("Material")]
        [Tooltip("The one cave material: a rock texture and a dirt texture blended by the shader.")]
        public Material caveMaterial;

        [Tooltip("Optional. A URP/Lit material that the built-in placeholder props are copied from. Leave empty to use URP/Lit directly.")]
        public Material propMaterial;

        [Tooltip("Should the cave walls cast shadows? Off is much cheaper, and the cave is lit by lamps anyway. Turn on only if you also raise Sun Strength on the material.")]
        public bool castShadows = false;

        [Header("Collision & Layers")]
        public bool generateColliders = true;
        public bool propColliders = true;

        [Tooltip("Layer for the cave and placeholder props. Make sure your locomotion layer mask includes it.")]
        public int generatedLayer = 0;

        [Header("Props")]
        public bool spawnProps = true;
        public List<CavePropEntry> props = CavePropEntry.CreateDefaults();

        [Header("Lamp Lights")]
        [Tooltip("Light the cave walls from the built-in lamps by baking the light into the mesh when the cave is generated. Cheap (no real-time lights), so it is fine on Quest, and there is no light popping.")]
        public bool bakeLampLight = true;

        [Tooltip("Also give every Nth built-in lamp or lantern a real-time point light. 0 = none. Real lights cost performance. Ignored while the lightmapper bakes the lamps (Lightmapper > Enabled, with Bake Lamp Light on): the baked light is then the only light from the lamps.")]
        [Min(0)] public int lampLightEvery = 0;
        [Min(0.1f)] public float lampLightRange = 13f;
        [Min(0f)] public float lampLightIntensity = 2.5f;
        public Color lampLightColor = new Color(1f, 0.93f, 0.8f);

        [Header("Lightmapper")]
        [Tooltip("Bakes the lamps' light (soft shadows, bounced light, ambient occlusion) into a lightmap texture every time a cave is generated. " +
                 "When this is on it replaces the vertex light above for the built-in lamps. Props you put a Cave Baked Light on are baked too.")]
        public CaveBakeSettings lightmap = new CaveBakeSettings();

        [Header("Saving")]
        [Tooltip("Keep the cave you generate in the editor as part of the scene, so it is still there when you reopen the scene or Unity. Its meshes are saved as files in Assets/CaveGeneratorSaved, and the scene is saved every time you press Generate or Clear. Off: an edit-mode cave is only a preview that disappears when the scene closes.")]
        public bool saveWithScene = true;

        [SerializeField, HideInInspector] string saveId = "";

        [Header("Runtime")]
        [Tooltip("In play mode, how many milliseconds per frame the main thread may spend building objects.")]
        [Range(1, 16)] public int frameBudgetMs = 6;

        [Header("Player")]
        [Tooltip("Optional. Drag your player rig here and it is moved to the spawn point, facing down the first tunnel, every time a cave finishes generating.")]
        public Transform movePlayerToSpawn;

        [Tooltip("How far above the spawn floor the player rig's pivot is placed.")]
        public float spawnHeightOffset = 0.3f;

        [Header("Events")]
        public UnityEvent onGenerated;

        /// <summary>Raised when a cave finishes building.</summary>
        public event System.Action<CaveGenerator> Generated;

        public bool IsGenerating { get; private set; }

        /// <summary>True while the lightmap is being baked (part of generating). For a loading bar.</summary>
        public bool IsBaking { get; private set; }

        /// <summary>0..1 progress of the lightmap bake, 0 when none is running.</summary>
        public float BakeProgress { get { CaveLightBaker b = activeBaker; return IsBaking && b != null ? b.Progress : 0f; } }

        /// <summary>What the lightmap bake is doing right now ("Unwrapping", "Baking light", ...).</summary>
        public string BakeStage { get { CaveLightBaker b = activeBaker; return IsBaking && b != null ? b.Stage : ""; } }

        volatile CaveLightBaker activeBaker;
        CancellationTokenSource bakeCts;

        /// <summary>True while a generated cave exists under this object.</summary>
        public bool HasGeneratedCave { get { return transform.Find(RootName) != null; } }
        public CaveStats LastStats { get; private set; }

        /// <summary>Where the player should start: the floor at the first room.</summary>
        public Vector3 SpawnPoint { get { return transform.position; } }

        /// <summary>Faces the first exit of the spawn cave (down the first tunnel).</summary>
        public Quaternion SpawnRotation
        {
            get { return Quaternion.Euler(0f, transform.eulerAngles.y + (lastLayout != null ? lastLayout.spawnYaw : 0f), 0f); }
        }

        /// <summary>The settings the current cave was really built with: Cave Settings with Cave Size, Tunnel Scale and Poly Count applied.</summary>
        public CaveSettings EffectiveSettings { get; private set; }

        /// <summary>Tunnel centre lines of the current cave (null when nothing is generated). Handy for placing enemies, pickups, etc.</summary>
        public CaveLayout Layout { get { return lastLayout; } }

        /// <summary>Converts a point from cave layout space (CaveLayout / CavePath positions) to world space.</summary>
        public Vector3 LayoutToWorld(Vector3 layoutPoint)
        {
            return transform.TransformPoint(layoutPoint + rootOffset);
        }

        CancellationTokenSource cts;
        Coroutine routine;
        CaveLayout lastLayout;
        Vector3 rootOffset;

        void Start()
        {
            if (generateOnStart) Generate();
        }

        void OnDisable()
        {
            CancelWork();
        }

        void OnDestroy()
        {
            CancelWork();
        }

        // ---------------------------------------------------------------- public API

        [ContextMenu("Generate")]
        public void Generate()
        {
            ClearCave();     // the saved files are kept: they are overwritten in place once the new cave is built
            cts = new CancellationTokenSource();
            IEnumerator work = GenerateRoutine(seed, cts.Token);

            if (Application.isPlaying && isActiveAndEnabled)
            {
                routine = StartCoroutine(work);
            }
            else
            {
                while (work.MoveNext()) Thread.Sleep(1);   // edit mode: run to the end, waiting on the worker threads
            }
        }

        public void Generate(int newSeed)
        {
            seed = newSeed;
            Generate();
        }

        [ContextMenu("Generate New Seed")]
        public void GenerateNewSeed()
        {
            Generate(NewRandomSeed());
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            ClearCave();
#if UNITY_EDITOR
            if (!Application.isPlaying) ForgetSavedCave();
#endif
        }

        void ClearCave()
        {
            CancelWork();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == RootName) DestroyCave(child.gameObject);
            }
            LastStats = default(CaveStats);
            lastLayout = null;
        }

        /// <summary>Back to the default (Experimental) cave: Quick Settings, Cave Settings and lamp lights. Keeps your seed, materials and prop list.</summary>
        [ContextMenu("Reset Cave Settings To Defaults")]
        public void ResetCaveSettings()
        {
            ApplyPreset(CavePreset.Experimental);
        }

        /// <summary>Loads one of the presets (Smooth, Experimental, Wild) into the Quick Settings and Cave Settings. Keeps your seed, materials and prop list.</summary>
        public void ApplyPreset(CavePreset preset)
        {
            caveSize = 1f;
            tunnelScale = 1f;
            polyCount = CaveSettings.PolyCountFor(preset);
            usePolyCount = true;
            settings = CaveSettings.FromPreset(preset);
            castShadows = false;
            bakeLampLight = true;
            lampLightEvery = 0;
            lampLightRange = 13f;
            lampLightIntensity = 2.5f;
            lampLightColor = new Color(1f, 0.93f, 0.8f);
        }

        /// <summary>Back to the default spawn cave: one round room with an exit ahead and a small flight of steps.</summary>
        [ContextMenu("Reset Spawn Cave To Defaults")]
        public void ResetSpawnCave()
        {
            spawnCave = new CaveSpawnCave();
        }

        [ContextMenu("Reset Props To Defaults")]
        public void ResetPropsToDefaults()
        {
            props = CavePropEntry.CreateDefaults();
        }

        public static int NewRandomSeed()
        {
            return new System.Random(System.Guid.NewGuid().GetHashCode()).Next(1, int.MaxValue);
        }

        // ---------------------------------------------------------------- clean up

        void CancelWork()
        {
            if (cts != null)
            {
                cts.Cancel();
                cts = null;
            }
            if (routine != null)
            {
                StopCoroutine(routine);
                routine = null;
            }
            if (bakeCts != null)
            {
                bakeCts.Cancel();
                bakeCts = null;
            }
            IsGenerating = false;
            IsBaking = false;
            activeBaker = null;
        }

        static bool IsAsset(Object o)
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.Contains(o);
#else
            return false;
#endif
        }

        static void DestroyCave(GameObject root)
        {
            // Meshes and generated materials aren't owned by a GameObject, so free them by hand.
            var doomed = new HashSet<Object>();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh != null && mesh.name.StartsWith(ChunkPrefix) && !IsAsset(mesh)) doomed.Add(mesh);   // saved meshes are files: leave them alone
            }
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || !m.name.StartsWith(PropMaterialPrefix) || IsAsset(m)) continue;
                    doomed.Add(m);
                    // the lightmap textures belong to the cave's own material copy
                    foreach (string prop in LightmapTextureProperties)
                    {
                        Texture t = m.HasProperty(prop) ? m.GetTexture(prop) : null;
                        if (t != null && !IsAsset(t)) doomed.Add(t);
                    }
                }
            }

            if (Application.isPlaying)
            {
                // Destroy() waits for the end of the frame; get the old cave out of the way right now.
                root.SetActive(false);
                root.transform.SetParent(null);
            }
            CaveUtil.Kill(root);
            foreach (Object o in doomed) CaveUtil.Kill(o);
        }

        // ---------------------------------------------------------------- generation

        /// <summary>Cave Settings with the Quick Settings multiplied in. Poly Count is not applied here; the voxel size is chosen once the layout exists.</summary>
        public CaveSettings BuildEffectiveSettings()
        {
            CaveSettings c = settings.Clone();
            float size = Mathf.Max(0.1f, caveSize);
            float t = Mathf.Max(0.1f, tunnelScale);

            // Cave size: how far it sprawls.
            c.mainTunnelLength = Mathf.Max(20f, c.mainTunnelLength * size);
            c.branchLength *= size;
            c.branchCount = Mathf.RoundToInt(c.branchCount * size);
            c.loops = Mathf.RoundToInt(c.loops * size);
            c.shafts = Mathf.RoundToInt(c.shafts * size);

            // Tunnel scale: how big each tunnel is. Everything measured in tunnel widths scales with it.
            c.tunnelWidth *= t;
            c.tunnelHeight *= t;
            c.tunnelSpacing *= t;
            c.roomRadius *= t;
            c.roomHeight *= t;
            c.rockRadius *= t;
            c.roughness *= t;
            c.roughnessScale *= t;
            c.levelRange *= t;
            c.strata *= t;
            c.pathWidth *= t;
            c.pathFade *= t;
            return c;
        }

        Material ResolveMaterial()
        {
            if (caveMaterial != null) return caveMaterial;

            Debug.LogWarning("CaveGenerator: no Cave Material assigned. Using a plain fallback; assign Assets/CaveGenerator/Materials/CaveBlend.mat.", this);
            Shader sh = Shader.Find("Havoc/Cave Blend");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            return new Material(sh) { name = PropMaterialPrefix + "fallback" };
        }

        IEnumerator GenerateRoutine(int useSeed, CancellationToken token)
        {
            IsGenerating = true;
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var budget = System.Diagnostics.Stopwatch.StartNew();
                int budgetMs = frameBudgetMs;
                System.Func<bool> overBudget = () => Application.isPlaying && budget.ElapsedMilliseconds >= budgetMs;

                CaveSettings cfg = BuildEffectiveSettings();
                // An edit-mode cave is kept in the scene when Save With Scene is on; otherwise it is a throwaway preview.
                HideFlags flags = (Application.isPlaying || saveWithScene) ? HideFlags.None : HideFlags.DontSaveInEditor;

                // 1. The abstract shape. Cheap, so it runs right here.
                CaveLayout layout = CaveLayout.Build(cfg, useSeed, spawnCave);
                bool polyLimited = false;
                if (usePolyCount) cfg.voxelSize = layout.VoxelSizeForTriangles(cfg, polyCount, out polyLimited);
                var field = new CaveField(layout, cfg, useSeed);
                lastLayout = layout;
                EffectiveSettings = cfg;

                // 2. Work out which chunks of space actually contain wall.
                float vs = cfg.voxelSize;
                Vector3 grid;
                var offsets = new List<Vector3Int>();        // each chunk's offset from the grid corner, in voxels
                var regions = new List<CaveField.Region>();
                CaveChunking.Plan(layout, field, vs, out grid, offsets, regions);

                // 3. The root object. The first room's floor is moved to this object's position.
                var rootGO = new GameObject(RootName);
                rootGO.hideFlags = flags;
                rootGO.layer = generatedLayer;
                rootGO.transform.SetParent(transform, false);

                Vector3 floor;
                if (!field.RayMarch(field.All(), layout.spawn, Vector3.down, 8f, out floor))
                    floor = layout.spawn + Vector3.down * 1.4f;
                rootOffset = -floor;
                rootGO.transform.localPosition = rootOffset;

                // Will the lightmapper run? Then the lamps are lit by the bake alone: they get no real-time lights.
                bool useLightmap = lightmap != null && lightmap.enabled && (!Application.isPlaying || lightmap.bakeAtRuntime);
                bool lampsBaked = useLightmap && bakeLampLight;

                // 4. Props first, so we know where the lamps are before the walls are lit. They get their own random
                //    stream so editing the prop list never changes the tunnels.
                int propCount = 0;
                CaveLamp[] lampArray = new CaveLamp[0];
                Transform propsRoot = null;
                if (spawnProps && props != null && props.Count > 0)
                {
                    var propsGO = new GameObject("Props");
                    propsGO.hideFlags = flags;
                    propsGO.layer = generatedLayer;
                    propsGO.transform.SetParent(rootGO.transform, false);
                    propsRoot = propsGO.transform;

                    var rng = new System.Random(useSeed ^ 0x5DEECE6);
                    var spawner = new CavePropSpawner(this, layout, field, propsGO.transform, rng, flags, overBudget, !lampsBaked);
                    IEnumerator spawning = spawner.Run(props);
                    while (spawning.MoveNext())
                    {
                        yield return null;
                        budget.Restart();
                        if (token.IsCancellationRequested) yield break;
                    }
                    propCount = spawner.Count;
                    if (bakeLampLight) lampArray = spawner.Lamps.ToArray();
                }

                // When the lightmapper is going to run it bakes the lamps itself (properly: soft shadows, bounces), so the
                // cheaper light-in-the-vertices bake is skipped.
                CaveLamp[] bakeLamps = lampArray;
                if (useLightmap) lampArray = new CaveLamp[0];

                // Each lamp gets a small slice of the cave to test line of sight against.
                var lampRegions = new CaveField.Region[lampArray.Length];
                for (int q = 0; q < lampArray.Length; q++)
                    lampRegions[q] = field.Query(lampArray[q].position - Vector3.one * lampLightRange, lampArray[q].position + Vector3.one * lampLightRange);
                float bakeRange = lampLightRange;
                float bakeIntensity = lampLightIntensity;

                // 5. Mesh every chunk on worker threads, lighting the walls from the lamps.
                int count = offsets.Count;
                var meshes = new CaveChunkMesh[count];
                int threads = Mathf.Clamp(SystemInfo.processorCount - 1, 1, 8);
                float eps = vs * 0.4f;
                Task worker = Task.Run(() =>
                {
                    var local = new ThreadLocal<CaveMesher>(() => new CaveMesher());
                    try
                    {
                        Parallel.For(0, count,
                            new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token },
                            i => { meshes[i] = local.Value.Build(field, regions[i], grid, offsets[i].x, offsets[i].y, offsets[i].z, vs, eps,
                                                                 lampArray, lampRegions, bakeRange, bakeIntensity); });
                    }
                    finally
                    {
                        local.Dispose();
                    }
                }, token);

                while (!worker.IsCompleted) yield return null;
                if (worker.IsCanceled || token.IsCancellationRequested) yield break;
                if (worker.IsFaulted)
                {
                    Debug.LogError("CaveGenerator: meshing failed: " + worker.Exception.GetBaseException(), this);
                    yield break;
                }
                budget.Restart();

                // 5b. The lightmap: unwrap the chunk meshes, trace the light for every texel, all on worker threads.
                CaveBakeResult bakeResult = null;
                List<CaveLightmapUnity.PropGroup> propGroups = null;
                if (useLightmap && count > 0)
                {
                    var chunkList = new List<CaveChunkMesh>();
                    for (int i = 0; i < count; i++) if (meshes[i] != null) chunkList.Add(meshes[i]);

                    if (chunkList.Count > 0)
                    {
                        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                        List<CaveLightmapUnity.PropGroup> groups = null;
                        CaveBakeOccluder[] occluders = new CaveBakeOccluder[0];
                        CaveBakeProbe[] probes = null;
                        if (lightmap.propsCastShadows || lightmap.lightProps)
                        {
                            occluders = CaveLightmapUnity.CollectOccluders(propsRoot, rootGO.transform, linear, lightmap.propsCastShadows, out groups);
                            if (lightmap.lightProps && groups.Count > 0) probes = CaveLightmapUnity.CollectProbes(groups, rootGO.transform);
                        }
                        propGroups = groups;
                        CaveBakeLight[] bakeLights = CaveLightmapUnity.CollectLights(bakeLamps, lampLightRange, lampLightIntensity * lightmap.lampBrightness, lampLightColor,
                                                                                    lightmap.lampRadius, lightmap.lampFalloff == CaveLampFalloff.Soft, lightmap.ceilingLampsShineDown,
                                                                                    rootGO.transform, groups, linear);
                        LmParams bakeParams = lightmap.Resolve(linear, Application.isMobilePlatform);
                        bakeParams.seed = useSeed;
                        bool hasAmbient = bakeParams.ambient.r > 0f || bakeParams.ambient.g > 0f || bakeParams.ambient.b > 0f;

                        if (bakeLights.Length > 0 || hasAmbient)
                        {
                            var baker = new CaveLightBaker();
                            activeBaker = baker;
                            IsBaking = true;
                            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                            bakeCts = linked;
                            CancellationToken bakeToken = linked.Token;
                            CaveChunkMesh[] chunkArray = chunkList.ToArray();
                            int bakeThreads = Mathf.Clamp(SystemInfo.processorCount - 1, 1, 16);
                            Task<CaveBakeResult> bakeTask = Task.Run(() => baker.Run(chunkArray, bakeLights, occluders, probes, bakeParams, bakeThreads, bakeToken), bakeToken);

#if UNITY_EDITOR
                            var barTimer = System.Diagnostics.Stopwatch.StartNew();
#endif
                            while (!bakeTask.IsCompleted)
                            {
#if UNITY_EDITOR
                                // In the editor the bake runs inside one button press, so show how far it is and let it be cancelled.
                                // (The edit mode driver spins this loop about a thousand times a second; the bar only needs a few.)
                                if (!Application.isPlaying && barTimer.ElapsedMilliseconds > 80)
                                {
                                    barTimer.Restart();
                                    if (UnityEditor.EditorUtility.DisplayCancelableProgressBar("Cave Generator: baking light", baker.Stage, baker.Progress))
                                        linked.Cancel();
                                }
#endif
                                yield return null;
                            }
                            IsBaking = false;
                            activeBaker = null;
                            if (token.IsCancellationRequested) yield break;
                            if (bakeTask.IsFaulted)
                                Debug.LogError("CaveGenerator: baking the lightmap failed, the cave has no lamp light: " + bakeTask.Exception.GetBaseException(), this);
                            else if (bakeTask.IsCanceled)
                                Debug.LogWarning("CaveGenerator: the lightmap bake was cancelled, the cave has no lamp light.", this);
                            else
                                bakeResult = bakeTask.Result;
                            bakeCts = null;
                            linked.Dispose();
                            budget.Restart();
                        }
                    }
                }

                // 6. The chunk objects.
                Material mat = ResolveMaterial();
                Material chunkMat = mat;
                if (bakeResult != null)
                {
                    Texture2D lmColor, lmDirection;
                    CaveLightmapUnity.CreateTextures(bakeResult, !Application.isPlaying, out lmColor, out lmDirection);
                    chunkMat = CaveLightmapUnity.CreateMaterial(mat, BakedMaterialName, lmColor, lmDirection);
                    if (!chunkMat.HasProperty(CaveLightmapUnity.ModeProperty))
                        Debug.LogWarning("CaveGenerator: the Cave Material's shader has no lightmap support (use Havoc/Cave Blend), so the baked light is not shown.", this);

                    // The props take their light from the same bake, scaled like the walls are.
                    if (bakeResult.probes != null)
                    {
                        float strength = mat.HasProperty("_CaveLightmapStrength") ? mat.GetFloat("_CaveLightmapStrength") : 1f;
                        CaveLightmapUnity.ApplyPropProbes(propGroups, bakeResult.probes, rootGO.transform, strength);
                    }
                }
                var chunksGO = new GameObject("Chunks");
                chunksGO.hideFlags = flags;
                chunksGO.layer = generatedLayer;
                chunksGO.transform.SetParent(rootGO.transform, false);

                int chunkCount = 0, vertCount = 0, triCount = 0;
                for (int i = 0; i < count; i++)
                {
                    CaveChunkMesh data = meshes[i];
                    meshes[i] = null;
                    if (data == null) continue;

                    var mesh = new Mesh { name = ChunkPrefix + chunkCount };
                    if (data.vertices.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
                    mesh.vertices = data.vertices;
                    mesh.normals = data.normals;
                    mesh.colors32 = data.colors;
                    if (data.uv2 != null && data.uv2.Length == data.vertices.Length) mesh.uv2 = data.uv2;
                    mesh.triangles = data.triangles;
                    mesh.RecalculateBounds();

                    var go = new GameObject(ChunkPrefix + chunkCount);
                    go.hideFlags = flags;
                    go.layer = generatedLayer;
                    go.transform.SetParent(chunksGO.transform, false);
                    go.transform.localPosition = data.origin;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = chunkMat;
                    renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    if (generateColliders) go.AddComponent<MeshCollider>().sharedMesh = mesh;

                    chunkCount++;
                    vertCount += data.vertices.Length;
                    triCount += data.triangles.Length / 3;

                    if (overBudget())
                    {
                        yield return null;
                        budget.Restart();
                        if (token.IsCancellationRequested) yield break;
                    }
                }

                timer.Stop();
                LastStats = new CaveStats
                {
                    seed = useSeed,
                    chunks = chunkCount,
                    vertices = vertCount,
                    triangles = triCount,
                    props = propCount,
                    seconds = (float)timer.Elapsed.TotalSeconds,
                    voxelSize = vs,
                    tunnelMeters = layout.totalLength,
                    polyCountLimited = polyLimited,
                    lightmapWidth = bakeResult != null ? bakeResult.width : 0,
                    lightmapHeight = bakeResult != null ? bakeResult.height : 0,
                    lightmapCharts = bakeResult != null ? bakeResult.charts : 0,
                    lightmapTexels = bakeResult != null ? bakeResult.texels : 0,
                    bakedLights = bakeResult != null ? bakeResult.lights : 0,
                    lightmapTexelsPerMeter = bakeResult != null ? bakeResult.texelsPerMeter : 0f,
                    bakeSeconds = bakeResult != null ? bakeResult.seconds : 0f
                };

#if UNITY_EDITOR
                if (!Application.isPlaying && saveWithScene) PersistToScene(rootGO);
#endif

                MovePlayerToSpawn();
                if (Generated != null) Generated(this);
                if (onGenerated != null) onGenerated.Invoke();
            }
            finally
            {
                IsGenerating = false;
            }
        }

        void MovePlayerToSpawn()
        {
            if (movePlayerToSpawn == null) return;

            Vector3 pos = SpawnPoint + Vector3.up * spawnHeightOffset;
            movePlayerToSpawn.SetPositionAndRotation(pos, SpawnRotation);

            Rigidbody body = movePlayerToSpawn.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
        }

        // ---------------------------------------------------------------- editor helpers

        void OnDrawGizmosSelected()
        {
            if (lastLayout == null) return;

            Gizmos.color = Color.cyan;
            for (int p = 0; p < lastLayout.paths.Count; p++)
            {
                CavePath path = lastLayout.paths[p];
                for (int i = 1; i < path.pos.Count; i++)
                    Gizmos.DrawLine(transform.TransformPoint(path.pos[i - 1] + rootOffset), transform.TransformPoint(path.pos[i] + rootOffset));
            }

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.position, 0.4f);
            Gizmos.DrawRay(transform.position, transform.forward * 1.5f);
        }

        /// <summary>How many chunks of cave are in the scene right now (a saved cave counts after the scene is reopened).</summary>
        public int SavedChunkCount
        {
            get
            {
                Transform root = transform.Find(RootName);
                Transform chunks = root != null ? root.Find("Chunks") : null;
                return chunks != null ? chunks.childCount : 0;
            }
        }

#if UNITY_EDITOR
        void Reset()
        {
            caveMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/CaveGenerator/Materials/CaveBlend.mat");
            propMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/CaveGenerator/Materials/CavePropBase.mat");
        }

        // ---------------------------------------------------------------- saving the cave with the scene (editor only)

        const string SavedRoot = "Assets/CaveGeneratorSaved";

        /// <summary>Another Cave Generator (a copy of this one) uses the same save id, so it would share our files.</summary>
        bool SaveIdShared()
        {
            if (string.IsNullOrEmpty(saveId)) return false;
            foreach (CaveGenerator other in FindObjectsOfType<CaveGenerator>())
                if (other != this && other.saveId == saveId) return true;
            return false;
        }

        string SavedFolderPath()
        {
            string sceneName = gameObject.scene.name;
            if (string.IsNullOrEmpty(sceneName)) sceneName = "Scene";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) sceneName = sceneName.Replace(c, '_');
            return SavedRoot + "/" + sceneName + "_" + saveId;
        }

        static string SafeName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static void EnsureFolder(string path)
        {
            if (UnityEditor.AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            UnityEditor.AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }

        /// <summary>
        /// Writes obj to path. If a file is already there, its contents are replaced in place, so the scene's references
        /// to it stay valid. A source object that was copied into an existing file is added to destroyLater.
        /// </summary>
        static Object PersistAsset(Object obj, string path, List<Object> destroyLater)
        {
            Object existing = UnityEditor.AssetDatabase.LoadAssetAtPath(path, obj.GetType());
            if (existing != null && existing != obj)
            {
                UnityEditor.EditorUtility.CopySerialized(obj, existing);
                UnityEditor.EditorUtility.SetDirty(existing);
                destroyLater.Add(obj);
                return existing;
            }
            UnityEditor.AssetDatabase.CreateAsset(obj, path);
            return obj;
        }

        /// <summary>
        /// Makes the cave that was just built part of the scene for good: the chunk meshes and the generated prop materials
        /// become asset files, the objects point at those files, and the scene is saved. Never touches anything outside
        /// this generator's own folder.
        /// </summary>
        void PersistToScene(GameObject root)
        {
            try
            {
                if (string.IsNullOrEmpty(saveId) || SaveIdShared())
                {
                    saveId = System.Guid.NewGuid().ToString("N").Substring(0, 8);
                    UnityEditor.EditorUtility.SetDirty(this);
                }
                string folder = SavedFolderPath();
                EnsureFolder(folder);

                var keep = new HashSet<string>();
                var meshes = new Dictionary<Mesh, Mesh>();
                var materials = new Dictionary<Material, Material>();
                var destroyLater = new List<Object>();
                int unnamed = 0;

                // The lightmap textures first: a material that points at a texture which is not a file would lose the reference
                // when it is saved. The textures are only used by the cave's own material copy, so each one is replaced as a whole
                // (the scene keeps pointing at the material file, which keeps pointing at the texture file).
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null || UnityEditor.AssetDatabase.Contains(m)) continue;
                        foreach (string prop in LightmapTextureProperties)
                        {
                            Texture t = m.HasProperty(prop) ? m.GetTexture(prop) : null;
                            if (t == null || UnityEditor.AssetDatabase.Contains(t)) continue;
                            string texPath = folder + "/" + SafeName(string.IsNullOrEmpty(t.name) ? prop : t.name) + ".asset";
                            keep.Add(texPath);
                            if (UnityEditor.AssetDatabase.LoadAssetAtPath<Texture>(texPath) != null) UnityEditor.AssetDatabase.DeleteAsset(texPath);
                            UnityEditor.AssetDatabase.CreateAsset(t, texPath);
                        }
                    }
                }

                UnityEditor.AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        Mesh mesh = mf.sharedMesh;
                        if (mesh == null || UnityEditor.AssetDatabase.Contains(mesh)) continue;     // built-in and project meshes are already files
                        Mesh saved;
                        if (!meshes.TryGetValue(mesh, out saved))
                        {
                            string path = folder + "/" + SafeName(string.IsNullOrEmpty(mesh.name) ? "Mesh" + (unnamed++) : mesh.name) + ".asset";
                            keep.Add(path);
                            saved = (Mesh)PersistAsset(mesh, path, destroyLater);
                            meshes[mesh] = saved;
                        }
                        mf.sharedMesh = saved;
                        MeshCollider col = mf.GetComponent<MeshCollider>();
                        if (col != null) col.sharedMesh = saved;
                    }

                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] list = r.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < list.Length; i++)
                        {
                            Material m = list[i];
                            if (m == null || UnityEditor.AssetDatabase.Contains(m)) continue;         // the cave material and prop materials from the project are files already
                            Material saved;
                            if (!materials.TryGetValue(m, out saved))
                            {
                                string path = folder + "/" + SafeName(string.IsNullOrEmpty(m.name) ? "Material" + (unnamed++) : m.name) + ".mat";
                                keep.Add(path);
                                saved = (Material)PersistAsset(m, path, destroyLater);
                                materials[m] = saved;
                            }
                            list[i] = saved;
                            changed = true;
                        }
                        if (changed) r.sharedMaterials = list;
                    }
                }
                finally
                {
                    UnityEditor.AssetDatabase.StopAssetEditing();
                }
                foreach (Object o in destroyLater) DestroyImmediate(o);

                // Files from an earlier, bigger cave that nothing uses any more.
                foreach (string guid in UnityEditor.AssetDatabase.FindAssets("", new[] { folder }))
                {
                    string p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                    if (!keep.Contains(p) && !UnityEditor.AssetDatabase.IsValidFolder(p)) UnityEditor.AssetDatabase.DeleteAsset(p);
                }
                UnityEditor.AssetDatabase.SaveAssets();

                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
                bool sceneSaved = SaveSceneNow();
                Debug.Log("[Cave Generator] Cave saved with the scene (" + meshes.Count + " meshes, " + materials.Count + " materials in " + folder + ")" +
                          (sceneSaved ? "; scene saved." : ". Save the scene (Ctrl+S) to keep it."), this);
            }
            catch (System.Exception e)
            {
                Debug.LogError("CaveGenerator: the cave was built but could not be saved with the scene: " + e, this);
            }
        }

        bool SaveSceneNow()
        {
            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path)) return false;   // an untitled scene asks for a name when you save it yourself
            return UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        }

        /// <summary>Clear in the editor: the cave's files are deleted too (only this generator's own folder) and the scene is saved without it.</summary>
        void ForgetSavedCave()
        {
            if (!saveWithScene || string.IsNullOrEmpty(saveId)) return;
            try
            {
                if (!SaveIdShared())
                {
                    string folder = SavedFolderPath();
                    if (UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.DeleteAsset(folder);
                }
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
                SaveSceneNow();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("CaveGenerator: could not remove the saved cave files: " + e, this);
            }
        }
#endif
    }
}
