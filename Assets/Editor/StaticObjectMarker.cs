using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Tools > Lighting > Mark Static Objects: ticks Static (Contribute GI, Occluder, Occludee, Batching, Reflection Probe) on
/// everything in the open scenes that never moves, so Unity can bake lighting into it and use it for occlusion culling.
/// "Preview" only lists what it would do; "Apply" does it as one Undo step and leaves the scene unsaved (Ctrl+S to keep it).
///
/// It is run again after regenerating the forest or the cave: the generators make brand new objects, and those start non-static.
///
/// What counts as static: a MeshRenderer that is opaque and has no mover on it or on a parent (Rigidbody, Animator, particles,
/// the XR rig, networked objects, the player, the loading cinematic...) and no script of the game on itself. When in doubt it
/// is left alone, because a wrongly static object that moves is a visible bug while a missed one only costs a little speed.
/// Not touched on purpose: skinned meshes, particles, UI, lights (they use their Mode instead), and colliders without a mesh.
///
/// Anything drawn without a GameObject cannot be static: the forest's trees, rocks and bushes go through the instanced renderer.
/// </summary>
public static class StaticObjectMarker
{
    // What the Inspector's "Static" tick sets, minus Batching: static batching copies a mesh into a combined one at build time,
    // which is only worth it for small meshes (see MaxBatchedVertices).
    const StaticEditorFlags Always = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic |
                                     StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
    const int MaxBatchedVertices = 10000;
    const int TransparentQueue = 2500;
    const int MaxNamesListed = 6;
    const string UndoName = "Mark Static Objects";

    // Any of these on an object or on a parent means it can move. Matched by class name, base classes included, so this file
    // needs no reference to the Photon, XR or game assemblies.
    static readonly HashSet<string> MovingTypes = new HashSet<string>
    {
        "Rigidbody", "Rigidbody2D", "CharacterController", "Animator", "Animation", "PlayableDirector",
        "ParticleSystem", "VisualEffect", "Canvas", "Camera", "XROrigin", "XRBaseInteractable",
        "NetworkObject", "NetworkBehaviour", "Player", "Dynamite", "LoadingSequence", "SceneTransition", "VolumetricFog"
    };

    // Scripts of the game that only mark an object and never move it. Anything else on the object itself keeps it non-static.
    static readonly HashSet<string> HarmlessScripts = new HashSet<string> { "Surface", "CaveBakedLight" };

    static readonly Dictionary<Shader, bool> lightmapAware = new Dictionary<Shader, bool>();

    [MenuItem("Tools/Lighting/Mark Static Objects (Preview)")]
    static void Preview()
    {
        Debug.Log(Run(false));
    }

    [MenuItem("Tools/Lighting/Mark Static Objects (Apply)")]
    static void Apply()
    {
        Debug.Log(Run(true));
    }

    /// <summary>Goes through every loaded scene. Returns the report; changes things only when apply is true.</summary>
    public static string Run(bool apply)
    {
        lightmapAware.Clear();
        var report = new StringBuilder();
        var marked = new SortedDictionary<string, int>();
        var unchanged = new SortedDictionary<string, int>();
        var skipped = new SortedDictionary<string, List<string>>();
        var noLightmapUv = new List<string>();
        var lightProbeOnly = new List<string>();
        var touchedScenes = new HashSet<Scene>();

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);

        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MeshRenderer r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                GameObject go = r.gameObject;
                string key = scene.name + "/" + Pattern(PathOf(go.transform));

                string why = WhyNot(r);
                if (why != null)
                {
                    if (!skipped.TryGetValue(why, out List<string> names)) skipped[why] = names = new List<string>();
                    names.Add(scene.name + "/" + PathOf(go.transform));
                    continue;
                }

                MeshFilter filter = r.GetComponent<MeshFilter>();
                Mesh mesh = filter.sharedMesh;
                StaticEditorFlags have = GameObjectUtility.GetStaticEditorFlags(go);
                StaticEditorFlags want = have | Always;
                if (mesh.vertexCount <= MaxBatchedVertices) want |= StaticEditorFlags.BatchingStatic;

                // A shader that never reads the lightmap would show nothing of it, and a lightmapped renderer also loses its
                // ambient light probe values (the shader's SampleSH comes back black), so those only contribute to the bake.
                bool wantProbes = !ReadsLightmaps(r.sharedMaterials);
                bool changeGI = wantProbes && r.receiveGI != ReceiveGI.LightProbes;
                if (wantProbes) lightProbeOnly.Add(key);
                else if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) noLightmapUv.Add(scene.name + "/" + PathOf(go.transform));

                if (want == have && !changeGI)
                {
                    Count(unchanged, key);
                    continue;
                }
                Count(marked, key);
                if (!apply) continue;

                var goObject = new SerializedObject(go);
                goObject.FindProperty("m_StaticEditorFlags").intValue = (int)want;
                goObject.ApplyModifiedProperties();
                if (changeGI)
                {
                    var rendererObject = new SerializedObject(r);
                    rendererObject.FindProperty("m_ReceiveGI").intValue = (int)ReceiveGI.LightProbes;
                    rendererObject.ApplyModifiedProperties();
                }
                touchedScenes.Add(scene);
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        foreach (Scene scene in touchedScenes) EditorSceneManager.MarkSceneDirty(scene);

        report.AppendLine(apply ? "STATIC OBJECT MARKER: applied (scenes are marked changed, not saved)" : "STATIC OBJECT MARKER: preview, nothing changed");
        AppendCounts(report, apply ? "Made static" : "Would be made static", marked);
        AppendCounts(report, "Already static, left as is", unchanged);

        report.AppendLine("Left non-static:");
        if (skipped.Count == 0) report.AppendLine("  (nothing)");
        foreach (KeyValuePair<string, List<string>> entry in skipped)
        {
            report.AppendLine($"  {entry.Value.Count,5}  {entry.Key}");
            for (int i = 0; i < entry.Value.Count && i < MaxNamesListed; i++) report.AppendLine("           " + entry.Value[i]);
            if (entry.Value.Count > MaxNamesListed) report.AppendLine("           ...");
        }

        if (lightProbeOnly.Count > 0)
            report.AppendLine($"Receive GI set to Light Probes (their shader does not read lightmaps): {lightProbeOnly.Count} objects, e.g. {lightProbeOnly[0]}");
        if (noLightmapUv.Count > 0)
        {
            report.AppendLine($"WARNING: {noLightmapUv.Count} static objects get lightmaps but their mesh has no lightmap UVs (UV1). " +
                              "Tick Generate Lightmap UVs on the model's import settings, or the bake uses the normal UVs and looks wrong:");
            for (int i = 0; i < noLightmapUv.Count && i < MaxNamesListed; i++) report.AppendLine("  " + noLightmapUv[i]);
        }

        AppendLights(report);
        return report.ToString();
    }

    /// <summary>Null when the renderer can be static, else the reason it must stay non-static.</summary>
    static string WhyNot(MeshRenderer r)
    {
        GameObject go = r.gameObject;
        if ((go.hideFlags & (HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild)) != 0) return "not saved with the scene (generated preview)";

        MeshFilter filter = r.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return "no mesh";

        for (Transform t = go.transform; t != null; t = t.parent)
        {
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                for (Type type = c.GetType(); type != null; type = type.BaseType)
                    if (MovingTypes.Contains(type.Name))
                        return $"can move ({type.Name}{(t == go.transform ? "" : " on a parent")})";
            }
        }

        foreach (MonoBehaviour b in go.GetComponents<MonoBehaviour>())
        {
            if (b == null) return "has a missing script";
            Type type = b.GetType();
            if (IsEngineType(type) || HarmlessScripts.Contains(type.Name)) continue;
            return $"has a script of the game ({type.Name})";
        }

        Material[] materials = r.sharedMaterials;
        if (materials.Length == 0) return "no material";
        foreach (Material m in materials)
        {
            if (m == null) return "no material";
            if (m.renderQueue >= TransparentQueue) return "transparent or additive material";
        }
        return null;
    }

    static bool IsEngineType(Type t)
    {
        string ns = t.Namespace;
        return ns != null && (ns.StartsWith("UnityEngine") || ns.StartsWith("UnityEditor") || ns.StartsWith("Unity.") || ns.StartsWith("TMPro"));
    }

    // True if every material's shader can show a baked lightmap. URP and package shaders can; a shader of the game is checked by
    // looking for the lightmap macros in its source (and the files it includes).
    static bool ReadsLightmaps(Material[] materials)
    {
        foreach (Material m in materials)
        {
            Shader shader = m.shader;
            if (!lightmapAware.TryGetValue(shader, out bool aware))
                lightmapAware[shader] = aware = ShaderReadsLightmaps(shader);
            if (!aware) return false;
        }
        return true;
    }

    static bool ShaderReadsLightmaps(Shader shader)
    {
        string path = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) return true;
        if (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)) return true;      // Shader Graph: the URP lit templates read them

        string source = File.ReadAllText(path);
        string folder = Path.GetDirectoryName(path);
        foreach (Match include in Regex.Matches(source, "#include\\s+\"([^\"]+)\""))
        {
            string file = Path.Combine(folder, include.Groups[1].Value);
            if (File.Exists(file)) source += File.ReadAllText(file);
        }
        return source.Contains("LIGHTMAP") || source.Contains("SAMPLE_GI") || source.Contains("lightmap");
    }

    static void AppendLights(StringBuilder report)
    {
        var modes = new SortedDictionary<string, int>();
        foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!light.gameObject.scene.isLoaded) continue;
            string key = $"{light.gameObject.scene.name}: {light.type} light, Mode {light.lightmapBakeType}";
            modes.TryGetValue(key, out int n);
            modes[key] = n + 1;
        }
        report.AppendLine("Lights (only Baked or Mixed lights go into a bake; Realtime ones are ignored by it):");
        if (modes.Count == 0) report.AppendLine("  (none)");
        foreach (KeyValuePair<string, int> m in modes) report.AppendLine($"  {m.Value,5}  {m.Key}");
    }

    static void AppendCounts(StringBuilder report, string title, SortedDictionary<string, int> counts)
    {
        int total = 0;
        foreach (int n in counts.Values) total += n;
        report.AppendLine($"{title}: {total}");
        foreach (KeyValuePair<string, int> e in counts) report.AppendLine($"  {e.Value,5}  {e.Key}");
    }

    static void Count(SortedDictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out int n);
        counts[key] = n + 1;
    }

    static string PathOf(Transform t)
    {
        string path = t.name;
        for (t = t.parent; t != null; t = t.parent) path = t.name + "/" + path;
        return path;
    }

    // "Forest/Terrain/Chunk_05_07" and "Chunk_05_08" count as one line: Chunk_#_#
    static string Pattern(string path)
    {
        return Regex.Replace(path, "\\d+", "#");
    }
}
