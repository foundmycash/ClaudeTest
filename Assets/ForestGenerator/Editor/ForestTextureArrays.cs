using System.IO;
using UnityEditor;
using UnityEngine;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Packs the 8 ground surfaces of a Ground Layers asset into two texture arrays next to it:
    ///   ..._ColourHeight.png  colour in rgb, height in alpha (the height map if given, else the colour's alpha if it has
    ///                         one, else its brightness)
    ///   ..._Normal.png        normals as xyz * 0.5 + 0.5 (flat where a surface has no normal map)
    /// They are written as vertical strips of 8 tiles and imported as Texture2DArray, so Unity compresses them per
    /// platform (ASTC on Quest) and makes the mipmaps. Any source size or format works; every tile is resized to the
    /// asset's Texture Size.
    /// </summary>
    public static class ForestTextureArrays
    {
        public static bool Build(ForestGroundLayers layers, out string message)
        {
            message = "";
            if (layers == null || layers.layers == null || layers.layers.Length < ForestGroundLayers.LayerCount)
            {
                message = "The Ground Layers asset needs 8 layers.";
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(layers);
            string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string baseName = Path.GetFileNameWithoutExtension(assetPath);
            string albedoPath = dir + "/" + baseName + "_ColourHeight.png";
            string normalPath = dir + "/" + baseName + "_Normal.png";

            int s = (int)layers.textureSize;
            int n = ForestGroundLayers.LayerCount;
            var albedoStrip = new Texture2D(s, s * n, TextureFormat.RGBA32, false);
            var normalStrip = new Texture2D(s, s * n, TextureFormat.RGBA32, false, true);
            Material unpack = new Material(Shader.Find("Hidden/HavocForest/UnpackNormal"));
            int missing = 0;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    ForestGroundLayer layer = layers.layers[i];
                    int row = (n - 1 - i) * s;          // slice 0 is the top tile of the strip

                    Color32[] albedo = layer.albedo != null ? Read(layer.albedo, s, true, null) : Fill(s, new Color32(128, 110, 90, 128));
                    if (layer.albedo == null) missing++;
                    if (layer.height != null)
                    {
                        Color32[] h = Read(layer.height, s, IsSRGB(layer.height), null);   // keep the file's own values
                        for (int k = 0; k < albedo.Length; k++) albedo[k].a = h[k].r;
                    }
                    else if (!HasAlpha(layer.albedo))
                    {
                        for (int k = 0; k < albedo.Length; k++)
                            albedo[k].a = (byte)Mathf.Clamp((albedo[k].r * 77 + albedo[k].g * 150 + albedo[k].b * 29) >> 8, 0, 255);
                    }
                    albedoStrip.SetPixels32(0, row, s, s, albedo);

                    Color32[] normal = layer.normal != null ? Read(layer.normal, s, false, unpack) : Fill(s, new Color32(128, 128, 255, 255));
                    normalStrip.SetPixels32(0, row, s, s, normal);
                }

                File.WriteAllBytes(albedoPath, albedoStrip.EncodeToPNG());
                File.WriteAllBytes(normalPath, normalStrip.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(albedoStrip);
                Object.DestroyImmediate(normalStrip);
                Object.DestroyImmediate(unpack);
            }

            AssetDatabase.ImportAsset(albedoPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(normalPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureArray(albedoPath, true, n, s, TextureImporterFormat.ASTC_6x6);
            ConfigureArray(normalPath, false, n, s, TextureImporterFormat.ASTC_5x5);

            layers.albedoArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(albedoPath);
            layers.normalArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(normalPath);
            if (layers.material != null)
            {
                layers.ApplyTo(layers.material);
                EditorUtility.SetDirty(layers.material);
            }
            EditorUtility.SetDirty(layers);
            AssetDatabase.SaveAssets();

            message = "Built two " + s + " x " + s + " x " + n + " texture arrays." + (missing > 0 ? " " + missing + " surface(s) have no colour texture (grey placeholder used)." : "");
            return layers.albedoArray != null && layers.normalArray != null;
        }

        static Color32[] Fill(int s, Color32 c)
        {
            var px = new Color32[s * s];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            return px;
        }

        static bool IsSRGB(Texture2D tex)
        {
            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            return imp == null || imp.sRGBTexture;
        }

        static bool HasAlpha(Texture2D tex)
        {
            if (tex == null) return false;
            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (imp != null) return imp.DoesSourceTextureHaveAlpha() && imp.alphaSource != TextureImporterAlphaSource.None;
            return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat);
        }

        /// <summary>Reads any texture (compressed, not readable, any size) at the given size through the GPU.</summary>
        static Color32[] Read(Texture tex, int s, bool srgb, Material blitMaterial)
        {
            RenderTexture rt = RenderTexture.GetTemporary(s, s, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            var readback = new Texture2D(s, s, TextureFormat.RGBA32, false, !srgb);
            try
            {
                if (blitMaterial != null) Graphics.Blit(tex, rt, blitMaterial);
                else Graphics.Blit(tex, rt);
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

        static void ConfigureArray(string path, bool srgb, int slices, int sliceSize, TextureImporterFormat androidFormat)
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
            imp.sRGBTexture = srgb;
            imp.alphaSource = TextureImporterAlphaSource.FromInput;
            imp.alphaIsTransparency = false;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = FilterMode.Trilinear;
            imp.anisoLevel = 2;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.maxTextureSize = Mathf.Min(16384, Mathf.NextPowerOfTwo(sliceSize * slices));
            imp.textureCompression = TextureImporterCompression.CompressedHQ;

            TextureImporterPlatformSettings android = imp.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.format = androidFormat;
            android.maxTextureSize = imp.maxTextureSize;
            imp.SetPlatformTextureSettings(android);
            imp.SaveAndReimport();
        }
    }
}
