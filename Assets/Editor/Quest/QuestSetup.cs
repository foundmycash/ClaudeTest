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
                name = "URP (Android quality level): switch off unused features",
                why = "Light layers, light cookies, LOD cross fade and lens flares are unused by the scenes, but each one adds shader work or variants.",
                isDone = () => AndroidUrpAssets().All(a => UrpFlag(a, "m_SupportsLightLayers") == false
                                                           && UrpFlag(a, "m_SupportsLightCookies") == false
                                                           && UrpFlag(a, "m_EnableLODCrossFade") == false
                                                           && UrpFlag(a, "m_SupportDataDrivenLensFlare") == false
                                                           && UrpFlag(a, "m_SupportScreenSpaceLensFlare") == false),
                apply = () =>
                {
                    foreach (UniversalRenderPipelineAsset asset in AndroidUrpAssets())
                    {
                        foreach (string property in new[]
                                 {
                                     "m_SupportsLightLayers", "m_SupportsLightCookies", "m_EnableLODCrossFade",
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
