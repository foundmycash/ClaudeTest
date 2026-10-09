using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace WakeUp
{
    /// <summary>
    /// "Waking up" intro: the view starts as a solid colour (black), fades up to a blurry, too-bright world, the eyes
    /// blink fast a few times (quick dips back to the solid colour, every blink clears the blur a little) and finally
    /// open onto the normal, sharp game.
    ///
    /// Setup: none. A WakeUpSequence is added to the main camera automatically when the game boots (except when it
    /// boots into a scene with its own intro, such as the Loading scene, see ScenesWithOwnIntro). To tweak the
    /// timing or to turn the intro off while developing, add this component to the camera yourself (the automatic
    /// one is skipped when the scene already has one) and edit / untick "Play On Start".
    ///
    /// VR: the effect is a full-screen overlay drawn in clip space by <c>Hidden/WakeUp/EyelidOverlay</c>. It is one
    /// uniform colour plus a blur, so it is identical in both eyes and follows the head on single-pass instanced,
    /// multiview and multi-pass rendering. The screen stays black until the headset is actually being worn, so the
    /// intro is not played to an empty headset. Pressing any key or controller button skips it.
    ///
    /// Requires the Universal Render Pipeline (the blur reads the camera's opaque texture). With another pipeline the
    /// fade still works, only the blur is dropped.
    /// </summary>
    [DisallowMultipleComponent]
    public class WakeUpSequence : MonoBehaviour
    {
        const string ShaderResourcePath = "WakeUp/WakeUpEyelids";
        const float MaxFrameStep = 0.1f;        // a long frame (scene load, headset hiccup) must not eat part of the intro
        const float SkipGraceSeconds = 0.75f;   // ignore input right at the start, e.g. the key press that started play mode
        const float SkipDuration = 0.4f;

        [Header("Timeline (seconds)")]
        [Tooltip("Eyes shut, screen is the solid colour.")]
        [Min(0f)] public float blackHold = 1.2f;

        [Tooltip("The first, slow opening of the eyes onto a blurry world.")]
        [Min(0f)] public float firstOpenDuration = 2f;

        [Tooltip("How much of the world you can see the first time the eyes open. 1 = fully lit.")]
        [Range(0.1f, 1f)] public float firstOpenAmount = 0.7f;

        [Tooltip("Time spent squinting at the blurry world before the blinking starts.")]
        [Min(0f)] public float blurryHold = 1.3f;

        [Tooltip("Number of fast blinks. The last one opens all the way and brings the picture into focus.")]
        [Range(1, 6)] public int blinkCount = 3;

        [Min(0.02f)] public float blinkCloseDuration = 0.1f;
        [Min(0.02f)] public float blinkOpenDuration = 0.17f;

        [Tooltip("Pause between two blinks.")]
        [Min(0f)] public float blinkGap = 0.05f;

        [Tooltip("The last opening, from shut to fully open.")]
        [Min(0.05f)] public float finalOpenDuration = 0.35f;

        [Header("Look")]
        [Tooltip("Solid colour the screen starts from and blinks to.")]
        public Color coverColor = Color.black;

        [Tooltip("Size of the blur at its strongest, as a view angle in radians (0.026 is about 1.5 degrees).")]
        [Range(0f, 0.06f)] public float blurRadius = 0.026f;

        [Tooltip("Blur quality. Every sample costs a texture read per pixel, so this is the main cost of the effect.")]
        [Range(4, 32)] public int blurSamples = 20;

        [Tooltip("Blur quality used on mobile / standalone headsets such as Quest.")]
        [Range(4, 32)] public int mobileBlurSamples = 10;

        [Tooltip("How much the blurry picture is washed out with light.")]
        [Range(0f, 1f)] public float hazeStrength = 0.2f;

        [Tooltip("Extra brightness while blurry, as if the eyes are not used to the light yet. Keep it modest: a big jump from black to bright is uncomfortable in a headset.")]
        [Range(0f, 1f)] public float brightnessBoost = 0.12f;

        [Header("Behaviour")]
        [Tooltip("Start by itself when this component starts. Untick to trigger it from code with Play().")]
        public bool playOnStart = true;

        [Tooltip("Pressing a key or any controller button skips ahead to the open, sharp view.")]
        public bool allowSkip = true;

        [Tooltip("In VR, keep the screen black until the headset is being worn.")]
        public bool waitForHeadset = true;

        [Tooltip("Stop waiting for the headset after this long and play anyway (some setups never report presence).")]
        [Min(0f)] public float maxHeadsetWait = 15f;

        [Tooltip("Camera that wakes up. Defaults to the camera on this object, then the main camera.")]
        public Camera targetCamera;

        /// <summary>True from <see cref="Play"/> until the picture is fully visible and sharp.</summary>
        public bool IsPlaying => phase == Phase.WaitingForViewer || phase == Phase.Playing;

        /// <summary>Raised when the sequence has finished (or was skipped) and the overlay is gone.</summary>
        public event Action Completed;

        enum Phase { Idle, WaitingForViewer, Playing, Done }
        enum Ease { Linear, In, Out, InOut }

        struct Segment
        {
            public float duration;
            public float visFrom, visTo;
            public float blurFrom, blurTo;
            public Ease visEase;
            public float flutter;   // brightness tremble while the eyes are still heavy, 0 for none

            public Segment(float duration, float visFrom, float visTo, float blurFrom, float blurTo, Ease visEase, float flutter = 0f)
            {
                this.duration = duration;
                this.visFrom = visFrom;
                this.visTo = visTo;
                this.blurFrom = blurFrom;
                this.blurTo = blurTo;
                this.visEase = visEase;
                this.flutter = flutter;
            }
        }

        static readonly int VisibilityId = Shader.PropertyToID("_Visibility");
        static readonly int CoverColorId = Shader.PropertyToID("_CoverColor");
        static readonly int BlurId = Shader.PropertyToID("_Blur");
        static readonly int BlurRadiusId = Shader.PropertyToID("_BlurRadius");
        static readonly int TapsId = Shader.PropertyToID("_Taps");
        static readonly int HazeStrengthId = Shader.PropertyToID("_HazeStrength");
        static readonly int ExposureId = Shader.PropertyToID("_Exposure");

        readonly List<Segment> segments = new List<Segment>();

        Phase phase = Phase.Idle;
        int segmentIndex;
        float segmentTime;
        float totalTime;
        float age;
        float waited;
        float visibility;   // 0 = solid colour, 1 = clear view
        float blur;
        bool prevControllerDown;

        GameObject overlay;
        MeshRenderer overlayRenderer;
        Mesh overlayMesh;
        Material overlayMaterial;
        bool blurAvailable;

        UniversalAdditionalCameraData cameraData;
        CameraOverrideOption savedOpaqueTextureOption;
        bool opaqueTextureOverridden;

        // ------------------------------------------------------------------------------------------------------
        // Automatic setup
        // ------------------------------------------------------------------------------------------------------

        // Boot scenes that play their own intro (the Loading scene's cinematic) and never get the wake-up.
        static readonly string[] ScenesWithOwnIntro = { "Loading" };

        // Runs once when the game boots, after the first scene is loaded and before its first frame is drawn.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (Array.IndexOf(ScenesWithOwnIntro, SceneManager.GetActiveScene().name) >= 0)
                return;

            if (FindFirstObjectByType<WakeUpSequence>(FindObjectsInactive.Include) != null)
                return;

            Camera cam = FindDefaultCamera();
            if (cam != null)
                cam.gameObject.AddComponent<WakeUpSequence>();
        }

        static Camera FindDefaultCamera()
        {
            Camera cam = Camera.main;
            if (cam != null)
                return cam;

            foreach (Camera c in Camera.allCameras)
            {
                if (c.stereoTargetEye != StereoTargetEyeMask.None)
                    return c;
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Starts (or restarts) the sequence. The view goes black immediately.</summary>
        [ContextMenu("Play Wake-Up Sequence")]
        public void Play()
        {
            if (!Application.isPlaying)
                return;

            if (targetCamera == null)
                targetCamera = GetComponent<Camera>();
            if (targetCamera == null)
                targetCamera = FindDefaultCamera();
            if (targetCamera == null)
            {
                Debug.LogWarning("[WakeUp] No camera found, skipping the wake-up sequence.", this);
                return;
            }

            ReleaseOverlay();
            if (!BuildOverlay())
            {
                phase = Phase.Done;
                return;
            }

            BuildTimeline();
            segmentIndex = 0;
            segmentTime = 0f;
            totalTime = 0f;
            age = 0f;
            waited = 0f;
            prevControllerDown = false;
            phase = Phase.WaitingForViewer;
            enabled = true;

            Apply(0f, 1f);
        }

        /// <summary>Jumps to the end: the view fades up and the picture snaps into focus.</summary>
        public void Skip()
        {
            if (!IsPlaying)
                return;

            segments.Clear();
            segments.Add(new Segment(SkipDuration, visibility, 1f, blur, 0f, Ease.InOut));
            segmentIndex = 0;
            segmentTime = 0f;
            phase = Phase.Playing;
        }

        // ------------------------------------------------------------------------------------------------------
        // Unity messages
        // ------------------------------------------------------------------------------------------------------

        void Start()
        {
            if (playOnStart && phase == Phase.Idle)
                Play();
        }

        void OnDisable()
        {
            // Disabling the component aborts the intro; the normal view comes back straight away.
            ReleaseOverlay();
            if (IsPlaying)
                phase = Phase.Idle;
        }

        void OnDestroy()
        {
            ReleaseOverlay();
        }

        void Update()
        {
            if (!IsPlaying)
                return;

            float dt = Mathf.Min(Time.unscaledDeltaTime, MaxFrameStep);
            age += dt;

            // Always poll so button edges are tracked, but only act on them once the grace period is over.
            bool skipPressed = allowSkip && SkipInputPressed();
            if (skipPressed && age > SkipGraceSeconds)
                Skip();

            if (phase == Phase.WaitingForViewer)
            {
                waited += dt;
                if (!waitForHeadset || waited >= maxHeadsetWait || HeadsetReady())
                    phase = Phase.Playing;
                return;
            }

            Advance(dt);
        }

        // ------------------------------------------------------------------------------------------------------
        // Timeline
        // ------------------------------------------------------------------------------------------------------

        void BuildTimeline()
        {
            segments.Clear();

            // 1. Eyes shut.
            segments.Add(new Segment(blackHold, 0f, 0f, 1f, 1f, Ease.Linear));

            // 2. Eyes open onto a blurry world, then it brightens a little and shimmers while the eyes struggle to focus.
            segments.Add(new Segment(firstOpenDuration, 0f, firstOpenAmount, 1f, 1f, Ease.InOut));
            float settled = Mathf.Min(firstOpenAmount + 0.1f, 0.95f);
            segments.Add(new Segment(blurryHold, firstOpenAmount, settled, 1f, 1f, Ease.InOut, 0.03f));

            // 3. Fast blinks. Each time the world is a bit brighter and the picture a bit sharper.
            float currentVisibility = settled;
            float currentBlur = 1f;
            for (int i = 0; i < blinkCount; i++)
            {
                bool last = i == blinkCount - 1;
                float progress = (i + 1f) / blinkCount;
                float blurAfter = 1f - progress;
                float visibleAfter = last ? 1f : Mathf.Lerp(settled, 1f, progress);

                segments.Add(new Segment(blinkCloseDuration, currentVisibility, 0f, currentBlur, currentBlur, Ease.In));
                segments.Add(new Segment(last ? finalOpenDuration : blinkOpenDuration, 0f, visibleAfter, currentBlur, blurAfter, last ? Ease.InOut : Ease.Out));
                if (!last && blinkGap > 0f)
                    segments.Add(new Segment(blinkGap, visibleAfter, visibleAfter, blurAfter, blurAfter, Ease.Linear));

                currentVisibility = visibleAfter;
                currentBlur = blurAfter;
            }
        }

        void Advance(float dt)
        {
            segmentTime += dt;
            totalTime += dt;

            while (segmentIndex < segments.Count && segmentTime >= segments[segmentIndex].duration)
            {
                segmentTime -= segments[segmentIndex].duration;
                segmentIndex++;
            }

            if (segmentIndex >= segments.Count)
            {
                Finish();
                return;
            }

            Segment s = segments[segmentIndex];
            float t = segmentTime / s.duration;   // duration > 0 here, zero-length segments were consumed above

            float v = Mathf.Lerp(s.visFrom, s.visTo, ApplyEase(s.visEase, t));
            if (s.flutter > 0f)
                v += s.flutter * Mathf.Sin(totalTime * 7f) * Mathf.Sin(t * Mathf.PI);

            float b = Mathf.Lerp(s.blurFrom, s.blurTo, t * t * (3f - 2f * t));
            Apply(Mathf.Clamp01(v), b);
        }

        static float ApplyEase(Ease ease, float t)
        {
            switch (ease)
            {
                case Ease.In: return t * t;
                case Ease.Out: { float u = 1f - t; return 1f - u * u * u; }
                case Ease.InOut: return t * t * (3f - 2f * t);
                default: return t;
            }
        }

        void Finish()
        {
            ReleaseOverlay();
            phase = Phase.Done;
            enabled = false;
            Completed?.Invoke();
        }

        // ------------------------------------------------------------------------------------------------------
        // Overlay
        // ------------------------------------------------------------------------------------------------------

        bool BuildOverlay()
        {
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning($"[WakeUp] Shader 'Resources/{ShaderResourcePath}' is missing or unsupported, skipping the wake-up sequence.", this);
                return false;
            }

            blurAvailable = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            if (!blurAvailable)
                Debug.LogWarning("[WakeUp] Universal Render Pipeline not active: the blur is disabled, only the fade is shown.", this);

            overlayMaterial = new Material(shader) { name = "WakeUp Overlay (runtime)" };
            overlayMaterial.SetColor(CoverColorId, coverColor);
            overlayMaterial.SetFloat(BlurRadiusId, blurRadius);
            overlayMaterial.SetFloat(TapsId, Application.isMobilePlatform ? mobileBlurSamples : blurSamples);
            overlayMaterial.SetFloat(HazeStrengthId, hazeStrength);
            overlayMaterial.SetFloat(ExposureId, brightnessBoost);

            // One oversized triangle in clip space (the shader writes the vertices as they are), never culled.
            overlayMesh = new Mesh { name = "WakeUp Fullscreen Triangle" };
            overlayMesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
            overlayMesh.triangles = new[] { 0, 1, 2 };
            overlayMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

            overlay = new GameObject("WakeUp Overlay");
            overlay.layer = targetCamera.gameObject.layer;
            overlay.transform.SetParent(targetCamera.transform, false);
            overlay.AddComponent<MeshFilter>().sharedMesh = overlayMesh;

            overlayRenderer = overlay.AddComponent<MeshRenderer>();
            overlayRenderer.sharedMaterial = overlayMaterial;
            overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            overlayRenderer.receiveShadows = false;
            overlayRenderer.lightProbeUsage = LightProbeUsage.Off;
            overlayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            overlayRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            overlayRenderer.allowOcclusionWhenDynamic = false;

            // Only the player's camera gets the overlay, not the scene view, previews or reflection cameras.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;

            if (blurAvailable)
                RequestOpaqueTexture();

            return true;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (overlayRenderer != null)
                overlayRenderer.enabled = cam == targetCamera;
        }

        // The blur samples the scene colour, which URP only provides when the camera asks for it. The pipeline asset
        // may have it off (it is costly on mobile), so switch it on for the camera for the length of the intro only.
        void RequestOpaqueTexture()
        {
            cameraData = targetCamera.GetUniversalAdditionalCameraData();
            savedOpaqueTextureOption = cameraData.requiresColorOption;
            cameraData.requiresColorOption = CameraOverrideOption.On;
            opaqueTextureOverridden = true;
        }

        void ReleaseOverlay()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            if (opaqueTextureOverridden && cameraData != null)
                cameraData.requiresColorOption = savedOpaqueTextureOption;
            opaqueTextureOverridden = false;
            cameraData = null;

            if (overlay != null) Destroy(overlay);
            if (overlayMaterial != null) Destroy(overlayMaterial);
            if (overlayMesh != null) Destroy(overlayMesh);
            overlay = null;
            overlayRenderer = null;
            overlayMaterial = null;
            overlayMesh = null;
        }

        void Apply(float newVisibility, float newBlur)
        {
            visibility = newVisibility;
            blur = newBlur;

            if (overlayMaterial == null)
                return;

            overlayMaterial.SetFloat(VisibilityId, visibility);
            overlayMaterial.SetFloat(BlurId, blurAvailable ? blur : 0f);
        }

        // ------------------------------------------------------------------------------------------------------
        // VR presence and input
        // ------------------------------------------------------------------------------------------------------

        static bool IsXRRunning()
        {
            XRGeneralSettings xr = XRGeneralSettings.Instance;
            return xr != null && xr.Manager != null && xr.Manager.activeLoader != null;
        }

        // True once the player can actually see the game: on a flat screen straight away, in VR when the headset
        // display is up and the headset is on the player's head.
        bool HeadsetReady()
        {
            if (!IsXRRunning())
                return true;

            if (!XRSettings.isDeviceActive)
                return false;

            UnityEngine.XR.InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid && head.TryGetFeatureValue(CommonUsages.userPresence, out bool present))
                return present;

            return true;   // this runtime does not report presence
        }

        bool SkipInputPressed()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame)
                return true;

            bool down = AnyButtonDown(XRNode.LeftHand) || AnyButtonDown(XRNode.RightHand);
            bool pressed = down && !prevControllerDown;
            prevControllerDown = down;
            return pressed;
        }

        static bool AnyButtonDown(XRNode node)
        {
            UnityEngine.XR.InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid)
                return false;

            return IsDown(device, CommonUsages.triggerButton)
                || IsDown(device, CommonUsages.gripButton)
                || IsDown(device, CommonUsages.primaryButton)
                || IsDown(device, CommonUsages.secondaryButton);
        }

        static bool IsDown(UnityEngine.XR.InputDevice device, InputFeatureUsage<bool> usage)
        {
            return device.TryGetFeatureValue(usage, out bool value) && value;
        }
    }
}
