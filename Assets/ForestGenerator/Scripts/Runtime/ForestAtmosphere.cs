using UnityEngine;
using UnityEngine.Rendering;
using VRFog;

namespace HavocForest
{
    /// <summary>Which fog the forest uses.</summary>
    public enum ForestFogMode
    {
        /// <summary>The project's ray-marched Volumetric Fog on a box over the whole forest.</summary>
        Volumetric,
        /// <summary>Unity's built-in exponential squared distance fog.</summary>
        UnityFog
    }

    /// <summary>
    /// Fog, ambient light and sun for the forest.
    ///
    /// Fog: by default the project's Volumetric Fog (Assets/Scripts/VolumetricFog) on a box over the whole forest, created
    /// as a child of this object: drifting mist, sunlight scattering in the fog and the real-time streetlights glowing in
    /// it. The profiles set how thick it is and its colour (Density, Fog Color and Max Distance of the fog component are
    /// driven from here); everything else (noise, wind, lamp glow, steps) is tuned on the fog component itself. Unity Fog
    /// mode uses the built-in exponential squared fog instead, which every shader supports at almost no cost on Quest.
    ///
    /// Either way the camera background is set to the fog colour and the camera's far plane is pulled in to where the fog
    /// is opaque, so nothing hidden by fog is drawn.
    ///
    /// The look blends from the Calm profile at the trailhead to the Dread profile deep in the forest (the forest's depth
    /// map, sampled at the camera). Horror events can push a profile of their own on top with <see cref="PushOverride"/>.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Forest Atmosphere")]
    public class ForestAtmosphere : MonoBehaviour
    {
        [Tooltip("The look near the trailhead.")]
        public ForestAtmosphereProfile calm;

        [Tooltip("The look deep in the forest.")]
        public ForestAtmosphereProfile dread;

        [Tooltip("The directional light used as the sun or moon. Its colour, brightness and angle are set from the profiles.")]
        public Light sun;

        [Tooltip("How the look follows depth: x = dread at the camera (0 trailhead, 1 heart of the forest), y = how far towards the Dread profile.")]
        public AnimationCurve dreadToBlend = new AnimationCurve(new Keyframe(0.1f, 0f), new Keyframe(0.55f, 0.45f), new Keyframe(1f, 1f));

        [Tooltip("Seconds the look takes to catch up when the player moves deeper in or back out. Slow is creepier: the fog thickens without the player noticing when.")]
        [Min(0.1f)] public float response = 6f;

        [Header("Fog")]
        [Tooltip("Volumetric: the project's ray-marched Volumetric Fog on a box over the whole forest (drifting mist, light glowing in the fog). It needs the camera depth texture and costs GPU time on every pixel of both eyes: see the README, 'Volumetric fog'. If its box is missing or switched off, Unity fog is used.\nUnity Fog: the built-in distance fog, almost free.")]
        public ForestFogMode fogMode = ForestFogMode.Volumetric;

        [Tooltip("The forest's Volumetric Fog box, created as a child of this object when empty. Its size follows the forest, and its Density, Fog Color and Max Distance come from the profiles; tune everything else on it.")]
        public VolumetricFog volumetricFog;

        [Tooltip("How far around the player the volumetric fog stays thin, as a share of the fog distance. 0 = like real fog (already hazy a few metres away); 0.3 = close to Unity's fog (clear close up, thickening quickly further out). At night 0.2 keeps the flashlight from lighting up a wall of fog right in front of the eyes while its beam still shows further out; 0 makes that glare much stronger.")]
        [Range(0f, 0.6f)] public float clearZone = 0.2f;

        [Tooltip("Draw the volumetric fog in the Scene view too. Turn off to see the forest from above while editing.")]
        public bool fogInSceneView = true;

        [Header("Camera")]
        [Tooltip("Set the camera's background to the fog colour and pull its far plane in to where the fog is opaque.")]
        public bool controlCamera = true;

        [Tooltip("The camera to control. Empty = Camera.main.")]
        public Camera targetCamera;

        [Tooltip("The far plane is never pulled in closer than this many metres.")]
        [Min(10f)] public float minFarPlane = 40f;

        [Header("Editor Preview")]
        [Tooltip("Dread shown in the editor when not playing.")]
        [Range(0f, 1f)] public float previewDread = 0f;

        /// <summary>The dread at the camera, smoothed. 0 at the trailhead, 1 at the heart of the forest. For audio and event systems.</summary>
        public float CurrentDread { get; private set; }

        /// <summary>The blended look that is applied right now.</summary>
        public ForestAtmosphereState CurrentState { get; private set; }

        /// <summary>Brightness multiplier for the streetlights from the current look.</summary>
        public static float LampBrightness { get; private set; } = 1f;

        /// <summary>Extra lamp dropout chance per second from the current look.</summary>
        public static float LampDropouts { get; private set; }

        /// <summary>Where the volumetric fog hides everything, in metres from the camera (0 while the volumetric fog is off). The far plane and the instanced renderer use it.</summary>
        public static float VolumetricDistance { get; private set; }

        /// <summary>True while the volumetric fog draws the forest's fog.</summary>
        public bool VolumetricActive
        {
            get { return fogMode == ForestFogMode.Volumetric && volumetricFog != null && volumetricFog.isActiveAndEnabled; }
        }

        /// <summary>Set to 0..1 to force the dread (e.g. during a scripted event), or below 0 to follow the player again.</summary>
        public float dreadOverride = -1f;

        // The volumetric fog is about 98% opaque (optical depth 4) at the profile's opaque distance, so what is behind the
        // far plane could not be seen anyway.
        const float OpaqueOpticalDepth = 4f;
        const string FogShaderName = "VR Volumetric Fog/Box Volume";

        static readonly int DensityId = Shader.PropertyToID("_Density");
        static readonly int FogColorId = Shader.PropertyToID("_FogColor");
        static readonly int MaxDistanceId = Shader.PropertyToID("_MaxDistance");
        static readonly int NearFadeId = Shader.PropertyToID("_NearFade");
        static readonly int ClearDistanceId = Shader.PropertyToID("_ForestFogClearDistance");
        static readonly int BoxCenterId = Shader.PropertyToID("_BoxCenter");
        static readonly int BoxExtentsId = Shader.PropertyToID("_BoxExtents");
        static readonly int ExtinctionId = Shader.PropertyToID("_ForestFogExtinction");

        // Half size of the box drawn around each camera (see OnBeginCamera).
        const float DrawBoxHalfSize = 1f;

        ForestAtmosphereProfile overrideProfile;
        float overrideWeight;
        float overrideTarget;
        float overrideSpeed;

        MaterialPropertyBlock fogBlock;
        MeshRenderer fogRenderer;
        Mesh cubeMesh;
        float fogDensity;
        Vector4 fogTint;
        float fogMaxDistance;
        float fogNearFade;
        ForestWorld world;
        string fittedHash;
        readonly Vector3[] upDirection = { Vector3.up };
        readonly Color[] shResult = new Color[1];

        /// <summary>Blends towards another look (for a horror event), reaching the given weight over fadeSeconds.</summary>
        public void PushOverride(ForestAtmosphereProfile profile, float weight = 1f, float fadeSeconds = 2f)
        {
            if (profile == null) return;
            if (overrideProfile != profile) overrideWeight = overrideProfile == null ? 0f : overrideWeight;
            overrideProfile = profile;
            overrideTarget = Mathf.Clamp01(weight);
            overrideSpeed = fadeSeconds > 0f ? 1f / fadeSeconds : 1000f;
        }

        /// <summary>Fades the event look back out.</summary>
        public void ReleaseOverride(float fadeSeconds = 2f)
        {
            overrideTarget = 0f;
            overrideSpeed = fadeSeconds > 0f ? 1f / fadeSeconds : 1000f;
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            CurrentDread = Application.isPlaying ? SampleDread() : previewDread;
            if (Application.isPlaying) RefreshFogVolume();
#if UNITY_EDITOR
            // Objects cannot be created while the scene is still loading: set the fog box up on the next editor tick.
            else UnityEditor.EditorApplication.delayCall += DelayedRefresh;
#endif
            Apply(Blend(CurrentDread));
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (fogRenderer != null) fogRenderer.forceRenderingOff = false;
            VolumetricDistance = 0f;
            Shader.SetGlobalFloat(ExtinctionId, 0f);
        }

#if UNITY_EDITOR
        void DelayedRefresh()
        {
            if (this == null || !isActiveAndEnabled || Application.isPlaying) return;
            RefreshFogVolume();
            Apply(Blend(previewDread));
        }
#endif

        void OnValidate()
        {
            if (isActiveAndEnabled && !Application.isPlaying) Apply(Blend(previewDread));
        }

        void LateUpdate()
        {
            RefreshFogVolume();
            if (!Application.isPlaying)
            {
                Apply(Blend(previewDread));
                return;
            }

            float dt = Time.deltaTime;
            float target = dreadOverride >= 0f ? Mathf.Clamp01(dreadOverride) : SampleDread();
            CurrentDread = Mathf.MoveTowards(CurrentDread, target, dt / response);
            overrideWeight = Mathf.MoveTowards(overrideWeight, overrideTarget, dt * overrideSpeed);
            if (overrideWeight <= 0f && overrideTarget <= 0f) overrideProfile = null;
            Apply(Blend(CurrentDread));
        }

        float SampleDread()
        {
            Camera cam = Cam();
            ForestWorld w = ForestWorld.Active;
            if (cam == null || w == null) return 0f;
            return w.DreadAt(cam.transform.position);
        }

        Camera Cam()
        {
            return targetCamera != null ? targetCamera : Camera.main;
        }

        ForestAtmosphereState Blend(float d)
        {
            ForestAtmosphereState a = calm != null ? calm.ToState() : Defaults();
            ForestAtmosphereState b = dread != null ? dread.ToState() : a;
            float t = Mathf.Clamp01(dreadToBlend != null && dreadToBlend.length > 0 ? dreadToBlend.Evaluate(d) : d);
            ForestAtmosphereState s = ForestAtmosphereState.Lerp(a, b, t);
            if (overrideProfile != null && overrideWeight > 0f) s = ForestAtmosphereState.Lerp(s, overrideProfile.ToState(), overrideWeight);
            return s;
        }

        static ForestAtmosphereState? defaults;

        static ForestAtmosphereState Defaults()
        {
            if (!defaults.HasValue)
            {
                var p = ScriptableObject.CreateInstance<ForestAtmosphereProfile>();
                defaults = p.ToState();
                if (Application.isPlaying) Destroy(p); else DestroyImmediate(p);
            }
            return defaults.Value;
        }

        void Apply(ForestAtmosphereState s)
        {
            CurrentState = s;
            LampBrightness = s.lampBrightness;
            LampDropouts = s.lampDropouts;

            // RenderSettings, lights and cameras take gamma-space colours
            if (RenderSettings.ambientMode != AmbientMode.Trilight) RenderSettings.ambientMode = AmbientMode.Trilight;
            Color sky = s.skyAmbient.gamma, horizon = s.horizonAmbient.gamma, ground = s.groundAmbient.gamma;
            if (RenderSettings.ambientSkyColor != sky) RenderSettings.ambientSkyColor = sky;
            if (RenderSettings.ambientEquatorColor != horizon) RenderSettings.ambientEquatorColor = horizon;
            if (RenderSettings.ambientGroundColor != ground) RenderSettings.ambientGroundColor = ground;

            if (sun != null)
            {
                Color sc = s.sunColor.gamma;
                if (sun.color != sc) sun.color = sc;
                if (!Mathf.Approximately(sun.intensity, s.sunIntensity)) sun.intensity = s.sunIntensity;
                Quaternion rot = Quaternion.Euler(s.sunElevation, s.sunAzimuth, 0f);
                if (Quaternion.Angle(sun.transform.rotation, rot) > 0.01f) sun.transform.rotation = rot;
            }

            Color fog = s.fogColor.gamma;
            float opaqueDistance;
            if (VolumetricActive)
            {
                if (RenderSettings.fog) RenderSettings.fog = false;
                opaqueDistance = ApplyVolumetric(s);
            }
            else
            {
                if (!RenderSettings.fog) RenderSettings.fog = true;
                if (RenderSettings.fogMode != FogMode.ExponentialSquared) RenderSettings.fogMode = FogMode.ExponentialSquared;
                if (RenderSettings.fogColor != fog) RenderSettings.fogColor = fog;
                if (!Mathf.Approximately(RenderSettings.fogDensity, s.fogDensity)) RenderSettings.fogDensity = s.fogDensity;
                VolumetricDistance = 0f;
                Shader.SetGlobalFloat(ExtinctionId, 0f);
                opaqueDistance = s.OpaqueDistance;
            }

            // The camera only in play mode: in the editor its settings would be saved into the scene.
            if (controlCamera && Application.isPlaying)
            {
                Camera cam = Cam();
                if (cam != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = fog;
                    cam.farClipPlane = Mathf.Max(minFarPlane, opaqueDistance * 1.03f);
                }
            }
        }

        // ---------------------------------------------------------------- volumetric fog

        /// <summary>
        /// Drives the volumetric fog from the profile: as thick as the Unity fog it replaces is at its opaque distance, and
        /// tinted so that where it is thick it shows the profile's fog colour under this ambient light and sun.
        /// Returns the distance at which the fog hides everything.
        /// </summary>
        float ApplyVolumetric(ForestAtmosphereState s)
        {
            VolumetricFog fog = volumetricFog;
            if (fogRenderer == null || fogRenderer.gameObject != fog.gameObject) fogRenderer = fog.GetComponent<MeshRenderer>();
            if (fogRenderer != null && !fogRenderer.forceRenderingOff) fogRenderer.forceRenderingOff = true;   // drawn around each camera instead

            // The fog thins out towards the camera over the clear zone (the shader's near fade), which takes away about half
            // the zone's worth of fog; the rest is made that much thicker so the fog is still opaque at the far plane.
            // The noise thins the fog on average too (the eye sees further through the thin parts between the wisps).
            float clear = Mathf.Clamp(clearZone, 0f, 0.9f);
            float noise = Mathf.Lerp(1f, (1f - fog.noiseCoverage) / (fog.noiseSharpness + 1f), fog.noiseStrength);
            float extinction = OpaqueOpticalDepth / (Mathf.Max(1f, s.OpaqueDistance) * (1f - clear * 0.5f));
            float density = extinction / Mathf.Max(0.05f, noise);

            // With a height falloff the fog is thinner up high, so the eye sees further from there.
            float height = HeightFactor(fog);
            float distance = Mathf.Min(s.OpaqueDistance / Mathf.Max(0.02f, height), 2000f);
            float clearDistance = Mathf.Max(fog.nearFade, clear * distance);

            // The fog scatters the ambient light and the sun (forward scattering makes it brighter towards the sun).
            RenderSettings.ambientProbe.Evaluate(upDirection, shResult);
            Color lit = shResult[0] * fog.ambientIntensity;
            if (sun != null && sun.isActiveAndEnabled)
                lit += s.sunColor * (s.sunIntensity * fog.lightIntensity * AveragePhase(s.sunElevation, fog.anisotropy));
            var tint = new Vector4(Tint(s.fogColor.r, lit.r), Tint(s.fogColor.g, lit.g), Tint(s.fogColor.b, lit.b), 1f);

            fogDensity = density;
            fogTint = tint;                         // linear, like the shader reads it
            fogMaxDistance = distance * 1.2f;       // the fog fades out over its last 30%: keep that behind the far plane
            fogNearFade = clearDistance;

            VolumetricDistance = distance;
            Shader.SetGlobalFloat(ExtinctionId, extinction * height);   // the lamp halos fade with it
            Shader.SetGlobalFloat(ClearDistanceId, clearDistance * 0.5f);
            return distance;
        }

        /// <summary>
        /// Draws the volumetric fog for one camera. The fog shader draws the inside faces of its box and marches the view
        /// ray through the box, but the forest's box is hundreds of metres wide, so its faces would lie beyond the camera's
        /// far plane (pulled in to the fog's opaque distance) and be clipped away. So the fog component's own renderer stays
        /// off, and its material is drawn on a small box around the camera instead, with the forest box handed to the shader
        /// in that small box's space: same rays, same fog, faces always on screen.
        /// </summary>
        void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (!VolumetricActive || fogRenderer == null) return;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
            if (cam.cameraType == CameraType.SceneView && !fogInSceneView) return;
            Material material = fogRenderer.sharedMaterial;
            if (material == null) return;
            if (cubeMesh == null) cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (fogBlock == null) fogBlock = new MaterialPropertyBlock();

            Transform box = volumetricFog.transform;
            Matrix4x4 draw = Matrix4x4.TRS(cam.transform.position, box.rotation, Vector3.one * (DrawBoxHalfSize * 2f));
            MeshFilter filter = volumetricFog.GetComponent<MeshFilter>();
            Bounds mesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            Vector3 center = draw.inverse.MultiplyPoint3x4(box.TransformPoint(mesh.center));
            Vector3 extents = Vector3.Scale(mesh.extents, box.lossyScale) / (DrawBoxHalfSize * 2f);

            fogBlock.SetFloat(DensityId, fogDensity);
            fogBlock.SetVector(FogColorId, fogTint);
            fogBlock.SetFloat(MaxDistanceId, fogMaxDistance);
            fogBlock.SetFloat(NearFadeId, fogNearFade);
            fogBlock.SetVector(BoxCenterId, center);
            fogBlock.SetVector(BoxExtentsId, extents);

            var rp = new RenderParams(material)
            {
                camera = cam,
                layer = volumetricFog.gameObject.layer,
                matProps = fogBlock,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
                worldBounds = fogRenderer.bounds        // the whole forest, so every lamp light is handed to the fog
            };
            Graphics.RenderMesh(rp, cubeMesh, 0, draw);
        }

        static float Tint(float target, float light)
        {
            return Mathf.Clamp(target / Mathf.Max(light, 0.003f), 0f, 4f);
        }

        float HeightFactor(VolumetricFog fog)
        {
            if (fog.heightFalloff <= 0f) return 1f;
            Camera cam = Cam();
            if (cam == null) return 1f;
            Transform t = fog.transform;
            float aboveBottom = (t.InverseTransformPoint(cam.transform.position).y + 0.5f) * t.lossyScale.y;   // the box is a unit cube
            return Mathf.Exp(-Mathf.Max(0f, aboveBottom - fog.heightFalloffStart) * fog.heightFalloff);
        }

        // The fog shader's Henyey-Greenstein phase (1 when it scatters evenly), averaged over looking around horizontally.
        static float AveragePhase(float sunElevation, float g)
        {
            const int Samples = 16;
            float horizontal = Mathf.Cos(sunElevation * Mathf.Deg2Rad);
            float g2 = g * g, sum = 0f;
            for (int i = 0; i < Samples; i++)
            {
                float cosTheta = horizontal * Mathf.Cos((i + 0.5f) * (2f * Mathf.PI / Samples));
                float d = Mathf.Max(1f + g2 - 2f * g * cosTheta, 1e-4f);
                sum += (1f - g2) / (d * Mathf.Sqrt(d));
            }
            return sum / Samples;
        }

        /// <summary>
        /// Finds or creates the volumetric fog box, keeps the forest's own box shown in Volumetric mode and hidden in Unity Fog
        /// mode (Fog Mode is the switch), and fits it over the forest after it was generated. Called every frame; cheap when
        /// nothing changed.
        /// </summary>
        public void RefreshFogVolume()
        {
            if (fogMode == ForestFogMode.Volumetric)
            {
                if (volumetricFog == null) volumetricFog = GetComponentInChildren<VolumetricFog>(true);
                if (volumetricFog == null) volumetricFog = CreateFogVolume();
                else if (!volumetricFog.gameObject.activeSelf && IsOwnBox(volumetricFog)) ShowFogVolume(true);
            }
            else if (volumetricFog != null && volumetricFog.gameObject.activeSelf && IsOwnBox(volumetricFog))
            {
                ShowFogVolume(false);
            }

            if (volumetricFog != null) FitFogVolume();
        }

        // Only the box the forest made (marked with ForestFogVolume) is switched on and off; a fog volume set up by hand is
        // left as it is.
        static bool IsOwnBox(VolumetricFog fog)
        {
            return fog.GetComponent<ForestFogVolume>() != null;
        }

        void ShowFogVolume(bool show)
        {
            volumetricFog.gameObject.SetActive(show);
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
        }

        VolumetricFog CreateFogVolume()
        {
            if (!gameObject.scene.IsValid()) return null;           // a prefab asset, not a scene object
            Shader shader = Shader.Find(FogShaderName);
            if (shader == null) return null;                        // not in this build: Unity fog stays on

            var go = new GameObject("Volumetric Fog");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            go.AddComponent<ForestFogVolume>();

            // A forest mist: smooth (no gaps), big slow wisps, gentle glow around the lamps.
            var fog = go.AddComponent<VolumetricFog>();
            fog.shader = shader;
            fog.fogColor = calm != null ? calm.fogColor : fog.fogColor;
            fog.density = 0.07f;
            fog.maxDistance = 90f;
            fog.lightIntensity = 1f;
            fog.ambientIntensity = 1f;
            fog.anisotropy = 0.1f;      // mostly even scattering: a flashlight beam pointing away from the eyes still shows
            fog.lightCoreRadius = 3f;     // soft glow close to a light: keeps the flashlight from blinding the eyes in the fog
            fog.lightAbsorption = 0.5f;
            fog.edgeFade = 4f;
            fog.heightFalloff = 0f;
            fog.heightFalloffStart = 0f;
            fog.nearFade = 1f;
            fog.noiseStrength = 0.5f;
            fog.noiseSize = 16f;
            fog.noiseCoverage = 0f;
            fog.noiseSharpness = 1f;
            fog.fineDetail = 0.45f;
            fog.fineDetailScale = 3.1f;
            fog.windDirection = new Vector3(0.8f, 0.05f, 0.5f);
            fog.windSpeed = 0.35f;
            fog.turbulence = 0.7f;
            fog.maxSteps = 20;
            fog.mobileMaxSteps = 12;
            fog.stepSize = 1f;
            fog.maxLights = 3;          // the 2 real-time lamp lights and the flashlight
            fog.stepJitter = 1f;
            fog.useSceneDepth = true;
            go.SetActive(true);

            fogRenderer = mr;
            fittedHash = null;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
            return fog;
        }

        /// <summary>Sizes the fog box to the generated forest: the whole terrain, from below the lowest ground to above the ceiling.</summary>
        void FitFogVolume()
        {
            if (world == null) world = GetComponent<ForestWorld>();
            ForestData data = world != null ? world.data : null;
            if (data == null || data.heights == null || data.heights.Length == 0) return;
            if (fittedHash != null && fittedHash == data.contentHash) return;
            fittedHash = data.contentHash;

            Transform t = volumetricFog.transform;
            if (t.parent != transform) return;      // moved somewhere else by hand: leave it alone

            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float h in data.heights)
            {
                if (h < lo) lo = h;
                if (h > hi) hi = h;
            }
            float bottom = lo - 5f;
            float top = Mathf.Max(hi + 30f, data.ceilingHeight) + 5f;
            var size = new Vector3(data.terrainSize.x, top - bottom, data.terrainSize.y);
            var center = new Vector3(data.terrainMin.x + data.terrainSize.x * 0.5f, (top + bottom) * 0.5f, data.terrainMin.y + data.terrainSize.y * 0.5f);
            if ((t.localPosition - center).sqrMagnitude > 1e-4f) t.localPosition = center;
            if ((t.localScale - size).sqrMagnitude > 1e-4f) t.localScale = size;
            if (t.localRotation != Quaternion.identity) t.localRotation = Quaternion.identity;
        }
    }
}
