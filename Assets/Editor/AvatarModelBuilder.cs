using System.Collections.Generic;
using System.Linq;
using GorillaNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the gorilla avatar model from Player.fbx: Assets/Models/Player/PlayerAvatar.prefab (the skeleton, one skinned
/// mesh and an <see cref="AvatarBodyRig"/>) and its mesh Assets/Models/Player/PlayerAvatarMesh.asset.
///
/// The FBX has four skinned meshes (Head, Body, Arms, Hands) that all use one material. They are merged into one, so a
/// player costs one draw call, and the vertex weights are cut down to the two strongest bones per vertex because the
/// Mobile quality level skins with two bones anyway (this keeps the hands from jumping between quality levels).
///
/// Run it again from the menu after changing Player.fbx. The two assets are overwritten in place, so everything that
/// uses them (the NetworkedAvatar prefab) keeps working.
/// </summary>
public static class AvatarModelBuilder
{
    public const string FbxPath = "Assets/Models/Player/Player.fbx";
    public const string PrefabPath = "Assets/Models/Player/PlayerAvatar.prefab";
    public const string MeshPath = "Assets/Models/Player/PlayerAvatarMesh.asset";

    /// <summary>The model is this much smaller than the FBX (the FBX gorilla is 3 m tall).</summary>
    public const float ModelScale = 0.3f;

    const int MaxInfluences = 2;

    [MenuItem("Tools/Gorilla/Rebuild Avatar Model")]
    public static void Build()
    {
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (fbx == null)
        {
            Debug.LogError($"[AvatarModelBuilder] {FbxPath} not found.");
            return;
        }

        // Work in a preview scene so the scene you have open isn't touched.
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = (GameObject)PrefabUtility.InstantiatePrefab(fbx, scene);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = "PlayerAvatar";
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            var sources = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (sources.Length == 0)
            {
                Debug.LogError("[AvatarModelBuilder] The FBX has no skinned meshes.");
                return;
            }

            var headSource = sources.FirstOrDefault(s => s.name == "Head");
            Transform Bone(string boneName) => FindDeep(root.transform, boneName);
            var headBone = Bone("Head");
            var bodyBone = Bone("Body");
            if (headBone == null || bodyBone == null)
            {
                Debug.LogError("[AvatarModelBuilder] Bones 'Body' and 'Head' are missing in the FBX.");
                return;
            }

            // New mesh object: identity under the root, so the merged vertices live in the root's space.
            var meshGo = new GameObject("Mesh");
            meshGo.transform.SetParent(root.transform, false);
            var bones = sources.SelectMany(s => s.bones).Where(b => b != null).Distinct().ToArray();
            Mesh merged = Merge(sources, meshGo.transform, bones, headSource, out Vector3 headCenterWorld, out Bounds worldBounds);

            // Write the mesh asset (in place when it exists, so references to it survive).
            var existingMesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            Mesh meshAsset;
            if (existingMesh != null)
            {
                EditorUtility.CopySerialized(merged, existingMesh);
                meshAsset = existingMesh;
                Object.DestroyImmediate(merged);
            }
            else
            {
                AssetDatabase.CreateAsset(merged, MeshPath);
                meshAsset = merged;
            }

            var renderer = meshGo.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = meshAsset;
            renderer.bones = bones;
            renderer.rootBone = bodyBone;
            renderer.sharedMaterials = new[] { sources[0].sharedMaterial };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.skinnedMotionVectors = false;
            renderer.updateWhenOffscreen = false;
            // Generous bounds (in the root bone's space) so the stretching arms are never culled.
            float half = Mathf.Max(worldBounds.extents.x, worldBounds.extents.y, worldBounds.extents.z) * 1.6f + 1f;
            renderer.localBounds = new Bounds(bodyBone.InverseTransformPoint(worldBounds.center), Vector3.one * (half / Mathf.Max(bodyBone.lossyScale.x, 1e-6f)));

            foreach (var s in sources)
                Object.DestroyImmediate(s.gameObject);

            var rig = root.AddComponent<AvatarBodyRig>();
            rig.body = renderer;
            rig.headBone = headBone;
            rig.headCenterLocal = headBone.InverseTransformPoint(headCenterWorld);
            rig.leftArm = MakeArm(Bone, "L");
            rig.rightArm = MakeArm(Bone, "R");

            root.transform.localScale = Vector3.one * ModelScale;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AvatarModelBuilder] Built {PrefabPath}: {meshAsset.vertexCount} vertices, {bones.Length} bones, {MaxInfluences} influences per vertex.");
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    static AvatarBodyRig.Arm MakeArm(System.Func<string, Transform> bone, string side) => new AvatarBodyRig.Arm
    {
        upperArm = bone("Arm1." + side),
        foreArm = bone("Arm2." + side),
        hand = bone("Hand." + side),
        pointerRoot = bone("Pointer1." + side),
        ringRoot = bone("Ring1." + side),
        thumbRoot = bone("Thumb1." + side),
    };

    static Transform FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == name)
                return t;
        }
        return null;
    }

    // Merges the skinned meshes into one mesh in the space of `space` (the bind pose is the pose the FBX instance is in).
    static Mesh Merge(SkinnedMeshRenderer[] sources, Transform space, Transform[] bones, SkinnedMeshRenderer headSource, out Vector3 headCenterWorld, out Bounds worldBounds)
    {
        var boneIndex = new Dictionary<Transform, int>();
        for (int i = 0; i < bones.Length; i++)
            boneIndex[bones[i]] = i;

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        var weights = new List<BoneWeight>();

        Vector3 headMin = Vector3.one * float.MaxValue, headMax = Vector3.one * float.MinValue;
        Vector3 allMin = Vector3.one * float.MaxValue, allMax = Vector3.one * float.MinValue;

        foreach (var source in sources)
        {
            var mesh = source.sharedMesh;
            Matrix4x4 toSpace = space.worldToLocalMatrix * source.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = toSpace.inverse.transpose;
            int offset = vertices.Count;

            var v = mesh.vertices; var n = mesh.normals; var t = mesh.tangents; var uv = mesh.uv; var bw = mesh.boneWeights;
            var sourceBones = source.bones;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = toSpace.MultiplyPoint3x4(v[i]);
                vertices.Add(p);
                normals.Add(n.Length > 0 ? normalMatrix.MultiplyVector(n[i]).normalized : Vector3.up);
                if (t.Length > 0)
                {
                    Vector3 tangent = toSpace.MultiplyVector(t[i]).normalized;
                    tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, t[i].w));
                }
                else
                {
                    tangents.Add(new Vector4(1, 0, 0, 1));
                }
                uvs.Add(uv.Length > 0 ? uv[i] : Vector2.zero);
                weights.Add(Remap(bw[i], sourceBones, boneIndex));

                Vector3 world = space.TransformPoint(p);
                allMin = Vector3.Min(allMin, world); allMax = Vector3.Max(allMax, world);
                if (source == headSource)
                {
                    headMin = Vector3.Min(headMin, world); headMax = Vector3.Max(headMax, world);
                }
            }

            foreach (int index in mesh.triangles)
                triangles.Add(index + offset);
        }

        var merged = new Mesh { name = "PlayerAvatarMesh" };
        if (vertices.Count > 65000)
            merged.indexFormat = IndexFormat.UInt32;
        merged.SetVertices(vertices);
        merged.SetNormals(normals);
        merged.SetTangents(tangents);
        merged.SetUVs(0, uvs);
        merged.SetTriangles(triangles, 0);
        merged.boneWeights = weights.ToArray();
        merged.bindposes = bones.Select(b => b.worldToLocalMatrix * space.localToWorldMatrix).ToArray();
        merged.RecalculateBounds();

        headCenterWorld = headSource != null ? (headMin + headMax) * 0.5f : space.position;
        worldBounds = new Bounds((allMin + allMax) * 0.5f, allMax - allMin);
        return merged;
    }

    // Keeps the strongest MaxInfluences bones of a vertex (renormalized) and points them at the merged bone list.
    static BoneWeight Remap(BoneWeight w, Transform[] sourceBones, Dictionary<Transform, int> boneIndex)
    {
        var list = new List<(int bone, float weight)>
        {
            (w.boneIndex0, w.weight0), (w.boneIndex1, w.weight1), (w.boneIndex2, w.weight2), (w.boneIndex3, w.weight3),
        };
        var strongest = list.Where(x => x.weight > 0f).OrderByDescending(x => x.weight).Take(MaxInfluences).ToList();
        float total = strongest.Sum(x => x.weight);
        var result = new BoneWeight();
        for (int k = 0; k < strongest.Count; k++)
        {
            int index = boneIndex[sourceBones[strongest[k].bone]];
            float weight = total > 0f ? strongest[k].weight / total : 0f;
            switch (k)
            {
                case 0: result.boneIndex0 = index; result.weight0 = weight; break;
                case 1: result.boneIndex1 = index; result.weight1 = weight; break;
            }
        }
        if (strongest.Count == 0)
            result.weight0 = 1f;
        return result;
    }
}
