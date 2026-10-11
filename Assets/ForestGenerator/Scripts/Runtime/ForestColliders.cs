using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Rebuilds the colliders of the instanced objects (trees, rocks, logs...) from their prefabs' own colliders when the
    /// forest loads, as static collider-only objects (no renderers, no rigidbodies). They are not saved in the scene, which
    /// keeps the scene file small; they exist in edit mode too, so physics queries and NavMesh baking see them.
    /// Categories with Colliders off get none, so decoration costs nothing in physics.
    /// </summary>
    public static class ForestColliders
    {
        public const string RootName = "__ForestColliders";

        public static GameObject Build(Transform parent, ForestData data, out int count)
        {
            count = 0;
            if (data == null || data.batches == null) return null;
            var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
            root.layer = data.layer;
            root.transform.SetParent(parent, false);

            foreach (ForestInstanceBatch batch in data.batches)
            {
                if (batch == null || !batch.colliders || batch.mode != ForestRenderMode.Instanced || batch.prefab == null) continue;
                ForestPrefabInfo info = ForestPrefabInfo.Get(batch.prefab);
                if (info == null || info.colliders.Count == 0) continue;

                var group = new GameObject(batch.prefab.name) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
                group.layer = data.layer;
                group.transform.SetParent(root.transform, false);
                for (int i = 0; i < batch.Count; i++)
                {
                    Matrix4x4 inst = batch.LocalMatrix(i);
                    foreach (ForestPrefabInfo.ColliderShape shape in info.colliders)
                    {
                        Matrix4x4 m = inst * shape.local;
                        var go = new GameObject("c") { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
                        go.layer = data.layer;
                        Transform t = go.transform;
                        t.SetParent(group.transform, false);
                        t.localPosition = m.GetColumn(3);
                        t.localRotation = m.rotation;
                        t.localScale = m.lossyScale;
                        AddShape(go, shape);
                        count++;
                    }
                }
            }
            return root;
        }

        static void AddShape(GameObject go, ForestPrefabInfo.ColliderShape s)
        {
            Collider c;
            switch (s.type)
            {
                case ForestPrefabInfo.ShapeType.Box:
                {
                    var b = go.AddComponent<BoxCollider>();
                    b.center = s.center;
                    b.size = s.size;
                    c = b;
                    break;
                }
                case ForestPrefabInfo.ShapeType.Sphere:
                {
                    var sp = go.AddComponent<SphereCollider>();
                    sp.center = s.center;
                    sp.radius = s.radius;
                    c = sp;
                    break;
                }
                case ForestPrefabInfo.ShapeType.Capsule:
                {
                    var cap = go.AddComponent<CapsuleCollider>();
                    cap.center = s.center;
                    cap.radius = s.radius;
                    cap.height = s.height;
                    cap.direction = s.direction;
                    c = cap;
                    break;
                }
                default:
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = s.mesh;
                    mc.convex = s.convex;
                    c = mc;
                    break;
                }
            }
            c.isTrigger = s.isTrigger;
            c.sharedMaterial = s.material;
        }

        public static void Destroy(GameObject root)
        {
            if (root == null) return;
            if (Application.isPlaying)
            {
                root.SetActive(false);
                Object.Destroy(root);
            }
            else
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
