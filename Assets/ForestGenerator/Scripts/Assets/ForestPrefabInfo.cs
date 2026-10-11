using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HavocForest
{
    /// <summary>
    /// What the forest needs to know about a prefab, read once and cached: its levels of detail (meshes, sub-meshes,
    /// materials and where they sit), its colliders, its size, and whether it can be drawn with GPU instancing or has to be
    /// a real GameObject. All transforms are relative to the prefab root's position but include the root's own rotation and
    /// scale (so a model exported with a -90 degree root still stands up).
    /// </summary>
    public sealed class ForestPrefabInfo
    {
        public struct Part
        {
            public Mesh mesh;
            public int submesh;
            public Material material;
            public Matrix4x4 local;
        }

        public sealed class Lod
        {
            /// <summary>Screen height (0..1) below which the next level takes over (from the LOD Group). 0 for the last level.</summary>
            public float screenHeight;
            public readonly List<Part> parts = new List<Part>();
        }

        public enum ShapeType { Box, Sphere, Capsule, Mesh }

        public struct ColliderShape
        {
            public ShapeType type;
            public Matrix4x4 local;      // the collider's GameObject, relative to the prefab root
            public Vector3 center;
            public Vector3 size;
            public float radius;
            public float height;
            public int direction;
            public Mesh mesh;
            public bool convex;
            public bool isTrigger;
            public PhysicsMaterial material;
        }

        public GameObject prefab;
        public readonly List<Lod> lods = new List<Lod>();
        public readonly List<ColliderShape> colliders = new List<ColliderShape>();

        /// <summary>Bounds of the first level of detail, relative to the prefab root's position.</summary>
        public Bounds bounds;

        /// <summary>The LOD Group's size in metres (0 without a LOD Group).</summary>
        public float lodSize;

        /// <summary>True when the prefab carries behaviour (scripts, lights, particles, audio, animation, physics bodies).</summary>
        public bool needsGameObject;
        public string needsGameObjectReason = "";

        /// <summary>True when every material can be GPU-instanced.</summary>
        public bool instancingReady = true;
        public string instancingProblem = "";

        /// <summary>Radius of the lowest capsule collider (a tree trunk). 0 when there is none.</summary>
        public float trunkRadius;

        /// <summary>The ground it covers (at scale 1), as a capsule in XZ: centre offset, direction, half length and radius.</summary>
        public Vector2 footprintCenter;
        public Vector2 footprintAxis = Vector2.right;
        public float footprintHalfLength;
        public float footprintRadius;

        public bool HasMeshes { get { return lods.Count > 0 && lods[0].parts.Count > 0; } }

        static readonly Dictionary<int, ForestPrefabInfo> cache = new Dictionary<int, ForestPrefabInfo>();

        public static ForestPrefabInfo Get(GameObject prefab)
        {
            if (prefab == null) return null;
            ForestPrefabInfo info;
            if (cache.TryGetValue(prefab.GetInstanceID(), out info) && info.prefab != null) return info;
            info = Analyze(prefab);
            cache[prefab.GetInstanceID()] = info;
            return info;
        }

        /// <summary>Forget everything read so far (call after editing prefabs).</summary>
        public static void ClearCache()
        {
            cache.Clear();
        }

        static ForestPrefabInfo Analyze(GameObject prefab)
        {
            var info = new ForestPrefabInfo { prefab = prefab };
            Transform root = prefab.transform;
            Matrix4x4 rootSpace = Matrix4x4.Translate(-root.position);

            // ---- behaviour that instancing cannot reproduce
            foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (c is Transform || c is MeshFilter || c is MeshRenderer || c is LODGroup || c is Collider) continue;
                info.needsGameObject = true;
                if (info.needsGameObjectReason.Length < 120)
                    info.needsGameObjectReason += (info.needsGameObjectReason.Length > 0 ? ", " : "") + c.GetType().Name;
            }

            // ---- levels of detail
            LODGroup group = prefab.GetComponent<LODGroup>();
            if (group != null)
            {
                info.lodSize = group.size * MaxAbs(root.lossyScale);
                foreach (LOD l in group.GetLODs())
                {
                    var lod = new Lod { screenHeight = l.screenRelativeTransitionHeight };
                    foreach (Renderer r in l.renderers) AddRenderer(info, lod, r, rootSpace);
                    info.lods.Add(lod);
                }
            }
            else
            {
                var lod = new Lod { screenHeight = 0f };
                foreach (MeshRenderer r in prefab.GetComponentsInChildren<MeshRenderer>(true)) AddRenderer(info, lod, r, rootSpace);
                info.lods.Add(lod);
            }
            if (info.lods.Count > 0) info.lods[info.lods.Count - 1].screenHeight = 0f;

            // ---- bounds of the first level
            bool any = false;
            Bounds b = new Bounds();
            if (info.lods.Count > 0)
            {
                foreach (Part p in info.lods[0].parts)
                {
                    Bounds mb = TransformBounds(p.mesh.bounds, p.local);
                    if (!any) { b = mb; any = true; }
                    else b.Encapsulate(mb);
                }
            }
            info.bounds = any ? b : new Bounds(Vector3.zero, Vector3.one);

            // ---- colliders
            Bounds cb = new Bounds();
            bool anyCollider = false;
            float lowestCapsule = float.MaxValue;
            foreach (Collider col in prefab.GetComponentsInChildren<Collider>(true))
            {
                var shape = new ColliderShape
                {
                    local = rootSpace * col.transform.localToWorldMatrix,
                    isTrigger = col.isTrigger,
                    material = col.sharedMaterial
                };
                var box = col as BoxCollider;
                var sphere = col as SphereCollider;
                var capsule = col as CapsuleCollider;
                var mesh = col as MeshCollider;
                if (box != null) { shape.type = ShapeType.Box; shape.center = box.center; shape.size = box.size; }
                else if (sphere != null) { shape.type = ShapeType.Sphere; shape.center = sphere.center; shape.radius = sphere.radius; }
                else if (capsule != null)
                {
                    shape.type = ShapeType.Capsule;
                    shape.center = capsule.center;
                    shape.radius = capsule.radius;
                    shape.height = capsule.height;
                    shape.direction = capsule.direction;
                    Vector3 lossy = col.transform.lossyScale;
                    float r = capsule.radius * Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.z));
                    float lowY = shape.local.MultiplyPoint3x4(capsule.center).y;
                    if (capsule.direction == 1 && lowY < lowestCapsule) { lowestCapsule = lowY; info.trunkRadius = r; }
                }
                else if (mesh != null && mesh.sharedMesh != null) { shape.type = ShapeType.Mesh; shape.mesh = mesh.sharedMesh; shape.convex = mesh.convex; }
                else continue;
                info.colliders.Add(shape);

                if (!col.isTrigger)
                {
                    Bounds wb = ShapeBounds(shape);
                    if (!anyCollider) { cb = wb; anyCollider = true; }
                    else cb.Encapsulate(wb);
                }
            }

            // ---- footprint: the colliders' extent on the ground, else the bounds
            Bounds fb = anyCollider ? cb : info.bounds;
            Vector3 size = fb.size;
            info.footprintCenter = new Vector2(fb.center.x, fb.center.z);
            if (size.x > size.z * 1.8f)
            {
                info.footprintAxis = Vector2.right;
                info.footprintRadius = size.z * 0.5f;
                info.footprintHalfLength = (size.x - size.z) * 0.5f;
            }
            else if (size.z > size.x * 1.8f)
            {
                info.footprintAxis = Vector2.up;
                info.footprintRadius = size.x * 0.5f;
                info.footprintHalfLength = (size.z - size.x) * 0.5f;
            }
            else
            {
                info.footprintRadius = Mathf.Max(size.x, size.z) * 0.5f * (anyCollider ? 1f : 0.85f);
                info.footprintHalfLength = 0f;
            }
            return info;
        }

        static void AddRenderer(ForestPrefabInfo info, Lod lod, Renderer r, Matrix4x4 rootSpace)
        {
            if (r == null) return;
            if (r is SkinnedMeshRenderer)
            {
                info.needsGameObject = true;
                info.needsGameObjectReason += (info.needsGameObjectReason.Length > 0 ? ", " : "") + "SkinnedMeshRenderer";
                return;
            }
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            Mesh mesh = mf.sharedMesh;
            Material[] mats = r.sharedMaterials;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                Material m = s < mats.Length ? mats[s] : (mats.Length > 0 ? mats[mats.Length - 1] : null);
                if (m == null)
                {
                    info.instancingReady = false;
                    info.instancingProblem = "a renderer has an empty material slot";
                    continue;
                }
                if (!m.enableInstancing)
                {
                    info.instancingReady = false;
                    if (info.instancingProblem.Length < 120)
                        info.instancingProblem += (info.instancingProblem.Length > 0 ? ", " : "") + "material '" + m.name + "' has GPU Instancing off";
                }
                lod.parts.Add(new Part { mesh = mesh, submesh = s, material = m, local = rootSpace * r.transform.localToWorldMatrix });
            }
        }

        static Bounds ShapeBounds(ColliderShape s)
        {
            Bounds local;
            switch (s.type)
            {
                case ShapeType.Box: local = new Bounds(s.center, s.size); break;
                case ShapeType.Sphere: local = new Bounds(s.center, Vector3.one * s.radius * 2f); break;
                case ShapeType.Capsule:
                {
                    Vector3 sz = Vector3.one * s.radius * 2f;
                    sz[s.direction] = Mathf.Max(s.height, s.radius * 2f);
                    local = new Bounds(s.center, sz);
                    break;
                }
                default: local = s.mesh != null ? s.mesh.bounds : new Bounds(Vector3.zero, Vector3.one); break;
            }
            return TransformBounds(local, s.local);
        }

        public static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            Vector3 c = m.MultiplyPoint3x4(b.center);
            Vector3 e = b.extents;
            Vector3 ax = m.MultiplyVector(new Vector3(e.x, 0f, 0f));
            Vector3 ay = m.MultiplyVector(new Vector3(0f, e.y, 0f));
            Vector3 az = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            var ext = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                                  Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                                  Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, ext * 2f);
        }

        static float MaxAbs(Vector3 v)
        {
            return Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
        }

        /// <summary>The distance (metres) at which a level of detail hands over, for a given vertical field of view and size.</summary>
        public static float SwitchDistance(float screenHeight, float objectSize, float fovDegrees)
        {
            if (screenHeight <= 0f) return float.MaxValue;
            float halfTan = Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);
            return objectSize / (2f * halfTan * screenHeight);
        }
    }
}
