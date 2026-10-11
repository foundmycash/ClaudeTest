using UnityEditor;
using UnityEngine;

namespace HavocCave.EditorTools
{
    [CustomEditor(typeof(CaveGenerator))]
    public class CaveGeneratorEditor : UnityEditor.Editor
    {
        CavePreviewPanel preview;   // the live preview
        CavePreset presetChoice = CavePreset.Experimental;

        void OnEnable()
        {
            preview = new CavePreviewPanel((CaveGenerator)target, Repaint);
        }

        void OnDisable()
        {
            if (preview != null) preview.Dispose();
            preview = null;
        }

        readonly CaveSpawnHandles spawnHandles = new CaveSpawnHandles();

        void OnSceneGUI()
        {
            if (preview != null) preview.DrawScene();
            spawnHandles.Draw((CaveGenerator)target);
        }

        public override void OnInspectorGUI()
        {
            var gen = (CaveGenerator)target;

            // ---- preview: see the cave before generating it
            if (preview != null) preview.DrawInspector();
            EditorGUILayout.Space(2f);

            // ---- seed row
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            int newSeed = EditorGUILayout.IntField("Seed", gen.seed);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(gen, "Change Cave Seed");
                gen.seed = newSeed;
                EditorUtility.SetDirty(gen);
            }
            if (GUILayout.Button(new GUIContent("<", "Previous seed"), GUILayout.Width(24)))
            {
                Undo.RecordObject(gen, "Previous Cave Seed");
                gen.seed = Mathf.Max(1, gen.seed - 1);
                EditorUtility.SetDirty(gen);
            }
            if (GUILayout.Button(new GUIContent(">", "Next seed"), GUILayout.Width(24)))
            {
                Undo.RecordObject(gen, "Next Cave Seed");
                gen.seed = gen.seed == int.MaxValue ? 1 : gen.seed + 1;
                EditorUtility.SetDirty(gen);
            }
            if (GUILayout.Button(new GUIContent("Random", "A random seed. The preview above shows what it looks like."), GUILayout.Width(64)))
            {
                Undo.RecordObject(gen, "Randomize Cave Seed");
                gen.seed = CaveGenerator.NewRandomSeed();
                EditorUtility.SetDirty(gen);
            }
            EditorGUILayout.EndHorizontal();

            // ---- action buttons
            using (new EditorGUI.DisabledScope(gen.IsGenerating))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Generate", GUILayout.Height(30)))
                    Run(gen, "Generate Cave", () => gen.Generate());
                if (GUILayout.Button("New Seed + Generate", GUILayout.Height(30)))
                    Run(gen, "Generate Cave (new seed)", () => gen.GenerateNewSeed());
                if (GUILayout.Button("Clear", GUILayout.Height(30)))
                    Run(gen, "Clear Cave", () => gen.Clear());
                EditorGUILayout.EndHorizontal();
            }

            // ---- presets
            EditorGUILayout.BeginHorizontal();
            presetChoice = (CavePreset)EditorGUILayout.EnumPopup(new GUIContent("Style Preset", "Smooth: one winding tunnel and a few round rooms. Experimental: levels, loops, shafts, halls, cathedrals, pillars. Wild: all of that, turned up."), presetChoice);
            if (GUILayout.Button(new GUIContent("Apply", "Loads this preset into Quick Settings and Cave Settings. Your seed, materials and props are kept."), GUILayout.Width(60)))
            {
                Undo.RecordObject(gen, "Apply Cave Preset");
                gen.ApplyPreset(presetChoice);
                EditorUtility.SetDirty(gen);
            }
            EditorGUILayout.EndHorizontal();

            // ---- spawn cave
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Spawn Cave (same shape for every seed)", EditorStyles.boldLabel);
            bool editing = EditorGUILayout.ToggleLeft(new GUIContent("Edit in Scene view", "Show the handles for the spawn cave in the Scene view while this object is selected."), CaveSpawnHandles.Editing);
            if (editing != CaveSpawnHandles.Editing) { CaveSpawnHandles.Editing = editing; SceneView.RepaintAll(); }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Add Hole", "A new hole in the wall facing the Scene view camera. Tick 'Leads To Cave' on it to start a tunnel there."))) spawnHandles.AddHole(gen);
            if (GUILayout.Button(new GUIContent("Add Ledge", "A single block of rock to stand on."))) spawnHandles.AddLedge(gen, 1);
            if (GUILayout.Button(new GUIContent("Add Steps", "A flight of four steps."))) spawnHandles.AddLedge(gen, 4);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Remove Selected", "Removes the hole or ledge you picked in the Scene view."))) spawnHandles.RemoveSelected(gen);
            if (GUILayout.Button(new GUIContent("Reset Spawn Cave", "Back to the default spawn cave.")))
            {
                Undo.RecordObject(gen, "Reset Spawn Cave");
                gen.ResetSpawnCave();
                EditorUtility.SetDirty(gen);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("In the Scene view: drag the squares on the cyan outline to resize the room. Click a ring (hole) or a green box (ledge) to pick it, then drag its handles. Holes with 'Leads To Cave' ticked are where the generated tunnels start. Press Generate to see the result at full detail.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();

            CaveStats s = gen.LastStats;
            if (s.chunks > 0)
            {
                EditorGUILayout.HelpBox(
                    string.Format("Seed {0}   {1:N0} triangles   {2:0} m of tunnel   voxel {3:0.00} m\n{4} chunks   {5} props   built in {6:0.00}s",
                        s.seed, s.triangles, s.tunnelMeters, s.voxelSize, s.chunks, s.props, s.seconds), MessageType.None);
            }
            if (s.chunks > 0 && s.lightmapWidth > 0)
            {
                EditorGUILayout.HelpBox(
                    string.Format("Lightmap {0} x {1}   {2:N0} texels ({3:0.#} per metre)   {4} charts   {5} lights   baked in {6:0.0}s",
                        s.lightmapWidth, s.lightmapHeight, s.lightmapTexels, s.lightmapTexelsPerMeter, s.lightmapCharts, s.bakedLights, s.bakeSeconds),
                    MessageType.None);
            }
            else if (s.chunks > 0 && gen.lightmap != null && gen.lightmap.enabled)
            {
                EditorGUILayout.HelpBox(
                    "No lightmap was baked for this cave: there were no lamps or Cave Baked Lights to bake, 'Bake At Runtime' is off, or the bake failed (see the Console).",
                    MessageType.Info);
            }
            if (gen.IsBaking)
            {
                Rect bar = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(bar, gen.BakeProgress, "Baking light: " + gen.BakeStage);
            }
            if (s.chunks > 0 && s.polyCountLimited)
            {
                EditorGUILayout.HelpBox(
                    "Poly Count is lower than this cave can use. Below this the tunnels turn into big flat facets and the textures smear, " +
                    "so it was stopped at the lowest that still looks right. Make the cave smaller (Cave Size) to go lower.",
                    MessageType.Info);
            }
            if (s.chunks == 0 && !Application.isPlaying)
            {
                int saved = gen.SavedChunkCount;
                EditorGUILayout.HelpBox(
                    saved > 0
                        ? "A cave is saved in this scene (" + saved + " chunks). It is still here when you reopen the scene or Unity."
                        : (gen.saveWithScene
                            ? "Press Generate. The cave is then saved with the scene (meshes in Assets/CaveGeneratorSaved) and the scene is saved for you."
                            : "Save With Scene is off, so an edit-mode cave is only a preview. With 'Generate On Start' on, the same seed rebuilds the same cave when you press Play."),
                    MessageType.None);
            }

            EditorGUILayout.Space();

            // ---- everything else
            serializedObject.Update();
            SerializedProperty it = serializedObject.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                if (it.propertyPath == "m_Script" || it.propertyPath == "seed") continue;

                if (it.propertyPath == "generatedLayer")
                {
                    it.intValue = EditorGUILayout.LayerField(new GUIContent(it.displayName, it.tooltip), it.intValue);
                    continue;
                }

                if (it.propertyPath == "polyCount")
                {
                    using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("usePolyCount").boolValue))
                        PolyCountSlider(it);
                    continue;
                }
                EditorGUILayout.PropertyField(it, true);
            }
            serializedObject.ApplyModifiedProperties();

            if (gen.IsGenerating || gen.IsBaking) Repaint();
        }

        /// <summary>A log-scale slider (1k to 500k) with a number box, because a linear slider is useless across that range.</summary>
        static void PolyCountSlider(SerializedProperty prop)
        {
            const float min = 1000f;
            const float max = 500000f;
            float logMin = Mathf.Log10(min);
            float logMax = Mathf.Log10(max);

            Rect row = EditorGUILayout.GetControlRect();
            row = EditorGUI.PrefixLabel(row, new GUIContent(prop.displayName, prop.tooltip));
            var slider = new Rect(row.x, row.y, row.width - 76f, row.height);
            var box = new Rect(row.xMax - 70f, row.y, 70f, row.height);

            float value = Mathf.Max(min, prop.floatValue);
            float t = Mathf.InverseLerp(logMin, logMax, Mathf.Log10(value));

            EditorGUI.BeginChangeCheck();
            float newT = GUI.HorizontalSlider(slider, t, 0f, 1f);
            bool slid = EditorGUI.EndChangeCheck();
            if (slid)
            {
                float v = Mathf.Pow(10f, Mathf.Lerp(logMin, logMax, newT));
                float step = v < 10000f ? 100f : (v < 100000f ? 1000f : 5000f);
                prop.floatValue = Mathf.Max(min, Mathf.Round(v / step) * step);
            }

            EditorGUI.BeginChangeCheck();
            float typed = EditorGUI.FloatField(box, prop.floatValue);
            if (EditorGUI.EndChangeCheck()) prop.floatValue = Mathf.Max(min, typed);
        }

        static void Run(CaveGenerator gen, string undoName, System.Action action)
        {
            Undo.RecordObject(gen, undoName);
            try
            {
                EditorUtility.DisplayProgressBar("Cave Generator", "Working...", 0.5f);
                action();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            EditorUtility.SetDirty(gen);
            SceneView.RepaintAll();
        }

        [MenuItem("GameObject/Havoc/Cave Generator", false, 10)]
        static void CreateCaveGenerator(MenuCommand command)
        {
            var go = new GameObject("Cave Generator");
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            go.AddComponent<CaveGenerator>();   // Reset() fills in the default materials
            Undo.RegisterCreatedObjectUndo(go, "Create Cave Generator");
            Selection.activeObject = go;
        }
    }
}
