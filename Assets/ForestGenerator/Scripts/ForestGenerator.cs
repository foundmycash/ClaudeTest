using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace HavocForest
{
    /// <summary>Which debug drawings to show in the Scene view while the Forest object is selected.</summary>
    [System.Serializable]
    public class ForestDebugGizmos
    {
        public bool playableEdge = true;
        public bool hiddenWall = false;
        public bool trails = true;
        [Tooltip("The strip along every monster route that is kept free of blocking objects, and every clearing's turning circle.")]
        public bool monsterClearance = false;
        public bool clearings = true;
        public bool zones = false;
        public bool markers = true;
        [Tooltip("Also draw when the Forest object is not selected.")]
        public bool alwaysShow = false;
    }

    /// <summary>
    /// Builds the forest under this object: terrain, trails, clearings and the ground maps (and, as the system grows, the
    /// vegetation, props, streetlights and boundary). Configure it with a Forest Settings asset; the result is saved next to
    /// the scene (Assets/ForestGeneratorSaved) so it is there in builds without generating at runtime.
    ///
    ///   Generate()          rebuild with the current seed
    ///   Generate(seed)      rebuild with a specific seed
    ///   GenerateNewSeed()   pick a random seed and rebuild
    ///   Clear()             remove the forest (and its saved files)
    ///
    /// Generating again replaces everything it made before: nothing is duplicated or left behind.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ForestWorld))]
    [RequireComponent(typeof(ForestInstancedRenderer))]
    [AddComponentMenu("Havoc/Forest Generator")]
    public class ForestGenerator : MonoBehaviour
    {
        public const string RootName = "__GeneratedForest";
        const string GeneratedPrefix = "Forest_";

        [Header("Seed")]
        [Tooltip("Same seed + same settings = same forest.")]
        public int seed = 7041;

        [Tooltip("Generate when play starts. Normally off: the forest is generated in the editor and saved with the scene. Turn on to build a different forest every session (call Generate(seed) with a synced seed for multiplayer).")]
        public bool generateOnStart = false;

        [Header("Configuration")]
        [Tooltip("The layout, terrain, trails, zones, boundary and monster settings.")]
        public ForestSettings settings;

        [Tooltip("The 8 ground surfaces and the shared ground material.")]
        public ForestGroundLayers groundLayers;

        [Tooltip("The models the forest is built from: trees, undergrowth, rocks, logs, landmarks, streetlights... Replace the placeholders with your own prefabs here.")]
        public ForestAssetLibrary assets;

        [Header("Saving")]
        [Tooltip("Keep the forest generated in the editor: its meshes and maps are saved as files in Assets/ForestGeneratorSaved and the scene references them. Off: an edit-mode forest is only a preview that is gone when the scene is closed.")]
        public bool saveWithScene = true;

#pragma warning disable 0414   // used by the editor-only saving code; player builds see them as unused
        [SerializeField, HideInInspector] string saveId = "";
        [SerializeField, HideInInspector] string savedFolder = "";
#pragma warning restore 0414

        /// <summary>Which one-time scene upgrades the editor has applied to this forest (1 = flashlight added). Editor use.</summary>
        [HideInInspector] public int setupVersion;

        [Header("Player")]
        [Tooltip("Optional. Your player rig: it is moved to the trailhead, facing down the first trail, every time the forest is generated.")]
        public Transform movePlayerToSpawn;

        [Tooltip("How far above the ground the rig's pivot is placed.")]
        public float spawnHeightOffset = 0.05f;

        [Header("Debug")]
        public ForestDebugGizmos gizmos = new ForestDebugGizmos();

        [Header("Events")]
        public UnityEvent onGenerated;

        /// <summary>Raised when a forest finishes generating.</summary>
        public event System.Action<ForestGenerator> Generated;

        /// <summary>Problems found while generating the last forest (empty when all went well).</summary>
        public List<string> LastWarnings { get; private set; }

        public ForestWorld World { get { return GetComponent<ForestWorld>(); } }

        public bool HasGeneratedForest { get { return transform.Find(RootName) != null; } }

        void Start()
        {
            if (Application.isPlaying && generateOnStart) Generate();
        }

        // ---------------------------------------------------------------- public API

        [ContextMenu("Generate")]
        public void Generate()
        {
            if (settings == null)
            {
                Debug.LogError("ForestGenerator: assign a Forest Settings asset first (Tools > Havoc Forest > Create Default Assets makes one).", this);
                return;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            if (GetComponent<ForestInstancedRenderer>() == null) gameObject.AddComponent<ForestInstancedRenderer>();
            if (GetComponent<ForestLampManager>() == null) gameObject.AddComponent<ForestLampManager>();
            if (GetComponent<ForestBoundaryGuard>() == null) gameObject.AddComponent<ForestBoundaryGuard>();
            ForestPrefabInfo.ClearCache();
            ForestBuildResult result = ForestBuilder.Build(settings, assets, seed);
            LastWarnings = result.warnings;

            ClearGenerated();
            bool persist = false;
#if UNITY_EDITOR
            persist = !Application.isPlaying && saveWithScene;
            if (persist) EnsureSaveId();
#endif
            HideFlags flags = (Application.isPlaying || persist) ? HideFlags.None : HideFlags.DontSaveInEditor;

            var rootGO = new GameObject(RootName);
            rootGO.hideFlags = flags;
            rootGO.layer = settings.terrain.layer;
            rootGO.transform.SetParent(transform, false);

            // The maps are saved before the forest data is even loaded: saving them imports files, and after an import the
            // editor may unload assets nothing references yet. A ForestData filled before that was thrown away and read
            // back empty from disk (a forest with ground but no trees, no spawn point).
            Texture2D c0 = MakeMap(result.ground.control0, result.ground.blendResolution, GeneratedPrefix + "Control0");
            Texture2D c1 = MakeMap(result.ground.control1, result.ground.blendResolution, GeneratedPrefix + "Control1");
            Texture2D lm = MakeMap(result.ground.light, result.ground.lightResolution, GeneratedPrefix + "LightMap");
            var meshes = new List<Mesh>(result.chunks.Count);
            foreach (ForestChunkMesh chunk in result.chunks) meshes.Add(MakeMesh(chunk));
#if UNITY_EDITOR
            if (persist)
            {
                ForestSaving.PrepareFolder(this);
                c0 = ForestSaving.SaveMap(this, c0, "Control0", false);
                c1 = ForestSaving.SaveMap(this, c1, "Control1", false);
                lm = ForestSaving.SaveMap(this, lm, "LightMap", true);
            }
#endif

            ForestData data = AcquireData(persist);
            data.Fill(result, settings);
            data.FillBatches(result, assets, result.warnings);
#if UNITY_EDITOR
            if (persist) meshes = ForestSaving.SaveMeshes(this, data, meshes);
#endif
            data.control0 = c0;
            data.control1 = c1;
            data.lightMap = lm;

            BuildTerrainObjects(rootGO.transform, result, meshes, flags);
            BuildPropObjects(rootGO.transform, data, flags);
            BuildContainment(rootGO.transform, result, data, flags);

            int verts = 0, tris = 0;
            foreach (ForestChunkMesh chunk in result.chunks)
            {
                verts += chunk.vertices.Length;
                tris += chunk.triangles.Length / 3;
            }
            float monsterMetres = 0f;
            foreach (ForestTrail t in result.layout.trails) if (t.monsterAccessible) monsterMetres += t.length;
            data.stats = new ForestStats
            {
                seed = seed,
                seconds = (float)timer.Elapsed.TotalSeconds,
                chunks = result.chunks.Count,
                vertices = verts,
                triangles = tris,
                trailMetres = result.layout.TotalTrailLength,
                monsterTrailMetres = monsterMetres,
                clearings = result.layout.clearings.Count,
                markers = result.markers.Count,
                warnings = result.warnings.Count,
                instances = data.InstanceCount,
                timings = result.timings.ToString()
            };

#if UNITY_EDITOR
            if (persist) ForestSaving.Commit(this, data);
#endif
            World.SetData(data);
            if (groundLayers != null && groundLayers.material != null) groundLayers.ApplyTo(groundLayers.material);
            MovePlayerToSpawn();

            foreach (string w in result.warnings) Debug.LogWarning("ForestGenerator: " + w, this);
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
            if (Generated != null) Generated(this);
            if (onGenerated != null) onGenerated.Invoke();
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

        /// <summary>Removes the generated forest. In the editor its saved files are deleted too.</summary>
        [ContextMenu("Clear")]
        public void Clear()
        {
            ClearGenerated();
            World.SetData(null);
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ForestSaving.DeleteSaved(this);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
        }

        public static int NewRandomSeed()
        {
            return new System.Random(System.Guid.NewGuid().GetHashCode()).Next(1, int.MaxValue);
        }

        // ---------------------------------------------------------------- building Unity objects

        ForestData AcquireData(bool persist)
        {
#if UNITY_EDITOR
            if (persist) return ForestSaving.LoadOrCreateData(this);
#endif
            var data = ScriptableObject.CreateInstance<ForestData>();
            data.name = GeneratedPrefix + "Data";
            data.hideFlags = HideFlags.DontSave;
            return data;
        }

        static Texture2D MakeMap(Color32[] pixels, int res, string name)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }

        static Mesh MakeMesh(ForestChunkMesh chunk)
        {
            var mesh = new Mesh { name = GeneratedPrefix + chunk.Name };
            if (chunk.vertices.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = chunk.vertices;
            mesh.normals = chunk.normals;
            mesh.uv = chunk.uv;
            mesh.triangles = chunk.triangles;
            mesh.bounds = chunk.bounds;
            mesh.hideFlags = HideFlags.DontSave;
            return mesh;
        }

        /// <summary>
        /// The hidden containment: thick wall colliders standing in the boundary slope (a closed ring a few metres outside the
        /// playable edge, from below the ground up to the ceiling) and one invisible ceiling above the tallest treetop plus
        /// the clearance. Together they form a closed box: with Gorilla locomotion players can climb any collider (walls,
        /// cliffs, trees), so nothing else could stop a determined climber. No renderers; on the forest layer.
        /// </summary>
        void BuildContainment(Transform root, ForestBuildResult result, ForestData data, HideFlags flags)
        {
            ForestBoundarySettings b = settings.boundary;
            data.ceilingHeight = 0f;
            if (!b.buildContainment || data.wallLine == null || data.wallLine.Length < 3) return;

            // the ceiling: above the highest ground and the highest treetop
            float top = float.MinValue;
            foreach (float h in result.heights.grid.values) top = Mathf.Max(top, h);
            if (data.batches != null)
                foreach (ForestInstanceBatch batch in data.batches)
                {
                    ForestPrefabInfo info = ForestPrefabInfo.Get(batch.prefab);
                    if (info == null) continue;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        Vector3 p, s;
                        Quaternion r;
                        batch.Get(i, out p, out r, out s);
                        top = Mathf.Max(top, p.y + info.bounds.max.y * s.y);
                    }
                }
            float ceiling = top + b.ceilingClearance;
            data.ceilingHeight = ceiling;

            var boundary = new GameObject("Boundary") { hideFlags = flags, layer = settings.terrain.layer };
            boundary.transform.SetParent(root, false);
            Vector2[] line = data.wallLine;
            float thick = b.wallThickness;
            for (int i = 0; i < line.Length; i++)
            {
                Vector2 a = line[i], c = line[(i + 1) % line.Length];
                Vector2 dir = c - a;
                float len = dir.magnitude;
                if (len < 0.01f) continue;
                dir /= len;
                Vector2 mid = (a + c) * 0.5f;
                var outward = new Vector2(dir.y, -dir.x);
                if (Vector2.Dot(outward, mid) < 0f) outward = -outward;
                Vector2 center = mid + outward * (thick * 0.5f);
                float bottom = Mathf.Min(result.heights.SampleHeight(a), Mathf.Min(result.heights.SampleHeight(c), result.heights.SampleHeight(mid))) - 6f;

                var wall = new GameObject("Wall") { hideFlags = flags, layer = settings.terrain.layer };
                wall.transform.SetParent(boundary.transform, false);
                wall.transform.localPosition = new Vector3(center.x, (bottom + ceiling) * 0.5f, center.y);
                wall.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg, 0f);
                var box = wall.AddComponent<BoxCollider>();
                box.size = new Vector3(thick, ceiling - bottom, len + thick);    // overlapping ends close the corners
            }

            var lid = new GameObject("Ceiling") { hideFlags = flags, layer = settings.terrain.layer };
            lid.transform.SetParent(boundary.transform, false);
            lid.transform.localPosition = new Vector3(data.terrainMin.x + data.terrainSize.x * 0.5f, ceiling + 1f, data.terrainMin.y + data.terrainSize.y * 0.5f);
            var lidBox = lid.AddComponent<BoxCollider>();
            lidBox.size = new Vector3(data.terrainSize.x + 40f, 2f, data.terrainSize.y + 40f);
        }

        /// <summary>Real prefab instances for the categories that cannot be instanced (lights, scripts...) or were set to GameObjects.</summary>
        void BuildPropObjects(Transform root, ForestData data, HideFlags flags)
        {
            Transform props = null;
            foreach (ForestInstanceBatch batch in data.batches)
            {
                if (batch.mode != ForestRenderMode.GameObjects || batch.prefab == null) continue;
                if (props == null)
                {
                    var go = new GameObject("Props") { hideFlags = flags, layer = settings.terrain.layer };
                    go.transform.SetParent(root, false);
                    props = go.transform;
                }
                Transform prefabRoot = batch.prefab.transform;
                for (int i = 0; i < batch.Count; i++)
                {
                    Vector3 p, s;
                    Quaternion r;
                    batch.Get(i, out p, out r, out s);
                    GameObject inst = null;
#if UNITY_EDITOR
                    if (!Application.isPlaying) inst = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(batch.prefab, gameObject.scene);
#endif
                    if (inst == null) inst = Instantiate(batch.prefab);
                    Transform t = inst.transform;
                    t.SetParent(props, false);
                    t.localPosition = p;
                    t.localRotation = r * prefabRoot.localRotation;
                    t.localScale = Vector3.Scale(s, prefabRoot.localScale);

                    // streetlights: state and colour decided by the generator
                    ForestLamp lamp = inst.GetComponent<ForestLamp>();
                    if (lamp != null && batch.extra != null && i < batch.extra.Length)
                    {
                        int code = Mathf.RoundToInt(batch.extra[i]);
                        ForestStreetlightSettings sl = settings.streetlights;
                        lamp.state = (ForestLampState)Mathf.Clamp(code % 10, 0, 2);
                        lamp.color = code >= 10 ? sl.greenColor : sl.warmColor;
                        lamp.intensity = sl.intensity;
                        lamp.range = sl.range;
                    }
                    if (flags != HideFlags.None)
                        foreach (Transform c in inst.GetComponentsInChildren<Transform>(true)) c.gameObject.hideFlags = flags;
                }
            }
        }

        void BuildTerrainObjects(Transform root, ForestBuildResult result, List<Mesh> meshes, HideFlags flags)
        {
            var terrainGO = new GameObject("Terrain");
            terrainGO.hideFlags = flags;
            terrainGO.layer = settings.terrain.layer;
            terrainGO.transform.SetParent(root, false);

            Material mat = groundLayers != null ? groundLayers.material : null;
            if (mat == null) Debug.LogWarning("ForestGenerator: no ground material (assign Ground Layers with a material). The terrain is drawn without one.", this);

            for (int i = 0; i < result.chunks.Count; i++)
            {
                ForestChunkMesh chunk = result.chunks[i];
                var go = new GameObject(chunk.Name);
                go.hideFlags = flags;
                go.layer = settings.terrain.layer;
                go.transform.SetParent(terrainGO.transform, false);
                go.transform.localPosition = chunk.origin;
                go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = settings.terrain.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                go.AddComponent<MeshCollider>().sharedMesh = meshes[i];
            }
        }

        /// <summary>Removes the generated objects and frees the meshes and textures that are not saved files.</summary>
        void ClearGenerated()
        {
            var doomed = new HashSet<Object>();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name != RootName) continue;
                foreach (MeshFilter mf in child.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null && !IsAsset(mf.sharedMesh)) doomed.Add(mf.sharedMesh);
                if (Application.isPlaying)
                {
                    child.gameObject.SetActive(false);
                    child.SetParent(null);
                }
                Kill(child.gameObject);
            }

            ForestData old = World.data;
            if (old != null && !IsAsset(old))
            {
                if (old.control0 != null && !IsAsset(old.control0)) doomed.Add(old.control0);
                if (old.control1 != null && !IsAsset(old.control1)) doomed.Add(old.control1);
                if (old.lightMap != null && !IsAsset(old.lightMap)) doomed.Add(old.lightMap);
                doomed.Add(old);
            }
            foreach (Object o in doomed) Kill(o);
        }

        static bool IsAsset(Object o)
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.Contains(o);
#else
            return false;
#endif
        }

        static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void MovePlayerToSpawn()
        {
            if (movePlayerToSpawn == null || World.data == null) return;
            Pose pose = World.SpawnPose;
            movePlayerToSpawn.SetPositionAndRotation(pose.position + Vector3.up * spawnHeightOffset, pose.rotation);
            Rigidbody body = movePlayerToSpawn.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic && Application.isPlaying)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
        }

#if UNITY_EDITOR
        void EnsureSaveId()
        {
            bool shared = false;
            if (!string.IsNullOrEmpty(saveId))
                foreach (ForestGenerator other in FindObjectsByType<ForestGenerator>(FindObjectsSortMode.None))
                    if (other != this && other.saveId == saveId) shared = true;
            if (string.IsNullOrEmpty(saveId) || shared)
            {
                saveId = System.Guid.NewGuid().ToString("N").Substring(0, 8);
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }

        /// <summary>The folder the files were last saved in (it moves along if the scene is renamed).</summary>
        public string LastSaveFolder
        {
            get { return savedFolder; }
            set
            {
                if (savedFolder == value) return;
                savedFolder = value;
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }

        /// <summary>The folder this forest's files are saved in.</summary>
        public string SaveFolder
        {
            get
            {
                string sceneName = gameObject.scene.name;
                if (string.IsNullOrEmpty(sceneName)) sceneName = "Scene";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) sceneName = sceneName.Replace(c, '_');
                return ForestSaving.SavedRoot + "/" + sceneName + "_" + saveId;
            }
        }
#endif

        // ---------------------------------------------------------------- gizmos

        void OnDrawGizmos()
        {
            if (gizmos != null && gizmos.alwaysShow) DrawGizmos();
        }

        void OnDrawGizmosSelected()
        {
            if (gizmos != null && !gizmos.alwaysShow) DrawGizmos();
        }

        void DrawGizmos()
        {
            ForestData data = World != null ? World.data : null;
            if (data == null || gizmos == null) return;
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            System.Func<Vector2, float, Vector3> at = (p, lift) => new Vector3(p.x, data.HeightAt(p) + lift, p.y);

            if (gizmos.playableEdge && data.playableEdge != null) DrawLoop(data.playableEdge, new Color(1f, 0.85f, 0.2f), at, 0.5f);
            if (gizmos.hiddenWall && data.wallLine != null) DrawLoop(data.wallLine, new Color(1f, 0.3f, 0.2f), at, 1f);

            if ((gizmos.trails || gizmos.monsterClearance) && data.trails != null)
            {
                foreach (ForestTrail t in data.trails)
                {
                    Color c = t.kind == ForestTrailKind.Main ? new Color(1f, 0.55f, 0.1f) : t.kind == ForestTrailKind.PlayerOnly ? new Color(0.3f, 0.9f, 1f) : new Color(1f, 0.9f, 0.5f);
                    for (int k = 1; k < t.points.Count; k++)
                    {
                        if (gizmos.trails)
                        {
                            Gizmos.color = c;
                            Gizmos.DrawLine(at(t.points[k - 1], 0.3f), at(t.points[k], 0.3f));
                        }
                        if (gizmos.monsterClearance && t.monsterAccessible && k % 2 == 0)
                        {
                            Vector2 dir = (t.points[k] - t.points[k - 1]).normalized;
                            var n = new Vector2(-dir.y, dir.x) * Mathf.Max(t.HalfWidth, settings != null ? settings.monster.CorridorHalfWidth : 1.6f);
                            Gizmos.color = new Color(0.9f, 0.2f, 0.9f, 0.6f);
                            Gizmos.DrawLine(at(t.points[k] + n, 0.2f), at(t.points[k] - n, 0.2f));
                        }
                    }
                }
            }

            if ((gizmos.clearings || gizmos.monsterClearance) && data.clearings != null)
            {
                foreach (ForestClearing c in data.clearings)
                {
                    if (gizmos.clearings)
                    {
                        Gizmos.color = c.heart ? new Color(0.8f, 0.1f, 0.1f) : c.trailhead ? new Color(0.2f, 1f, 0.3f) : new Color(0.6f, 1f, 0.6f);
                        Vector3 prev = Vector3.zero;
                        for (int k = 0; k <= 48; k++)
                        {
                            float a = k * Mathf.PI * 2f / 48f;
                            Vector3 p = at(c.center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * c.RadiusAt(a), 0.4f);
                            if (k > 0) Gizmos.DrawLine(prev, p);
                            prev = p;
                        }
                    }
                    if (gizmos.monsterClearance && settings != null)
                    {
                        Gizmos.color = new Color(0.9f, 0.2f, 0.9f, 0.8f);
                        float r = settings.monster.MinClearingRadius;
                        Vector3 prev = Vector3.zero;
                        for (int k = 0; k <= 32; k++)
                        {
                            float a = k * Mathf.PI * 2f / 32f;
                            Vector3 p = at(c.center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, 0.5f);
                            if (k > 0) Gizmos.DrawLine(prev, p);
                            prev = p;
                        }
                    }
                }
            }

            if (gizmos.zones && data.zoneSites != null && settings != null)
            {
                foreach (ForestZoneSite z in data.zoneSites)
                {
                    List<ForestZone> zones = settings.zones.zones;
                    Gizmos.color = z.zone < zones.Count && zones[z.zone] != null ? zones[z.zone].debugColor : Color.white;
                    Gizmos.DrawSphere(at(z.position, 6f), 3f);
                }
            }

            if (gizmos.monsterClearance && data.clearanceViolations != null)
            {
                Gizmos.color = Color.red;
                foreach (Vector3 v in data.clearanceViolations) Gizmos.DrawSphere(v + Vector3.up, 1f);
            }

            if (gizmos.markers && data.markers != null)
            {
                foreach (ForestMarker m in data.markers)
                {
                    Gizmos.color = MarkerColor(m.type);
                    Vector3 p = m.position + Vector3.up * 1.5f;
                    Gizmos.DrawWireCube(p, Vector3.one * 1.2f);
                    Gizmos.DrawLine(p, p + m.Forward * 3f);
                }
            }
            Gizmos.matrix = old;
        }

        static void DrawLoop(Vector2[] pts, Color c, System.Func<Vector2, float, Vector3> at, float lift)
        {
            Gizmos.color = c;
            for (int i = 0; i < pts.Length; i++) Gizmos.DrawLine(at(pts[i], lift), at(pts[(i + 1) % pts.Length], lift));
        }

        public static Color MarkerColor(ForestMarkerType t)
        {
            switch (t)
            {
                case ForestMarkerType.Spawn: return Color.green;
                case ForestMarkerType.Heart: return Color.red;
                case ForestMarkerType.Clearing: return new Color(0.6f, 1f, 0.6f);
                case ForestMarkerType.Junction: return Color.white;
                case ForestMarkerType.DeadEnd: return new Color(0.6f, 0.4f, 0.2f);
                case ForestMarkerType.Landmark: return new Color(1f, 0.8f, 0f);
                case ForestMarkerType.RevealPoint: return Color.cyan;
                case ForestMarkerType.BlindCorner: return new Color(1f, 0.4f, 0.1f);
                case ForestMarkerType.AmbientSound: return new Color(0.5f, 0.5f, 1f);
                case ForestMarkerType.MonsterGap: return Color.magenta;
                case ForestMarkerType.MonsterLair: return new Color(0.5f, 0f, 0.5f);
                case ForestMarkerType.Streetlight: return new Color(0.6f, 1f, 0.5f);
                default: return Color.gray;
            }
        }
    }
}
