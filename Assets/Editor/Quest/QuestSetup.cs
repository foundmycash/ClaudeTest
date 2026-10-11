using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace QuestOptimization.Editor
{
    /// <summary>
    /// Tools > Quest > Apply Optimized Settings puts the project settings that matter for a Meta Quest build in place;
    /// Tools > Quest > Check Settings only reports them. Every step reports what it found and changed, and running it
    /// twice is harmless. The runtime side (foveation level, refresh rate) lives in QuestPerformance.
    /// </summary>
    public static class QuestSetup
    {
        const string Tag = "[QuestSetup] ";
        const BuildTargetGroup Android = BuildTargetGroup.Android;

        sealed class Step
        {
            public string name;
            public string why;
            public Func<bool> isDone;
            public Action apply;
        }

        static List<Step> Steps() => new List<Step>
        {
            new Step
            {
                name = "Android graphics API: Vulkan only",
                why = "Quest runs Vulkan. Keeping OpenGL ES as a fallback doubles shader build time and APK size, and the fallback has no foveated rendering.",
                isDone = () => !PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android)
                               && PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).SequenceEqual(new[] { GraphicsDeviceType.Vulkan }),
                apply = () =>
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                }
            },
            new Step
            {
                name = "Texture compression: ASTC",
                why = "ASTC is the format Quest GPUs read fastest.",
                isDone = () => PlayerSettings.Android.textureCompressionFormats.Length > 0
                               && PlayerSettings.Android.textureCompressionFormats[0] == TextureCompressionFormat.ASTC,
                apply = () => PlayerSettings.Android.textureCompressionFormats = new[] { TextureCompressionFormat.ASTC }
            },
            new Step
            {
                name = "OpenXR: Meta Quest Support on",
                why = "Writes the Quest boot settings into the build: Vulkan pipeline cache (fewer shader hitches), low-latency audio, memoryless MSAA, core mask.",
                isDone = () => FeatureEnabled("MetaQuestFeature"),
                apply = () => SetFeature("MetaQuestFeature", true)
            },
            new Step
            {
                name = "OpenXR: foveated rendering through Unity's SRP API",
                why = "Foveated rendering shades the edges of each eye at a lower density. The SRP API (not the older Meta one) also works when URP renders to intermediate targets, which the fog's depth texture forces.",
                isDone = () => FeatureEnabled("FoveatedRenderingFeature") && FoveationApi() == 1 && !FeatureEnabled("MetaXRFoveationFeature"),
                apply = () =>
                {
                    SetFeature("FoveatedRenderingFeature", true);
                    SetFeature("MetaXRFoveationFeature", false);
                    SetSerialized(OpenXRSettings.GetSettingsForBuildTargetGroup(Android), "m_foveatedRenderingApi", 1);
                }
            },
            new Step
            {
                name = "OpenXR: subsampled layout on, eye tracking off",
                why = "Subsampled layout lets the compositor read the foveated eye buffers directly. Eye tracking would add a permission and a Quest Pro requirement for no gain here.",
                isDone = () => SerializedBool(FindFeature("FoveatedRenderingFeature"), "enableSubsampledLayout") == true
                               && SerializedBool(FindFeature("FoveatedRenderingFeature"), "useEyeTracking") == false,
                apply = () =>
                {
                    SetSerialized(FindFeature("FoveatedRenderingFeature"), "enableSubsampledLayout", true);
                    SetSerialized(FindFeature("FoveatedRenderingFeature"), "useEyeTracking", false);
                }
            },
            new Step
            {
                name = "OpenXR: symmetric projection on (only when the original Quest is no longer a target device)",
                why = "Renders both eyes with a symmetric field of view, which needs fewer pixels for the same picture (Meta: a performance gain at every IPD). Vulkan only, which this project is. " +
                      "The build refuses it while the original Quest (2019) is ticked under OpenXR > Meta Quest Support > Target Devices, so this step leaves it off in that case: untick \"Quest\" there and run this again to get it.",
                isDone = () => (OriginalQuestIsTarget() && NoteSymmetricProjectionSkipped())
                               || (OpenXrBool(OpenXRSettings.GetSettingsForBuildTargetGroup(Android), "m_symmetricProjection") == true
                                   && OpenXrBool(FindFeature("MetaQuestFeature"), "m_symmetricProjection") == true),
                apply = () =>
                {
                    SetSerialized(OpenXRSettings.GetSettingsForBuildTargetGroup(Android), "m_symmetricProjection", true);
                    SetSerialized(FindFeature("MetaQuestFeature"), "m_symmetricProjection", true);
                }
            },
            new Step
            {
                name = "OpenXR: optimize buffer discards on",
                why = "Lets the multisampled color and depth buffers stay in tile memory and never be written to RAM (memoryless MSAA on Vulkan).",
                isDone = () => OpenXrBool(OpenXRSettings.GetSettingsForBuildTargetGroup(Android), "m_optimizeBufferDiscards") == true
                               && OpenXrBool(FindFeature("MetaQuestFeature"), "m_optimizeBufferDiscards") == true,
                apply = () =>
                {
                    SetSerialized(OpenXRSettings.GetSettingsForBuildTargetGroup(Android), "m_optimizeBufferDiscards", true);
                    SetSerialized(FindFeature("MetaQuestFeature"), "m_optimizeBufferDiscards", true);
                }
            },
            new Step
            {
                name = "OpenXR: Application SpaceWarp off",
                why = "Nothing in the game turns SpaceWarp on, so the feature only adds an unused OpenXR extension and the passes it prepares for motion vectors.",
                isDone = () => !FeatureEnabled("SpaceWarpFeature") && !FeatureEnabled("MetaXRSpaceWarp"),
                apply = () =>
                {
                    if (FindFeature("SpaceWarpFeature") != null) SetFeature("SpaceWarpFeature", false);
                    if (FindFeature("MetaXRSpaceWarp") != null) SetFeature("MetaXRSpaceWarp", false);
                }
            },
            new Step
            {
                name = "Frame timing stats on",
                why = "Lets the Performance Lab show CPU and GPU frame times in the headset. (A few timestamp queries per frame.)",
                isDone = () => PlayerSettings.enableFrameTimingStats,
                apply = () => PlayerSettings.enableFrameTimingStats = true
            },
            new Step
            {
                name = "URP (Android quality level): no reflection probe blending / box projection, no adaptive performance",
                why = "Blending two probes and projecting onto a box add math to every lit pixel; the scenes use baked probes and are lit mostly by lightmaps and lamps. Adaptive Performance has no provider here.",
                isDone = () => AndroidUrpAssets().All(a => UrpFlag(a, "m_ReflectionProbeBlending") == false
                                                           && UrpFlag(a, "m_ReflectionProbeBoxProjection") == false
                                                           && UrpFlag(a, "m_UseAdaptivePerformance") == false),
                apply = () =>
                {
                    foreach (UniversalRenderPipelineAsset asset in AndroidUrpAssets())
                    {
                        foreach (string property in new[] { "m_ReflectionProbeBlending", "m_ReflectionProbeBoxProjection", "m_UseAdaptivePerformance" })
                            SetSerialized(asset, property, false);
                    }
                }
            },
            new Step
            {
                name = "Cave textures: ASTC 6x6, 2x anisotropic",
                why = "The cave rock and dirt maps were ASTC 4x4 (8 bits per pixel, high quality) with 4x anisotropic filtering. 6x6 reads 2.2x fewer bytes and 2x aniso costs half the texture samples; the surface is rough rock, which hides both.",
                isDone = () => CaveTextures().All(p => { var i = (TextureImporter)AssetImporter.GetAtPath(p); return i.textureCompression == TextureImporterCompression.Compressed && i.anisoLevel <= 2; }),
                apply = () =>
                {
                    foreach (string path in CaveTextures())
                    {
                        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                        importer.textureCompression = TextureImporterCompression.Compressed;
                        importer.anisoLevel = Mathf.Min(importer.anisoLevel, 2);
                        importer.SaveAndReimport();
                    }
                }
            },
            new Step
            {
                name = "URP (Android quality level): switch off unused features",
                why = "Light layers, LOD cross fade and lens flares are unused by the scenes, but each one adds shader work or variants. (Light cookies stay on: the forest flashlight uses one.)",
                isDone = () => AndroidUrpAssets().All(a => UrpFlag(a, "m_SupportsLightLayers") == false
                                                           && UrpFlag(a, "m_EnableLODCrossFade") == false
                                                           && UrpFlag(a, "m_SupportDataDrivenLensFlare") == false
                                                           && UrpFlag(a, "m_SupportScreenSpaceLensFlare") == false),
                apply = () =>
                {
                    foreach (UniversalRenderPipelineAsset asset in AndroidUrpAssets())
                    {
                        foreach (string property in new[]
                                 {
                                     "m_SupportsLightLayers", "m_EnableLODCrossFade",
                                     "m_SupportDataDrivenLensFlare", "m_SupportScreenSpaceLensFlare"
                                 })
                            SetSerialized(asset, property, false);
                    }
                }
            },
        };

        [MenuItem("Tools/Quest/Apply Optimized Settings")]
        public static void Apply()
        {
            int changed = 0;
            foreach (Step step in Steps())
            {
                bool done;
                try { done = step.isDone(); }
                catch (Exception e) { Debug.LogWarning($"{Tag}{step.name}: could not check ({e.Message}), applying anyway."); done = false; }

                if (done)
                {
                    Debug.Log($"{Tag}OK       {step.name}");
                    continue;
                }

                try
                {
                    step.apply();
                    changed++;
                    Debug.Log($"{Tag}CHANGED  {step.name}\n          {step.why}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"{Tag}FAILED   {step.name}: {e}");
                }
            }

            FeatureHelpers.RefreshFeatures(Android);
            AssetDatabase.SaveAssets();
            Debug.Log($"{Tag}Done, {changed} setting group(s) changed. Use File > Save Project to also write the Project Settings to disk.");
        }

        [MenuItem("Tools/Quest/Check Settings")]
        public static void Check()
        {
            foreach (Step step in Steps())
            {
                bool done;
                try { done = step.isDone(); }
                catch (Exception e) { Debug.LogWarning($"{Tag}{step.name}: could not check ({e.Message})"); continue; }
                Debug.Log($"{Tag}{(done ? "OK      " : "TO DO   ")} {step.name}");
            }
        }

        /// <summary>For -executeMethod in batch mode.</summary>
        public static void ApplyBatch()
        {
            Apply();
            AssetDatabase.SaveAssets();
        }

        // ---- OpenXR helpers ----------------------------------------------------------------------------------------

        static OpenXRFeature FindFeature(string typeName)
        {
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(Android);
            return settings == null ? null : settings.GetFeatures<OpenXRFeature>().FirstOrDefault(f => f.GetType().Name == typeName);
        }

        static bool FeatureEnabled(string typeName)
        {
            OpenXRFeature feature = FindFeature(typeName);
            return feature != null && feature.enabled;
        }

        static void SetFeature(string typeName, bool enabled)
        {
            OpenXRFeature feature = FindFeature(typeName);
            if (feature == null)
                throw new InvalidOperationException($"OpenXR feature {typeName} not found for Android.");
            feature.enabled = enabled;
            EditorUtility.SetDirty(feature);
        }

        static bool? OpenXrBool(UnityEngine.Object target, string property) => SerializedBool(target, property);

        static bool NoteSymmetricProjectionSkipped()
        {
            Debug.LogWarning($"{Tag}Symmetric projection left off: the original Quest is still a target device (OpenXR > Meta Quest Support > Target Devices), and the build refuses symmetric projection with it. Untick \"Quest\" there if you no longer support it, then run this again.");
            return true;
        }

        // True while the original Quest (manifest name "quest") is ticked in the Meta Quest feature's target devices.
        static bool OriginalQuestIsTarget()
        {
            OpenXRFeature feature = FindFeature("MetaQuestFeature");
            if (feature == null)
                return false;
            SerializedProperty devices = new SerializedObject(feature).FindProperty("targetDevices");
            if (devices == null || !devices.isArray)
                return false;
            for (int i = 0; i < devices.arraySize; i++)
            {
                SerializedProperty device = devices.GetArrayElementAtIndex(i);
                if (device.FindPropertyRelative("manifestName")?.stringValue == "quest")
                    return device.FindPropertyRelative("enabled")?.boolValue == true;
            }
            return false;
        }

        static IEnumerable<string> CaveTextures()
        {
            return AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/CaveGenerator/Textures" }).Select(AssetDatabase.GUIDToAssetPath);
        }

        static int FoveationApi() => SerializedInt(OpenXRSettings.GetSettingsForBuildTargetGroup(Android), "m_foveatedRenderingApi") ?? -1;

        // ---- URP helpers -------------------------------------------------------------------------------------------

        static IEnumerable<UniversalRenderPipelineAsset> AndroidUrpAssets()
        {
            var assets = new List<RenderPipelineAsset>();
            QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref assets);
            return assets.OfType<UniversalRenderPipelineAsset>().Distinct().ToList();
        }

        static bool? UrpFlag(UnityEngine.Object asset, string property) => SerializedBool(asset, property);

        // ---- Serialized property helpers ---------------------------------------------------------------------------

        static bool? SerializedBool(UnityEngine.Object target, string property)
        {
            SerializedProperty p = target == null ? null : new SerializedObject(target).FindProperty(property);
            return p?.boolValue;
        }

        static int? SerializedInt(UnityEngine.Object target, string property)
        {
            SerializedProperty p = target == null ? null : new SerializedObject(target).FindProperty(property);
            return p?.intValue;
        }

        static void SetSerialized(UnityEngine.Object target, string property, object value)
        {
            if (target == null)
                throw new InvalidOperationException($"No object to set {property} on.");

            var serialized = new SerializedObject(target);
            SerializedProperty p = serialized.FindProperty(property);
            if (p == null)
                throw new InvalidOperationException($"{target.name} has no property {property}.");

            if (value is bool b) p.boolValue = b;
            else if (value is int i) p.intValue = i;
            else throw new ArgumentException($"Unsupported value type for {property}.");

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }
    }
}
