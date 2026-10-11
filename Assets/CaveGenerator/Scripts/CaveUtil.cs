using UnityEngine;

namespace HavocCave
{
    public static class CaveUtil
    {
        /// <summary>Destroy now in edit mode, at end of frame in play mode.</summary>
        public static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>Apply hide flags to a generated object and everything under it. A layer of -1 leaves layers alone.</summary>
        public static void Tag(GameObject go, int layer, HideFlags flags)
        {
            Transform[] all = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (layer >= 0) all[i].gameObject.layer = layer;
                all[i].gameObject.hideFlags = flags;
            }
        }

        static Mesh cube;
        static Mesh cylinder;

        /// <summary>Unity's built-in cube mesh (1 x 1 x 1, centred).</summary>
        public static Mesh CubeMesh
        {
            get
            {
                if (cube == null) cube = GrabPrimitive(PrimitiveType.Cube);
                return cube;
            }
        }

        /// <summary>Unity's built-in cylinder mesh (1 wide, 2 tall, centred).</summary>
        public static Mesh CylinderMesh
        {
            get
            {
                if (cylinder == null) cylinder = GrabPrimitive(PrimitiveType.Cylinder);
                return cylinder;
            }
        }

        static Mesh GrabPrimitive(PrimitiveType type)
        {
            GameObject tmp = GameObject.CreatePrimitive(type);
            Mesh mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            tmp.SetActive(false);   // Destroy() is deferred in play mode; don't leave a live collider behind
            Kill(tmp);
            return mesh;
        }
    }
}
