using System.IO;
using UnityEditor;
using UnityEngine;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Surface textures for the generated models (bark, needles, leaves, stone...).
    ///   Tools > Havoc Forest > Surface Textures > Add To The Models   paints the placeholder textures, builds the texture array,
    ///       switches it on in the shared vegetation material and rebuilds the placeholder models so they carry texture
    ///       coordinates (same shapes as before; the ground is not touched).
    ///   ... > Turn Off / Turn On         the plain vertex-colour look is one click away.
    ///   ... > Repaint Placeholder Textures, Rebuild Placeholder Models.
    /// The textures are packed into ForestSurfaceLayers_Detail.png (a vertical strip imported as a Texture2DArray, ASTC on
    /// Quest). Each one is reduced to its pattern (divided by its own average colour, stored around 0.5 and doubled again
    /// in the shader) so it adds grain and variation without changing the colours the models were designed with.
    /// </summary>
    public static class ForestSurfaceArrays
    {
        public const string TextureDir = ForestSetup.TexturesDir + "/Surfaces";
        public const string LayersPath = ForestSetup.SettingsDir + "/ForestSurfaceLayers.asset";
        const string VegetationPath = ForestSetup.MaterialsDir + "/ForestVegetation.mat";
        const string GlassPath = ForestSetup.MaterialsDir + "/ForestLampGlass.mat";
        const string GlowPath = ForestSetup.MaterialsDir + "/ForestLampGlow.mat";

        // ---------------------------------------------------------------- menu

        [MenuItem("Tools/Havoc Forest/Surface Textures/Add To The Models", priority = 40)]
        static void AddMenu()
        {
            if (!EditorUtility.DisplayDialog("Havoc Forest",
                    "Add surface textures (bark, needles, leaves, stone...) to the placeholder models?\n\n" +
                    "This paints the textures, builds the texture array, switches it on in the shared vegetation material and rebuilds " +
                    "the placeholder model meshes and prefabs so they have texture coordinates. The shapes stay the same; the ground " +
                    "is not touched. 'Turn Off' in the same menu goes back to plain vertex colours.\n\n" +
                    "Save your scene first if it has unsaved changes.", "Add Textures", "Cancel")) return;
            Report(SetUp(false, true) + LookHere);
        }

        [MenuItem("Tools/Havoc Forest/Surface Textures/Turn Off (plain vertex colours)", priority = 41)]
        static void TurnOffMenu()
        {
            SetEnabled(false);
        }

        [MenuItem("Tools/Havoc Forest/Surface Textures/Turn On", priority = 42)]
        static void TurnOnMenu()
        {
            SetEnabled(true);
        }

        [MenuItem("Tools/Havoc Forest/Surface Textures/Repaint Placeholder Textures", priority = 50)]
        static void RepaintMenu()
        {
            Report("Repainted the placeholder surface textures and rebuilt the texture array. " + SetUp(true, false) + LookHere);
        }

        [MenuItem("Tools/Havoc Forest/Surface Textures/Rebuild Placeholder Models", priority = 51)]
        static void RebuildModelsMenu()
        {
            int count = RebuildPlaceholderModels();
            Report("Rebuilt " + count + " placeholder models (meshes and prefabs, in place)." + LookHere +
                   " If a forest in a scene looks wrong, press Generate on its Forest object and save the scene.");
        }

        [MenuItem("Tools/Havoc Forest/Surface Textures/Rebuild Streetlight Models Only", priority = 52)]
        static void RebuildStreetlightsMenu()
        {
            Material veg = AssetDatabase.LoadAssetAtPath<Material>(VegetationPath);
            Material glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            Material glow = AssetDatabase.LoadAssetAtPath<Material>(GlowPath);
            if (veg == null || glass == null || glow == null)
            {
                Report("The forest materials are missing. Use Tools > Havoc Forest > Surface Textures > Rebuild Placeholder Models instead.");
                return;
            }
            int count = ForestPlaceholderModels.CreateStreetLamps(veg, glass, glow, true).Count;
            ForestPrefabInfo.ClearCache();
            AssetDatabase.SaveAssets();
            Report("Rebuilt " + count + " streetlight models (meshes and prefabs, in place); the trees and rocks were not touched.");
        }

        const string LookHere ="\n\nWhere to see it: open the Forest scene (Assets/Scenes/Forest.unity) and look at a tree trunk, a rock or a stump. " +
                                "The models are only drawn in scenes that have a forest, so a scene without one will not change.";

        /// <summary>Tells the user what a menu item did: in the Console, and in a dialog so it is clear something happened.</summary>
        static void Report(string message)
        {
            Debug.Log("Havoc Forest: " + message);
            if (!Application.isBatchMode) EditorUtility.DisplayDialog("Havoc Forest", message, "OK");
        }

        static void SetEnabled(bool on)
        {
            var layers = AssetDatabase.LoadAssetAtPath<ForestSurfaceLayers>(LayersPath);
            if (layers == null)
            {
                Report("There are no surface textures yet. Use Tools > Havoc Forest > Surface Textures > Add To The Models first.");
                return;
            }
            if (on && !PlaceholderModelsHaveTextureCoordinates())
            {
                Report("The placeholder models have no texture coordinates yet, so the textures would tint them flat. Use Tools > Havoc Forest > Surface Textures > Add To The Models first.");
                return;
            }
            layers.useSurfaceTextures = on;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(VegetationPath);
            layers.ApplyTo(mat);
            EditorUtility.SetDirty(layers);
            if (mat != null) EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Report(on ? "Surface textures are ON: the generated models show bark, needles, leaves and stone." + LookHere
                      : "Surface textures are OFF: the generated models use their plain vertex colours again." + LookHere);
        }

        // ---------------------------------------------------------------- set up

        /// <summary>
        /// Everything in one go. repaint = paint the placeholder textures again even if the files exist; rebuildModels =
        /// rebuild the placeholder model meshes and prefabs so they get texture coordinates.
        /// </summary>
        public static string SetUp(bool repaint, bool rebuildModels)
        {
            Material veg = AssetDatabase.LoadAssetAtPath<Material>(VegetationPath);
            if (veg == null)
            {
                ForestSetup.CreateDefaultAssets(false);
                veg = AssetDatabase.LoadAssetAtPath<Material>(VegetationPath);
            }
            string rebuilt = "";
            if (rebuildModels)
            {
                RebuildPlaceholderModels();         // first: the textures are only switched on for models that can show them
                rebuilt = " The placeholder models were rebuilt with texture coordinates.";
            }
            return EnsureDefaults(veg, repaint) + rebuilt;
        }

        /// <summary>
        /// True when the placeholder models carry texture coordinates (models made before surface textures existed do not).
        /// With the surface switch on, a model without them would sample one texel for every vertex and tint the whole thing.
        /// </summary>
        public static bool PlaceholderModelsHaveTextureCoordinates()
        {
            // the meshes the prefabs really use (a mesh file can also hold old, unused copies)
            foreach (string name in new[] { "ConiferA", "RockA", "Fern", "StreetLampA" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ForestPlaceholderModels.Dir + "/" + name + ".prefab");
                if (prefab == null) return false;
                MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length == 0) return false;
                foreach (MeshFilter f in filters)
                    if (f.sharedMesh == null || !f.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)) return false;
            }
            return true;
        }

        /// <summary>Makes the placeholder textures, the layers asset and the array if they are missing, and applies them to the material.</summary>
        public static string EnsureDefaults(Material vegetation, bool repaint)
        {
            EnsureFolder(TextureDir);
            EnsureFolder(ForestSetup.SettingsDir);

            var layers = AssetDatabase.LoadAssetAtPath<ForestSurfaceLayers>(LayersPath);
            bool isNew = layers == null;
            if (isNew)
            {
                layers = ScriptableObject.CreateInstance<ForestSurfaceLayers>();
                AssetDatabase.CreateAsset(layers, LayersPath);
            }
            string held = "";
            if (!PlaceholderModelsHaveTextureCoordinates())
            {
                if (layers.useSurfaceTextures) held = " The textures are built but kept OFF: the placeholder models have no texture coordinates yet. Use Tools > Havoc Forest > Surface Textures > Add To The Models.";
                layers.useSurfaceTextures = false;
            }

            int size = (int)layers.textureSize;
            for (int i = 0; i < ForestSurfaceLayers.LayerCount; i++)
            {
                string path = PlaceholderPath(i);
                if (repaint || !File.Exists(path)) WritePlaceholder(i, path, size);
                if (layers.layers[i].albedo == null) layers.layers[i].albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            layers.material = vegetation;
            EditorUtility.SetDirty(layers);

            string msg;
            Build(layers, out msg);
            return msg + held;
        }

        public static string PlaceholderPath(int surface)
        {
            return TextureDir + "/Placeholder_" + ForestSurfacePainter.Names[surface] + ".png";
        }

        static void WritePlaceholder(int surface, string path, int size)
        {
            float[] rgb = ForestSurfacePainter.Paint(surface, size, 1000 + surface * 37);
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(rgb[i * 3] * 255f), 0, 255),
                                    (byte)Mathf.Clamp(Mathf.RoundToInt(rgb[i * 3 + 1] * 255f), 0, 255),
                                    (byte)Mathf.Clamp(Mathf.RoundToInt(rgb[i * 3 + 2] * 255f), 0, 255), 255);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(px);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null && imp.mipmapEnabled)
            {
                imp.mipmapEnabled = false;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.SaveAndReimport();
            }
        }

        // ---------------------------------------------------------------- the array

        public static bool Build(ForestSurfaceLayers layers, out string message)
        {
            message = "";
            if (layers == null || layers.layers == null || layers.layers.Length < ForestSurfaceLayers.LayerCount)
            {
                message = "The Surface Layers asset needs 8 layers.";
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(layers);
            string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string stripPath = dir + "/" + Path.GetFileNameWithoutExtension(assetPath) + "_Detail.png";
            int s = (int)layers.textureSize;
            int n = ForestSurfaceLayers.LayerCount;

            var strip = new Texture2D(s, s * n, TextureFormat.RGBA32, false, true);
            int missing = 0;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    Texture2D src = layers.layers[i].albedo;
                    Color32[] detail;
                    if (src == null)
                    {
                        detail = new Color32[s * s];
                        for (int k = 0; k < detail.Length; k++) detail[k] = new Color32(128, 128, 128, 255);   // flat = no pattern
                        missing++;
                    }
                    else
                    {
                        bool srgb = IsSRGB(src);
                        detail = ToDetail(Read(src, s, srgb), srgb);
                    }
                    strip.SetPixels32(0, (n - 1 - i) * s, s, s, detail);          // slice 0 is the top tile of the strip
                }
                File.WriteAllBytes(stripPath, strip.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(strip);
            }

            AssetDatabase.ImportAsset(stripPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureArray(stripPath, n, s);
            layers.array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(stripPath);
            if (layers.material != null)
            {
                layers.ApplyTo(layers.material);
                EditorUtility.SetDirty(layers.material);
            }
            EditorUtility.SetDirty(layers);
            AssetDatabase.SaveAssets();

            message = "Built the " + s + " x " + s + " x " + n + " surface texture array." + (missing > 0 ? " " + missing + " surface(s) have no texture and stay plain." : "");
            return layers.array != null;
        }

        /// <summary>Reduces a texture to its pattern: each channel divided by its own average, stored around 0.5 (linear).</summary>
        static Color32[] ToDetail(Color32[] px, bool srgb)
        {
            int count = px.Length;
            var lin = new float[count * 3];
            double sr = 0, sg = 0, sb = 0;
            for (int i = 0; i < count; i++)
            {
                float r = ToLinear(px[i].r, srgb), g = ToLinear(px[i].g, srgb), b = ToLinear(px[i].b, srgb);
                lin[i * 3] = r;
                lin[i * 3 + 1] = g;
                lin[i * 3 + 2] = b;
                sr += r;
                sg += g;
                sb += b;
            }
            float mr = Mathf.Max((float)(sr / count), 0.004f), mg = Mathf.Max((float)(sg / count), 0.004f), mb = Mathf.Max((float)(sb / count), 0.004f);
            var result = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                // 0.1x .. 2x the average: never pure black, and bright specks cannot blow out
                float r = Mathf.Clamp(lin[i * 3] / mr, 0.1f, 2f), g = Mathf.Clamp(lin[i * 3 + 1] / mg, 0.1f, 2f), b = Mathf.Clamp(lin[i * 3 + 2] / mb, 0.1f, 2f);
                result[i] = new Color32((byte)Mathf.RoundToInt(r * 127.5f), (byte)Mathf.RoundToInt(g * 127.5f), (byte)Mathf.RoundToInt(b * 127.5f), 255);
            }
            return result;
        }

        static float ToLinear(byte v, bool srgb)
        {
            float f = v / 255f;
            return srgb ? Mathf.GammaToLinearSpace(f) : f;
        }

        static bool IsSRGB(Texture2D tex)
        {
            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            return imp == null || imp.sRGBTexture;
        }

        /// <summary>Reads any texture (compressed, not readable, any size) at the given size through the GPU.</summary>
        static Color32[] Read(Texture tex, int s, bool srgb)
        {
            RenderTexture rt = RenderTexture.GetTemporary(s, s, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            var readback = new Texture2D(s, s, TextureFormat.RGBA32, false, !srgb);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, s, s), 0, 0, false);
                readback.Apply(false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(readback);
            }
        }

        static void ConfigureArray(string path, int slices, int sliceSize)
        {
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imp == null) return;
            imp.textureType = TextureImporterType.Default;
            var st = new TextureImporterSettings();
            imp.ReadTextureSettings(st);
            st.textureShape = TextureImporterShape.Texture2DArray;
            st.flipbookRows = slices;
            st.flipbookColumns = 1;
            imp.SetTextureSettings(st);
            imp.sRGBTexture = false;                 // the values are linear multipliers, not colours
            imp.alphaSource = TextureImporterAlphaSource.None;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = FilterMode.Trilinear;
            imp.anisoLevel = 2;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.maxTextureSize = Mathf.Min(16384, Mathf.NextPowerOfTwo(sliceSize * slices));
            imp.textureCompression = TextureImporterCompression.CompressedHQ;

            TextureImporterPlatformSettings android = imp.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.format = TextureImporterFormat.ASTC_6x6;
            android.maxTextureSize = imp.maxTextureSize;
            imp.SetPlatformTextureSettings(android);
            imp.SaveAndReimport();
        }

        // ---------------------------------------------------------------- models

        /// <summary>Rebuilds every placeholder model (meshes in place, prefabs overwritten) so they carry the current texture coordinates. Does not touch the ground. Returns how many models were rebuilt.</summary>
        public static int RebuildPlaceholderModels()
        {
            Material veg = AssetDatabase.LoadAssetAtPath<Material>(VegetationPath);
            Material glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            Material glow = AssetDatabase.LoadAssetAtPath<Material>(GlowPath);
            if (veg == null || glass == null || glow == null)
            {
                ForestSetup.CreateDefaultAssets(false);
                veg = AssetDatabase.LoadAssetAtPath<Material>(VegetationPath);
                glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
                glow = AssetDatabase.LoadAssetAtPath<Material>(GlowPath);
            }
            int count = ForestPlaceholderModels.CreateAll(veg, true).Count + ForestPlaceholderModels.CreateStreetLamps(veg, glass, glow, true).Count;
            ForestPrefabInfo.ClearCache();
            AssetDatabase.SaveAssets();
            return count;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    [CustomEditor(typeof(ForestSurfaceLayers))]
    public class ForestSurfaceLayersEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Build Texture Array", GUILayout.Height(24)))
            {
                string msg;
                bool ok = ForestSurfaceArrays.Build((ForestSurfaceLayers)target, out msg);
                if (ok) Debug.Log("Havoc Forest: " + msg, target);
                else Debug.LogError("Havoc Forest: " + msg, target);
            }
            if (GUILayout.Button("Repaint Placeholder Textures"))
            {
                string msg = ForestSurfaceArrays.SetUp(true, false);
                Debug.Log("Havoc Forest: " + msg, target);
            }
            EditorGUILayout.HelpBox("Put your own seamless textures in the slots, then press Build Texture Array. Only the pattern is used: the colours come from the models' vertex colours. Strength and the on/off switch apply right away. Changing the tile sizes (ForestSurfaceLayers.TileMetres) needs Tools > Havoc Forest > Surface Textures > Rebuild Placeholder Models.", MessageType.None);
        }
    }
}
