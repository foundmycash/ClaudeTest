using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace VRFog
{
    /// <summary>
    /// Volumetric fog for a box. Put it on a cube: the cube's renderer is drawn with the "VR Volumetric Fog/Box Volume"
    /// shader, which ray-marches the inside of the box.
    ///
    /// The fog is lit by the ambient light, the main light and every additional light (point and spot lights), so a
    /// Point Light placed in or near the box makes the fog glow around it. Lights do not cast shadows into the fog;
    /// that keeps it affordable in VR.
    ///
    /// It updates live in the Scene view, so every setting below can be tuned without pressing Play. Move, rotate and
    /// scale the cube freely: the fog fills it and fades out softly towards its faces.
    ///
    /// Looks: right-click the component header > Preset to load "Misty Night" (dense, smooth, cold mist with soft
    /// glowing lamps) or "Wispy Clouds" (billowing fog with gaps), then fine tune from there.
    ///
    /// VR cost is set by Max Steps (and Max Lights): on mobile / standalone headsets the Mobile values are used.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    [AddComponentMenu("Rendering/Volumetric Fog")]
    public class VolumetricFog : MonoBehaviour
    {
        public enum NoiseResolution { Low = 32, Medium = 48, High = 64 }
        public enum Preset { MistyNight, WispyClouds }

        // The settings a preset changes (everything about the look, nothing about quality / performance).
        struct Look
        {
            public Color fogColor;
            public float density, lightIntensity, ambientIntensity, anisotropy, lightCoreRadius, lightAbsorption;
            public float edgeFade, heightFalloff, heightFalloffStart;
            public float noiseStrength, noiseSize, noiseCoverage, noiseSharpness, fineDetail, fineDetailScale, windSpeed, turbulence;
        }

        // Dense, smooth, cold mist: distant things dissolve into silhouettes, lamps glow as big soft halos and the
        // noise is only a gentle slow swirl (no gaps).
        static readonly Look MistyNight = new Look
        {
            fogColor = new Color(0.22f, 0.68f, 0.92f),
            density = 1f, lightIntensity = 3f, ambientIntensity = 0.2f, anisotropy = 0.6f, lightCoreRadius = 1.2f, lightAbsorption = 0.8f,
            edgeFade = 2f, heightFalloff = 0.1f, heightFalloffStart = 0f,
            noiseStrength = 0.35f, noiseSize = 8f, noiseCoverage = 0f, noiseSharpness = 1f,
            fineDetail = 0.35f, fineDetailScale = 3.1f, windSpeed = 0.12f, turbulence = 0.5f
        };

        // Billowing fog with clear gaps between the wisps.
        static readonly Look WispyClouds = new Look
        {
            fogColor = new Color(0.78f, 0.85f, 0.95f),
            density = 0.7f, lightIntensity = 1f, ambientIntensity = 0.6f, anisotropy = 0.45f, lightCoreRadius = 0.8f, lightAbsorption = 0f,
            edgeFade = 1.5f, heightFalloff = 0f, heightFalloffStart = 0f,
            noiseStrength = 1f, noiseSize = 4f, noiseCoverage = 0.4f, noiseSharpness = 1.8f,
            fineDetail = 0.7f, fineDetailScale = 3.1f, windSpeed = 0.3f, turbulence = 1f
        };

        const string ShaderName = "VR Volumetric Fog/Box Volume";

        [Header("Look")]
        [Tooltip("Tint of the fog. It multiplies the light that the fog scatters, so a darker colour gives darker fog.")]
        public Color fogColor = WispyClouds.fogColor;

        [Tooltip("How thick the fog is, per meter. Around 0.1 is a light haze, around 1 is a thick fog bank.")]
        [Range(0f, 3f)] public float density = WispyClouds.density;

        [Tooltip("Brightness of the main light and the additional lights (point / spot lights) inside the fog.")]
        [Range(0f, 8f)] public float lightIntensity = WispyClouds.lightIntensity;

        [Tooltip("Brightness of the ambient (sky) light inside the fog. This is what lights the fog far away from any lamp.")]
        [Range(0f, 4f)] public float ambientIntensity = WispyClouds.ambientIntensity;

        [Tooltip("How much light is thrown forward. Higher values make a bright halo when you look towards a light; 0 scatters evenly; negative scatters backwards.")]
        [Range(-0.9f, 0.95f)] public float anisotropy = WispyClouds.anisotropy;

        [Tooltip("Keeps the glow finite right next to a light. Larger = softer, bigger glow around lamps.")]
        [Range(0.05f, 3f)] public float lightCoreRadius = WispyClouds.lightCoreRadius;

        [Tooltip("How much the fog swallows the light of lamps on its way to a point. Higher = tight glowing halos with darkness between lamps (like lamps in a thick mist); 0 = light ignores the fog.")]
        [Range(0f, 2f)] public float lightAbsorption = WispyClouds.lightAbsorption;

        [Header("Shape")]
        [Tooltip("The fog fades out this far (meters) before the faces of the cube, so the cube never shows. Keep it under half the cube size.")]
        [Range(0f, 10f)] public float edgeFade = WispyClouds.edgeFade;

        [Tooltip("How fast the fog thins out going up from the bottom of the cube, per meter. 0 = same density at every height.")]
        [Range(0f, 2f)] public float heightFalloff = WispyClouds.heightFalloff;

        [Tooltip("Height above the bottom of the cube where the height falloff starts (meters).")]
        [Range(0f, 20f)] public float heightFalloffStart = WispyClouds.heightFalloffStart;

        [Tooltip("The fog fades in over this distance (meters) in front of the eyes. Avoids a harsh wall of fog when walking into it.")]
        [Range(0f, 3f)] public float nearFade = 0.6f;

        [Tooltip("Fog further away than this (meters) from the eyes is not drawn. Lower values are cheaper.")]
        [Range(1f, 200f)] public float maxDistance = 60f;

        [Header("Noise (the swirling detail)")]
        [Tooltip("How much the noise breaks the fog up. 0 = perfectly even fog, 1 = full wisps and gaps.")]
        [Range(0f, 1f)] public float noiseStrength = WispyClouds.noiseStrength;

        [Tooltip("Size of the biggest wisps, in meters.")]
        [Range(0.5f, 40f)] public float noiseSize = WispyClouds.noiseSize;

        [Tooltip("Higher = more empty gaps between the wisps. 0 = no gaps at all (smooth mist).")]
        [Range(0f, 0.95f)] public float noiseCoverage = WispyClouds.noiseCoverage;

        [Tooltip("Higher = harder edged wisps, lower = soft and milky.")]
        [Range(0.2f, 4f)] public float noiseSharpness = WispyClouds.noiseSharpness;

        [Tooltip("Mixes in a second, finer layer of noise.")]
        [Range(0f, 1f)] public float fineDetail = WispyClouds.fineDetail;

        [Tooltip("How much smaller the fine layer is than the big wisps.")]
        [Range(1.5f, 8f)] public float fineDetailScale = WispyClouds.fineDetailScale;

        [Tooltip("Direction the fog drifts in (world space).")]
        public Vector3 windDirection = new Vector3(0.8f, 0.15f, 0.5f);

        [Tooltip("How fast the fog drifts, in meters per second. 0 = still.")]
        [Min(0f)] public float windSpeed = WispyClouds.windSpeed;

        [Tooltip("Internal swirling motion: the noise layers slowly meander against each other, so the fog keeps billowing and changing shape even when the wind is slow or off. 0 = the fog only drifts with the wind.")]
        [Range(0f, 2f)] public float turbulence = WispyClouds.turbulence;

        [Tooltip("Resolution of the generated noise texture. Higher is finer but takes a little longer to generate when the scene starts.")]
        public NoiseResolution noiseResolution = NoiseResolution.Medium;

        [Tooltip("Change this for a different noise pattern.")]
        public int noiseSeed = 1;

        [Header("Quality / VR performance")]
        [Tooltip("Most samples taken along each view ray. This is the main cost of the fog. Fewer steps = faster but blurrier fog.")]
        [Range(4, 64)] public int maxSteps = 20;

        [Tooltip("Max Steps used on mobile / standalone headsets such as Quest.")]
        [Range(4, 64)] public int mobileMaxSteps = 12;

        [Tooltip("Preferred distance between samples (meters). Short rays use fewer steps than Max Steps.")]
        [Range(0.05f, 3f)] public float stepSize = 0.35f;

        [Tooltip("How many additional lights (point / spot) are added per sample. Each one costs more per step.")]
        [Range(0, 8)] public int maxLights = 4;

        [Tooltip("Randomises where each pixel starts along its ray, which hides stepping bands as fine grain. 0 = off.")]
        [Range(0f, 1f)] public float stepJitter = 1f;

        [Tooltip("Stop the fog at solid objects inside it (and let them stand in front of it). Turns on the camera depth texture, which costs a little on mobile headsets.")]
        public bool useSceneDepth = true;

        [Header("Advanced")]
        [Tooltip("Fog shader. Assigned automatically; keep it referenced so builds include it.")]
        public Shader shader;

        struct DepthOverride
        {
            public UniversalAdditionalCameraData data;
            public CameraOverrideOption original;
        }

        static readonly int FogColorId = Shader.PropertyToID("_FogColor");
        static readonly int DensityId = Shader.PropertyToID("_Density");
        static readonly int AnisotropyId = Shader.PropertyToID("_Anisotropy");
        static readonly int LightIntensityId = Shader.PropertyToID("_LightIntensity");
        static readonly int AmbientIntensityId = Shader.PropertyToID("_AmbientIntensity");
        static readonly int LightCoreRadiusId = Shader.PropertyToID("_LightCoreRadius");
        static readonly int LightAbsorptionId = Shader.PropertyToID("_LightAbsorption");
        static readonly int EdgeFadeId = Shader.PropertyToID("_EdgeFade");
        static readonly int HeightFalloffId = Shader.PropertyToID("_HeightFalloff");
        static readonly int HeightOffsetId = Shader.PropertyToID("_HeightOffset");
        static readonly int NearFadeId = Shader.PropertyToID("_NearFade");
        static readonly int MaxDistanceId = Shader.PropertyToID("_MaxDistance");
        static readonly int NoiseTexId = Shader.PropertyToID("_NoiseTex");
        static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        static readonly int NoiseStrengthId = Shader.PropertyToID("_NoiseStrength");
        static readonly int NoiseCoverageId = Shader.PropertyToID("_NoiseCoverage");
        static readonly int NoiseSharpnessId = Shader.PropertyToID("_NoiseSharpness");
        static readonly int NoiseDetailId = Shader.PropertyToID("_NoiseDetail");
        static readonly int NoiseDetailScaleId = Shader.PropertyToID("_NoiseDetailScale");
        static readonly int WindId = Shader.PropertyToID("_WindVelocity");
        static readonly int TurbulenceId = Shader.PropertyToID("_Turbulence");
        static readonly int NoiseTexelsId = Shader.PropertyToID("_NoiseTexelsPerTile");
        static readonly int StepsId = Shader.PropertyToID("_Steps");
        static readonly int StepSizeId = Shader.PropertyToID("_StepSize");
        static readonly int MaxLightsId = Shader.PropertyToID("_MaxLights");
        static readonly int JitterId = Shader.PropertyToID("_Jitter");
        static readonly int BoxCenterId = Shader.PropertyToID("_BoxCenter");
        static readonly int BoxExtentsId = Shader.PropertyToID("_BoxExtents");
        static readonly int ZTestId = Shader.PropertyToID("_ZTest");

        MeshRenderer meshRenderer;
        MeshFilter meshFilter;
        Material material;

        int noiseSizeInUse;
        int noiseSeedInUse;
        bool noiseAcquired;

        readonly List<DepthOverride> depthOverrides = new List<DepthOverride>();
        int frameCounter;

#if UNITY_EDITOR
        // Editor only (a build has no Scene view). The Scene view camera cannot be given a depth texture the way game
        // cameras are (EnableCameraDepth): it follows the pipeline asset, and Mobile_RPAsset has it off. Play mode switches
        // to the Mobile quality level, so in the Scene view the fog could not see solid objects and was drawn over
        // everything: far too dense, while it looked right before pressing Play. So while a fog that uses scene depth is
        // enabled, the asset's depth texture is switched on for the length of each Scene view camera's render.
        static int sceneViewDepthFogs;
        static UniversalRenderPipelineAsset sceneViewDepthAsset;
        bool sceneViewDepthRegistered;

        void SetSceneViewDepth(bool wanted)
        {
            if (wanted == sceneViewDepthRegistered)
                return;
            sceneViewDepthRegistered = wanted;

            if (wanted)
            {
                if (sceneViewDepthFogs++ == 0)
                {
                    RenderPipelineManager.beginCameraRendering += RaiseSceneViewDepth;
                    RenderPipelineManager.endCameraRendering += LowerSceneViewDepth;
                }
            }
            else if (--sceneViewDepthFogs == 0)
            {
                RenderPipelineManager.beginCameraRendering -= RaiseSceneViewDepth;
                RenderPipelineManager.endCameraRendering -= LowerSceneViewDepth;
                LowerSceneViewDepth(default, null);
            }
        }

        // beginCameraRendering comes before the pipeline reads the asset's setting for this camera, endCameraRendering after.
        static void RaiseSceneViewDepth(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType != CameraType.SceneView)
                return;

            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            if (asset == null || asset.supportsCameraDepthTexture)
                return;

            asset.supportsCameraDepthTexture = true;
            sceneViewDepthAsset = asset;
        }

        static void LowerSceneViewDepth(ScriptableRenderContext context, Camera camera)
        {
            if (sceneViewDepthAsset == null)
                return;

            sceneViewDepthAsset.supportsCameraDepthTexture = false;
            sceneViewDepthAsset = null;
        }
#endif

        /// <summary>Loads one of the ready-made looks. Only the look changes, not the quality / performance settings.</summary>
        public void ApplyPreset(Preset preset)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "Apply Fog Preset");
#endif
            Look look = preset == Preset.WispyClouds ? WispyClouds : MistyNight;
            fogColor = look.fogColor;
            density = look.density;
            lightIntensity = look.lightIntensity;
            ambientIntensity = look.ambientIntensity;
            anisotropy = look.anisotropy;
            lightCoreRadius = look.lightCoreRadius;
            lightAbsorption = look.lightAbsorption;
            edgeFade = look.edgeFade;
            heightFalloff = look.heightFalloff;
            heightFalloffStart = look.heightFalloffStart;
            noiseStrength = look.noiseStrength;
            noiseSize = look.noiseSize;
            noiseCoverage = look.noiseCoverage;
            noiseSharpness = look.noiseSharpness;
            fineDetail = look.fineDetail;
            fineDetailScale = look.fineDetailScale;
            windSpeed = look.windSpeed;
            turbulence = look.turbulence;

            Apply();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        [ContextMenu("Preset/Misty Night (dense, smooth, cold mist)")]
        void ApplyMistyNightPreset() => ApplyPreset(Preset.MistyNight);

        [ContextMenu("Preset/Wispy Clouds (billowing, with gaps)")]
        void ApplyWispyCloudsPreset() => ApplyPreset(Preset.WispyClouds);

        void Reset()
        {
            shader = Shader.Find(ShaderName);
        }

        void OnEnable()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            meshFilter = GetComponent<MeshFilter>();

            if (shader == null)
                shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[VolumetricFog] Shader '{ShaderName}' not found.", this);
                return;
            }

            material = new Material(shader) { name = "Volumetric Fog (runtime)", hideFlags = HideFlags.HideAndDontSave };
            meshRenderer.sharedMaterial = material;

            if (Application.isPlaying)
            {
                // Fog is not geometry: no shadows, no probes, nothing to blend.
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                meshRenderer.lightProbeUsage = LightProbeUsage.Off;
                meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                SceneManager.sceneLoaded += OnSceneLoaded;
            }

            Apply();
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            RestoreCameraDepth();
#if UNITY_EDITOR
            SetSceneViewDepth(false);
#endif

            if (meshRenderer != null && material != null && meshRenderer.sharedMaterial == material)
                meshRenderer.sharedMaterial = null;

            if (material != null)
            {
                if (Application.isPlaying) Destroy(material); else DestroyImmediate(material);
                material = null;
            }

            ReleaseNoise();
        }

        void OnValidate()
        {
            // Runs when a value is edited in the Inspector (also before OnEnable at load, hence the null check).
            if (material != null)
                Apply();
        }

        void Update()
        {
            // New cameras can appear while the game runs (the Loading scene brings in other scenes).
            if (Application.isPlaying && useSceneDepth && ++frameCounter % 60 == 0)
                EnableCameraDepth();
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (useSceneDepth)
                EnableCameraDepth();
        }

        void Apply()
        {
            if (material == null)
                return;

            material.SetColor(FogColorId, fogColor);
            material.SetFloat(DensityId, density);
            material.SetFloat(AnisotropyId, anisotropy);
            material.SetFloat(LightIntensityId, lightIntensity);
            material.SetFloat(AmbientIntensityId, ambientIntensity);
            material.SetFloat(LightCoreRadiusId, lightCoreRadius);
            material.SetFloat(LightAbsorptionId, lightAbsorption);

            material.SetFloat(EdgeFadeId, edgeFade);
            material.SetFloat(HeightFalloffId, heightFalloff);
            material.SetFloat(HeightOffsetId, heightFalloffStart);
            material.SetFloat(NearFadeId, nearFade);
            material.SetFloat(MaxDistanceId, maxDistance);

            material.SetFloat(NoiseScaleId, noiseSize);
            material.SetFloat(NoiseStrengthId, noiseStrength);
            material.SetFloat(NoiseCoverageId, noiseCoverage);
            material.SetFloat(NoiseSharpnessId, noiseSharpness);
            material.SetFloat(NoiseDetailId, fineDetail);
            material.SetFloat(NoiseDetailScaleId, fineDetailScale);
            Vector3 wind = windDirection.sqrMagnitude > 1e-6f ? windDirection.normalized * windSpeed : Vector3.zero;
            material.SetVector(WindId, wind);
            material.SetFloat(TurbulenceId, turbulence);

            material.SetFloat(StepsId, Application.isMobilePlatform ? mobileMaxSteps : maxSteps);
            material.SetFloat(StepSizeId, stepSize);
            material.SetFloat(MaxLightsId, maxLights);
            material.SetFloat(JitterId, stepJitter);

            // The ray marcher works in the mesh's own box, so any box-shaped mesh works, not just Unity's cube.
            Vector3 center = Vector3.zero;
            Vector3 extents = Vector3.one * 0.5f;
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                Bounds bounds = meshFilter.sharedMesh.bounds;
                center = bounds.center;
                extents = bounds.extents;
            }
            material.SetVector(BoxCenterId, center);
            material.SetVector(BoxExtentsId, extents);

            if (useSceneDepth)
                material.EnableKeyword("_SCENE_DEPTH");
            else
                material.DisableKeyword("_SCENE_DEPTH");
            // Without scene depth the cube's own depth test is the only occlusion there is.
            material.SetFloat(ZTestId, useSceneDepth ? (float)CompareFunction.Always : (float)CompareFunction.LessEqual);

            UpdateNoiseTexture();
            material.SetFloat(NoiseTexelsId, noiseSizeInUse);   // lets the shader pick the right mip level for its step size

            if (Application.isPlaying)
            {
                if (useSceneDepth) EnableCameraDepth();
                else RestoreCameraDepth();
            }

#if UNITY_EDITOR
            SetSceneViewDepth(useSceneDepth);
#endif
        }

        void UpdateNoiseTexture()
        {
            int size = (int)noiseResolution;
            if (!noiseAcquired || size != noiseSizeInUse || noiseSeed != noiseSeedInUse)
            {
                ReleaseNoise();
                Texture3D texture = VolumetricFogNoise.Acquire(size, noiseSeed);
                noiseSizeInUse = size;
                noiseSeedInUse = noiseSeed;
                noiseAcquired = true;
                material.SetTexture(NoiseTexId, texture);
            }
        }

        void ReleaseNoise()
        {
            if (!noiseAcquired)
                return;
            VolumetricFogNoise.Release(noiseSizeInUse, noiseSeedInUse);
            noiseAcquired = false;
        }

        // The fog soft-clips against solid objects using the camera depth texture. The pipeline asset may have it off
        // (it is off in Mobile_RPAsset), so switch it on for game cameras while fog is active and put it back after.
        void EnableCameraDepth()
        {
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam.cameraType != CameraType.Game)
                    continue;

                UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
                if (data.renderType == CameraRenderType.Overlay || data.requiresDepthOption == CameraOverrideOption.On)
                    continue;

                depthOverrides.Add(new DepthOverride { data = data, original = data.requiresDepthOption });
                data.requiresDepthOption = CameraOverrideOption.On;
            }
        }

        void RestoreCameraDepth()
        {
            foreach (DepthOverride o in depthOverrides)
            {
                if (o.data != null)
                    o.data.requiresDepthOption = o.original;
            }
            depthOverrides.Clear();
        }
    }
}
