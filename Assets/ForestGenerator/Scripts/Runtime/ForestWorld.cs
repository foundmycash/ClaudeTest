using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// The generated forest at runtime: hands the ground maps to the forest shaders, picks the quality level for the
    /// headset, and answers questions about the forest in world space (ground height, how deep in a spot is, the markers,
    /// the nearest trail). Lives on the Forest object next to the Forest Generator. Only one forest per scene is supported
    /// (the shader inputs are global).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Forest World")]
    public class ForestWorld : MonoBehaviour
    {
        static readonly int Control0Id = Shader.PropertyToID("_ForestControl0");
        static readonly int Control1Id = Shader.PropertyToID("_ForestControl1");
        static readonly int LightMapId = Shader.PropertyToID("_ForestLightMap");
        static readonly int MapParamsId = Shader.PropertyToID("_ForestMapParams");
        static readonly int WorldToMapId = Shader.PropertyToID("_ForestWorldToMap");

        /// <summary>The forest in the loaded scene (null when there is none).</summary>
        public static ForestWorld Active { get; private set; }

        [Tooltip("The generated forest. Set by the Forest Generator when you press Generate.")]
        public ForestData data;

        [Tooltip("Auto = the cheaper ground shading on Quest 2 / Quest 1, full everywhere else. Force Full or Lite to compare in the editor.")]
        public ForestDetailMode detail = ForestDetailMode.Auto;

        [Tooltip("Rebuild the colliders of the instanced objects (trees, rocks, logs) when the forest loads. They are not saved in the scene.")]
        public bool buildColliders = true;

        /// <summary>Raised after new forest data was assigned (a regenerate).</summary>
        public event System.Action<ForestWorld> DataChanged;

        /// <summary>Number of colliders built for the instanced objects.</summary>
        public int ColliderCount { get; private set; }

        GameObject colliderRoot;

        void OnEnable()
        {
            if (Active != null && Active != this)
                Debug.LogWarning("ForestWorld: more than one forest is enabled; only the last one gets its ground maps shown.", this);
            Active = this;
            Apply();
            BuildColliders();
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            ForestColliders.Destroy(colliderRoot);
            colliderRoot = null;
        }

        void OnValidate()
        {
            if (isActiveAndEnabled) Apply();
        }

        public void SetData(ForestData newData)
        {
            data = newData;
            Apply();
            BuildColliders();
            if (DataChanged != null) DataChanged(this);
        }

        /// <summary>(Re)builds the collider-only copies of the instanced objects' colliders.</summary>
        public void BuildColliders()
        {
            ForestColliders.Destroy(colliderRoot);
            colliderRoot = null;
            ColliderCount = 0;
            // also remove a leftover from before a script reload
            Transform old = transform.Find(ForestColliders.RootName);
            if (old != null) ForestColliders.Destroy(old.gameObject);
            if (!buildColliders || data == null) return;
            int n;
            colliderRoot = ForestColliders.Build(transform, data, out n);
            ColliderCount = n;
        }

        /// <summary>Sends the ground maps and the forest's placement to the shaders.</summary>
        public void Apply()
        {
            ForestDetail.Apply(detail);
            if (data == null) return;
            Shader.SetGlobalTexture(Control0Id, data.control0 != null ? (Texture)data.control0 : Texture2D.blackTexture);
            Shader.SetGlobalTexture(Control1Id, data.control1 != null ? (Texture)data.control1 : Texture2D.blackTexture);
            Shader.SetGlobalTexture(LightMapId, data.lightMap != null ? (Texture)data.lightMap : Texture2D.whiteTexture);
            Vector2 size = new Vector2(Mathf.Max(1f, data.terrainSize.x), Mathf.Max(1f, data.terrainSize.y));
            Shader.SetGlobalVector(MapParamsId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));

            // world position -> (u, ?, v) on the ground maps
            Matrix4x4 toMap = Matrix4x4.Scale(new Vector3(1f / size.x, 1f, 1f / size.y))
                            * Matrix4x4.Translate(new Vector3(-data.terrainMin.x, 0f, -data.terrainMin.y))
                            * transform.worldToLocalMatrix;
            Shader.SetGlobalMatrix(WorldToMapId, toMap);
        }

        void Update()
        {
            // keep the shaders in step if the forest object is moved
            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                Apply();
            }
        }

        // ---------------------------------------------------------------- queries (world space)

        public Vector2 ToLocal2D(Vector3 world)
        {
            Vector3 l = transform.InverseTransformPoint(world);
            return new Vector2(l.x, l.z);
        }

        public Vector3 ToWorld(Vector3 local)
        {
            return transform.TransformPoint(local);
        }

        /// <summary>World-space height of the ground below (or above) a world position.</summary>
        public float GroundHeight(Vector3 world)
        {
            if (data == null) return transform.position.y;
            Vector2 l = ToLocal2D(world);
            return transform.TransformPoint(new Vector3(l.x, data.HeightAt(l), l.y)).y;
        }

        /// <summary>0..1: how deep in the forest a world position is (0 at the trailhead, 1 at the heart, more off the trails).</summary>
        public float DreadAt(Vector3 world)
        {
            return data != null ? data.DreadAt(ToLocal2D(world)) : 0f;
        }

        /// <summary>Where the player starts and which way they face.</summary>
        public Pose SpawnPose
        {
            get
            {
                if (data == null) return new Pose(transform.position, transform.rotation);
                return new Pose(ToWorld(data.spawnPosition), transform.rotation * Quaternion.Euler(0f, data.spawnYaw, 0f));
            }
        }

        /// <summary>A marker converted to world space (position and yaw).</summary>
        public ForestMarker ToWorld(ForestMarker m)
        {
            m.position = ToWorld(m.position);
            m.yaw += transform.eulerAngles.y;
            return m;
        }

        /// <summary>Adds every marker of a type (in world space) to the list. Returns how many were added.</summary>
        public int GetMarkers(ForestMarkerType type, List<ForestMarker> results)
        {
            if (data == null || data.markers == null) return 0;
            int n = 0;
            for (int i = 0; i < data.markers.Count; i++)
            {
                if (data.markers[i].type != type) continue;
                results.Add(ToWorld(data.markers[i]));
                n++;
            }
            return n;
        }

        /// <summary>The nearest marker of a type to a world position (world space). False when there are none.</summary>
        public bool TryGetNearestMarker(ForestMarkerType type, Vector3 world, out ForestMarker marker)
        {
            marker = default(ForestMarker);
            if (data == null || data.markers == null) return false;
            Vector3 local = transform.InverseTransformPoint(world);
            float best = float.MaxValue;
            int bi = -1;
            for (int i = 0; i < data.markers.Count; i++)
            {
                if (data.markers[i].type != type) continue;
                float d = (data.markers[i].position - local).sqrMagnitude;
                if (d < best) { best = d; bi = i; }
            }
            if (bi < 0) return false;
            marker = ToWorld(data.markers[bi]);
            return true;
        }

        /// <summary>The nearest point on any trail (world space, on the ground). Optionally only trails the monster can use.</summary>
        public bool TryGetNearestTrailPoint(Vector3 world, bool monsterRoutesOnly, out Vector3 point)
        {
            point = world;
            if (data == null || data.trails == null) return false;
            Vector2 l = ToLocal2D(world);
            float best = float.MaxValue;
            Vector2 bp = l;
            foreach (ForestTrail t in data.trails)
            {
                if (monsterRoutesOnly && !t.monsterAccessible) continue;
                for (int k = 0; k < t.points.Count; k++)
                {
                    float d = (t.points[k] - l).sqrMagnitude;
                    if (d < best) { best = d; bp = t.points[k]; }
                }
            }
            if (best == float.MaxValue) return false;
            point = ToWorld(new Vector3(bp.x, data.HeightAt(bp), bp.y));
            return true;
        }

        /// <summary>Signed distance in metres from a world position to the playable edge: positive inside.</summary>
        public float EdgeDistance(Vector3 world)
        {
            if (data == null || data.playableEdge == null || data.playableEdge.Length < 3) return float.MaxValue;
            Vector2 p = ToLocal2D(world);
            Vector2[] e = data.playableEdge;
            float best = float.MaxValue;
            bool inside = false;
            for (int i = 0, j = e.Length - 1; i < e.Length; j = i++)
            {
                Vector2 a = e[j], b = e[i];
                if (((b.y > p.y) != (a.y > p.y)) && (p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x)) inside = !inside;
                best = Mathf.Min(best, ForestTrailRouter.DistanceToSegment(p, a, b));
            }
            return inside ? best : -best;
        }
    }
}
