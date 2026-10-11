using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HavocForest.EditorTools
{
    [CustomEditor(typeof(ForestGenerator))]
    public class ForestGeneratorEditor : Editor
    {
        Editor settingsEditor;
        Editor layersEditor;
        static string lastNavMeshReport;
        static bool showSettings = true;
        static bool showLayers;
        static bool showIssues = true;

        public override void OnInspectorGUI()
        {
            var gen = (ForestGenerator)target;

            // ---- actions
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = gen.settings != null;
            if (GUILayout.Button("Generate", GUILayout.Height(28))) Run(gen, () => gen.Generate());
            if (GUILayout.Button("New Seed", GUILayout.Height(28), GUILayout.Width(80))) Run(gen, () => gen.GenerateNewSeed());
            GUI.enabled = true;
            if (GUILayout.Button("Clear", GUILayout.Height(28), GUILayout.Width(60)))
            {
                if (EditorUtility.DisplayDialog("Clear forest", "Remove the generated forest and delete its saved files?", "Clear", "Cancel"))
                    gen.Clear();
            }
            EditorGUILayout.EndHorizontal();

            // ---- what was built
            ForestData data = gen.World != null ? gen.World.data : null;
            if (data != null)
            {
                ForestStats st = data.stats;
                EditorGUILayout.HelpBox(
                    "Seed " + st.seed + "   " + st.seconds.ToString("0.0") + " s   hash " + data.contentHash + "\n" +
                    "Terrain: " + st.chunks + " chunks, " + st.triangles.ToString("N0") + " triangles\n" +
                    "Trails: " + st.trailMetres.ToString("0") + " m (" + st.monsterTrailMetres.ToString("0") + " m monster-wide), " +
                    (st.clearings - 1) + " clearings + trailhead, " + st.markers + " markers" +
                    (st.instances > 0 ? "\nPlaced objects: " + st.instances.ToString("N0") : ""),
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox("No forest generated yet.", MessageType.None);
            }

            // ---- problems
            List<ForestIssue> issues = ForestValidator.Validate(gen);
            if (issues.Count > 0)
            {
                showIssues = EditorGUILayout.Foldout(showIssues, "Checks (" + issues.Count + ")", true);
                if (showIssues)
                    foreach (ForestIssue issue in issues) EditorGUILayout.HelpBox(issue.text, issue.type);
            }

            EditorGUILayout.Space(4);
            DrawDefaultInspector();

            // ---- the settings assets, inline
            if (gen.settings != null)
            {
                EditorGUILayout.Space(6);
                showSettings = EditorGUILayout.InspectorTitlebar(showSettings, gen.settings);
                if (showSettings)
                {
                    CreateCachedEditor(gen.settings, null, ref settingsEditor);
                    EditorGUI.indentLevel++;
                    settingsEditor.OnInspectorGUI();
                    EditorGUI.indentLevel--;
                }
            }
            if (gen.groundLayers != null)
            {
                EditorGUILayout.Space(6);
                showLayers = EditorGUILayout.InspectorTitlebar(showLayers, gen.groundLayers);
                if (showLayers)
                {
                    CreateCachedEditor(gen.groundLayers, null, ref layersEditor);
                    EditorGUI.indentLevel++;
                    layersEditor.OnInspectorGUI();
                    EditorGUI.indentLevel--;
                }
            }

            // ---- tools
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = gen.groundLayers != null;
            if (GUILayout.Button("Build Ground Texture Arrays"))
            {
                string msg;
                bool ok = ForestTextureArrays.Build(gen.groundLayers, out msg);
                if (ok) Debug.Log("Havoc Forest: " + msg, gen.groundLayers);
                else Debug.LogError("Havoc Forest: " + msg, gen.groundLayers);
            }
            GUI.enabled = data != null;
            if (GUILayout.Button("Show Saved Files") && data != null)
            {
                Selection.activeObject = data;
                EditorGUIUtility.PingObject(data);
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            GUI.enabled = data != null && !Application.isPlaying;
            if (GUILayout.Button(new GUIContent("Bake Monster NavMesh", "Creates or updates the 'Forest Monster' agent type from the Monster settings and bakes a NavMesh for it, then checks every clearing and monster gap can be reached.")))
            {
                try
                {
                    EditorUtility.DisplayProgressBar("Havoc Forest", "Baking the monster NavMesh...", 0.5f);
                    lastNavMeshReport = ForestNavMeshBaker.Bake(gen);
                    Debug.Log("Havoc Forest: " + lastNavMeshReport, gen);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(lastNavMeshReport)) EditorGUILayout.HelpBox(lastNavMeshReport, MessageType.None);

            string scenePath = gen.gameObject.scene.path;
            bool inBuild = false;
            foreach (EditorBuildSettingsScene sc in EditorBuildSettings.scenes) if (sc.path == scenePath && sc.enabled) inBuild = true;
            if (!string.IsNullOrEmpty(scenePath) && !inBuild && GUILayout.Button("Add This Scene To Build Settings"))
                ForestSetup.AddToBuildSettings(scenePath);
        }

        static void Run(ForestGenerator gen, System.Action action)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Havoc Forest", "Generating the forest...", 0.3f);
                action();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void OnDisable()
        {
            if (settingsEditor != null) DestroyImmediate(settingsEditor);
            if (layersEditor != null) DestroyImmediate(layersEditor);
        }
    }

    [CustomEditor(typeof(ForestGroundLayers))]
    public class ForestGroundLayersEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Build Texture Arrays", GUILayout.Height(24)))
            {
                string msg;
                bool ok = ForestTextureArrays.Build((ForestGroundLayers)target, out msg);
                if (ok) Debug.Log("Havoc Forest: " + msg, target);
                else Debug.LogError("Havoc Forest: " + msg, target);
            }
            EditorGUILayout.HelpBox("Changing tiling, tint, smoothness or normal strength applies right away. After changing textures or the texture size, press Build Texture Arrays.", MessageType.None);
        }
    }
}
