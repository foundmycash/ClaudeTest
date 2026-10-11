#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Editor only: saves a generated forest as files next to the scene, overwriting the previous files in place so every
    /// reference to them (from the scene, from materials) stays valid. Files that are no longer needed are deleted.
    ///   ForestData.asset   heights, layout, markers and the terrain meshes (binary)
    ///   Control0/1.png     the ground blend maps (uncompressed)
    ///   LightMap.png       the ground light map
    /// </summary>
    public static class ForestSaving
    {
        public const string SavedRoot = "Assets/ForestGeneratorSaved";
        const string DataFile = "ForestData.asset";
        const string ChunkMeshPrefix = "Forest_Chunk_";

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Makes sure the save folder exists. Call before saving anything into it.</summary>
        public static void PrepareFolder(ForestGenerator gen)
        {
            string folder = gen.SaveFolder;
            // The scene was renamed (or first saved) since the last generate: move the old files instead of starting over,
            // so their GUIDs, and every reference to them, stay the same.
            string previous = gen.LastSaveFolder;
            if (string.IsNullOrEmpty(previous) && gen.World != null && gen.World.data != null)
            {
                string dataPath = AssetDatabase.GetAssetPath(gen.World.data);
                if (dataPath.StartsWith(SavedRoot + "/")) previous = Path.GetDirectoryName(dataPath).Replace('\\', '/');
            }
            if (!string.IsNullOrEmpty(previous) && previous != folder && AssetDatabase.IsValidFolder(previous) && !AssetDatabase.IsValidFolder(folder))
            {
                EnsureFolder(Path.GetDirectoryName(folder).Replace('\\', '/'));
                string error = AssetDatabase.MoveAsset(previous, folder);
                if (!string.IsNullOrEmpty(error)) Debug.LogWarning("ForestGenerator: could not move " + previous + " to " + folder + ": " + error, gen);
            }
            EnsureFolder(folder);
            gen.LastSaveFolder = folder;
        }

        /// <summary>
        /// The forest data asset, marked as changed: the editor never unloads a changed asset, so what is put in it stays
        /// until it is saved, even if files are imported in between. Fill it straight after this and save with Commit.
        /// </summary>
        public static ForestData LoadOrCreateData(ForestGenerator gen)
        {
            PrepareFolder(gen);
            string path = gen.SaveFolder + "/" + DataFile;
            ForestData data = AssetDatabase.LoadAssetAtPath<ForestData>(path);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<ForestData>();
                data.name = "ForestData";
                AssetDatabase.CreateAsset(data, path);
                ForestData created = AssetDatabase.LoadAssetAtPath<ForestData>(path);    // the live copy, in case the import replaced it
                if (created != null) data = created;
            }
            EditorUtility.SetDirty(data);
            return data;
        }

        /// <summary>Writes a map as a PNG and returns the imported texture. The in-memory texture is destroyed.</summary>
        public static Texture2D SaveMap(ForestGenerator gen, Texture2D tex, string name, bool compressOnAndroid)
        {
            string folder = gen.SaveFolder;
            EnsureFolder(folder);
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            int size = tex.width;
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null && ConfigureImporter(importer, size, compressOnAndroid)) importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Linear data, clamped, mipmapped, uncompressed (the blend weights must stay exact). True if anything changed.</summary>
        static bool ConfigureImporter(TextureImporter imp, int size, bool compressOnAndroid)
        {
            bool changed = false;
            if (imp.textureType != TextureImporterType.Default) { imp.textureType = TextureImporterType.Default; changed = true; }
            if (imp.sRGBTexture) { imp.sRGBTexture = false; changed = true; }
            if (imp.alphaSource != TextureImporterAlphaSource.FromInput) { imp.alphaSource = TextureImporterAlphaSource.FromInput; changed = true; }
            if (imp.alphaIsTransparency) { imp.alphaIsTransparency = false; changed = true; }
            if (!imp.mipmapEnabled) { imp.mipmapEnabled = true; changed = true; }
            if (imp.wrapMode != TextureWrapMode.Clamp) { imp.wrapMode = TextureWrapMode.Clamp; changed = true; }
            if (imp.filterMode != FilterMode.Bilinear) { imp.filterMode = FilterMode.Bilinear; changed = true; }
            if (imp.npotScale != TextureImporterNPOTScale.None) { imp.npotScale = TextureImporterNPOTScale.None; changed = true; }
            if (imp.isReadable) { imp.isReadable = false; changed = true; }
            if (imp.textureCompression != TextureImporterCompression.Uncompressed) { imp.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
            if (imp.maxTextureSize < size) { imp.maxTextureSize = Mathf.NextPowerOfTwo(size); changed = true; }

            TextureImporterPlatformSettings android = imp.GetPlatformTextureSettings("Android");
            TextureImporterFormat want = compressOnAndroid ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.RGBA32;
            if (!android.overridden || android.format != want || android.maxTextureSize < size)
            {
                android.overridden = true;
                android.format = want;
                android.maxTextureSize = Mathf.Max(android.maxTextureSize, Mathf.NextPowerOfTwo(size));
                imp.SetPlatformTextureSettings(android);
                changed = true;
            }
            return changed;
        }

        /// <summary>Stores the chunk meshes inside ForestData.asset, reusing the existing sub-assets by name.</summary>
        public static List<Mesh> SaveMeshes(ForestGenerator gen, ForestData data, List<Mesh> meshes)
        {
            string path = AssetDatabase.GetAssetPath(data);
            var existing = new Dictionary<string, Mesh>();
            foreach (Object o in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                var m = o as Mesh;
                if (m != null && m.name.StartsWith(ChunkMeshPrefix)) existing[m.name] = m;
            }

            var result = new List<Mesh>(meshes.Count);
            var used = new HashSet<string>();
            foreach (Mesh mesh in meshes)
            {
                used.Add(mesh.name);
                Mesh target;
                if (existing.TryGetValue(mesh.name, out target))
                {
                    EditorUtility.CopySerialized(mesh, target);
                    target.hideFlags = HideFlags.None;     // the in-memory mesh was DontSave; the saved one must not be
                    Object.DestroyImmediate(mesh);
                }
                else
                {
                    target = mesh;
                    target.hideFlags = HideFlags.None;
                    AssetDatabase.AddObjectToAsset(target, data);
                }
                result.Add(target);
            }
            foreach (var kv in existing)
            {
                if (used.Contains(kv.Key)) continue;
                AssetDatabase.RemoveObjectFromAsset(kv.Value);
                Object.DestroyImmediate(kv.Value, true);
            }
            return result;
        }

        public static void Commit(ForestGenerator gen, ForestData data)
        {
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        public static void DeleteSaved(ForestGenerator gen)
        {
            string folder = gen.SaveFolder;
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            string previous = gen.LastSaveFolder;
            if (!string.IsNullOrEmpty(previous) && previous != folder && AssetDatabase.IsValidFolder(previous)) AssetDatabase.DeleteAsset(previous);
            gen.LastSaveFolder = "";
        }
    }
}
#endif
