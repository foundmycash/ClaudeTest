using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LoadingScreen
{
    /// <summary>
    /// Hides the switch from the Loading scene to the main scene inside the explosion's fire, then burns the fire away
    /// to reveal the main scene.
    ///
    /// It sits in the Loading scene until <see cref="HandOver"/>. From then on it is kept alive across the switch
    /// (DontDestroyOnLoad) so it can finish the job after the Loading scene is gone, and it destroys itself at the end.
    /// While the fire fills the whole view it:
    ///   1. removes the Loading rig's GorillaLocomotion.Player. That class is a singleton whose Awake destroys any
    ///      second instance, so left alive it would make the main scene's player (and its camera) delete itself;
    ///   2. lets the main scene activate, and in the same frame its objects wake up, makes the main scene's camera the
    ///      one that draws the fire and switches the Loading camera and audio listener off;
    ///   3. unloads the Loading scene and the assets only it used;
    /// and only then reveals the main scene. The main player's movement stays locked until the view is mostly clear.
    ///
    /// The fire is one full-screen triangle drawn by Hidden/Loading/FireOverlay for one camera only, which works the same
    /// on single-pass instanced, multiview and multi-pass VR. The explosion sound plays from here too, so it keeps
    /// rolling into the main scene instead of being cut off with the Loading scene.
    /// </summary>
    [DisallowMultipleComponent]
    public class SceneTransition : MonoBehaviour
    {
        public enum Phase { Idle, Approach, Covered, Revealing, Finished }

        const float MaxAnimationStep = 1f / 30f;   // a hitch must not make the reveal jump
        const float ApproachHeat = 0.9f;    // yellow-white flames as the fireball swallows the view
        const float HoldHeat = 0.64f;       // cooling to orange while the view is covered
        const float EndHeat = 0.45f;        // deep orange-red by the time the last flames burn away

        [Header("Look")]
        [Tooltip("Material using Hidden/Loading/FireOverlay. A copy is used at runtime.")]
        public Material overlayMaterial;

        [Header("Timing (seconds)")]
        [Tooltip("Shortest time the view stays filled with fire, even when the main scene is ready sooner.")]
        [Min(0f)] public float minimumHold = 0.55f;

        [Tooltip("How long the fire takes to burn away and reveal the main scene.")]
        [Min(0.1f)] public float revealDuration = 1.8f;

        [Tooltip("Longest wait for the main scene to bring its own camera before revealing anyway.")]
        [Min(0f)] public float cameraWaitLimit = 2f;

        [Header("Audio")]
        [Tooltip("2D source the explosion plays on. It lives on this object so the boom keeps rolling into the main scene.")]
        public AudioSource boomSource;

        [Header("Player")]
        [Tooltip("Keep the main scene's player from moving until the reveal is this far along.")]
        [Range(0f, 1f)] public float unlockPlayerAt = 0.5f;

        /// <summary>The transition that is currently running, if any (for tools and tests).</summary>
        public static SceneTransition Active { get; private set; }

        public Phase CurrentPhase { get; private set; } = Phase.Idle;

        static readonly int CoverId = Shader.PropertyToID("_Cover");
        static readonly int RevealId = Shader.PropertyToID("_Reveal");
        static readonly int HeatId = Shader.PropertyToID("_Heat");
        static readonly int FlashId = Shader.PropertyToID("_Flash");
        static readonly int GlowId = Shader.PropertyToID("_Glow");
        static readonly int FireTimeId = Shader.PropertyToID("_FireTime");
        static readonly int SpaceRotationId = Shader.PropertyToID("_SpaceRotation");

        Camera targetCamera;
        GameObject overlay;
        MeshRenderer overlayRenderer;
        Mesh overlayMesh;
        Material material;

        float cover, reveal, heat = ApproachHeat, flash, glow, fireTime;
        int forceVisibleFrames;

        GorillaLocomotion.Player lockedPlayer;
        bool lockedPlayerWasDisabled;
        bool priorityOverridden;
        ThreadPriority priorityToRestore;

        // ------------------------------------------------------------------------------------------------------
        // Driven by LoadingSequence while the Loading scene is still there
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Draws the (invisible) fire for a couple of frames so its shader is ready before it is needed.</summary>
        public IEnumerator Prewarm(Camera cam)
        {
            SetTargetCamera(cam);
            forceVisibleFrames = 2;
            while (forceVisibleFrames > 0)
                yield return null;
        }

        /// <summary>Starts drawing the fire for this camera. Follow up with <see cref="SetApproach"/> every frame.</summary>
        public void BeginApproach(Camera cam)
        {
            SetTargetCamera(cam);
            CurrentPhase = Phase.Approach;
            Active = this;
        }

        /// <summary>How far the flames have closed in (0..1) and how strongly they light up the view.</summary>
        public void SetApproach(float coverAmount, float glowAmount)
        {
            cover = Mathf.Clamp01(coverAmount);
            glow = Mathf.Max(0f, glowAmount);
        }

        /// <summary>A white flash over the whole view that fades by itself.</summary>
        public void Flash(float amount)
        {
            flash = Mathf.Max(flash, amount);
        }

        /// <summary>Plays the explosion sound at an exact time on the audio clock (see <see cref="AudioSettings.dspTime"/>).</summary>
        public void ScheduleBoom(double dspTime)
        {
            if (boomSource != null && boomSource.clip != null)
                boomSource.PlayScheduled(dspTime);
        }

        /// <summary>
        /// Takes over once the fire fills the view: activates the main scene behind it, swaps cameras, unloads the
        /// Loading scene, reveals the main scene and cleans up after itself.
        /// </summary>
        /// <param name="mainSceneLoad">The main scene's load, held back with allowSceneActivation = false.</param>
        /// <param name="mainSceneName">Name of the scene that load brings in.</param>
        /// <param name="loadingScene">The Loading scene, unloaded once the main scene is up.</param>
        /// <param name="restorePriority">Application.backgroundLoadingPriority to go back to when done.</param>
        public void HandOver(AsyncOperation mainSceneLoad, string mainSceneName, Scene loadingScene, ThreadPriority restorePriority)
        {
            cover = 1f;
            CurrentPhase = Phase.Covered;
            Active = this;

            transform.SetParent(null, true);
            DontDestroyOnLoad(gameObject);
            StartCoroutine(RunHandOver(mainSceneLoad, mainSceneName, loadingScene, restorePriority));
        }

        // ------------------------------------------------------------------------------------------------------
        // Hand-over
        // ------------------------------------------------------------------------------------------------------

        IEnumerator RunHandOver(AsyncOperation load, string mainSceneName, Scene loadingScene, ThreadPriority restorePriority)
        {
            float coveredAt = Time.unscaledTime;

            bool lightProbesChanged = false;
            Action onProbesChanged = () => lightProbesChanged = true;
            LightProbes.needsRetetrahedralization += onProbesChanged;

            RemoveLoadingSingletons(loadingScene);
            yield return null;   // Destroy() takes effect at the end of the frame

            // The view is covered, so let the main scene wake up as fast as it can.
            priorityToRestore = restorePriority;
            priorityOverridden = true;
            Application.backgroundLoadingPriority = ThreadPriority.High;

            // sceneLoaded runs right after the main scene's Awake/OnEnable and before any Start, in the same frame: the
            // camera swap happens before the main scene renders anything or looks for Camera.main.
            Scene mainScene = default;
            void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                if (scene.name != mainSceneName)
                    return;
                mainScene = scene;
                SceneManager.SetActiveScene(scene);   // lighting, skybox and new objects now belong to the main scene
                TrySwitchCamera(scene, loadingScene);
                LockPlayer(scene);
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            load.allowSceneActivation = true;
            while (!load.isDone)
                yield return null;
            SceneManager.sceneLoaded -= OnSceneLoaded;

            if (!mainScene.IsValid())
                mainScene = SceneManager.GetSceneByName(mainSceneName);

            // A main scene that spawns its camera a little later: keep the Loading camera until it shows up.
            float waited = 0f;
            while (targetCamera == null || targetCamera.gameObject.scene == loadingScene)
            {
                if (mainScene.IsValid() && TrySwitchCamera(mainScene, loadingScene))
                    break;
                if (waited >= cameraWaitLimit)
                {
                    Debug.LogWarning($"[Loading] '{mainSceneName}' has no camera; revealing without one.", this);
                    break;
                }
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (loadingScene.IsValid() && loadingScene.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(loadingScene);
                while (unload != null && !unload.isDone)
                    yield return null;
            }

            AsyncOperation unloadAssets = Resources.UnloadUnusedAssets();
            while (!unloadAssets.isDone)
                yield return null;

            LightProbes.needsRetetrahedralization -= onProbesChanged;
            if (lightProbesChanged)
                LightProbes.TetrahedralizeAsync();

            RestoreLoadingPriority();

            while (Time.unscaledTime - coveredAt < minimumHold)
                yield return null;
            yield return null;   // one main-scene frame (all Start calls done) before anything shows

            // Reveal.
            CurrentPhase = Phase.Revealing;
            float startHeat = heat;
            for (float t = 0f; t < revealDuration; )
            {
                t += Mathf.Min(Time.unscaledDeltaTime, MaxAnimationStep);
                float u = Mathf.Clamp01(t / revealDuration);

                reveal = u;   // even sweep: the reveal pattern itself spreads the pixels over the whole range
                heat = Mathf.Lerp(startHeat, EndHeat, u);
                glow = 0.15f * (1f - u) * (1f - u) * (1f - u);
                if (u >= unlockPlayerAt)
                    UnlockPlayer();
                yield return null;
            }

            reveal = 1f;
            glow = 0f;
            UnlockPlayer();
            CurrentPhase = Phase.Finished;
            ReleaseOverlay();

            // Let the tail of the explosion ring out in the main scene, then nothing is left behind.
            while (boomSource != null && boomSource.isPlaying)
                yield return null;
            Destroy(gameObject);
        }

        static void RemoveLoadingSingletons(Scene loadingScene)
        {
            if (!loadingScene.IsValid() || !loadingScene.isLoaded)
                return;

            foreach (GameObject root in loadingScene.GetRootGameObjects())
            {
                foreach (GorillaLocomotion.Player player in root.GetComponentsInChildren<GorillaLocomotion.Player>(true))
                    Destroy(player);
            }
        }

        bool TrySwitchCamera(Scene mainScene, Scene loadingScene)
        {
            Camera cam = FindSceneCamera(mainScene);
            if (cam == null)
                return false;

            SetTargetCamera(cam);

            if (loadingScene.IsValid() && loadingScene.isLoaded)
            {
                foreach (GameObject root in loadingScene.GetRootGameObjects())
                {
                    foreach (Camera c in root.GetComponentsInChildren<Camera>(true))
                        c.enabled = false;
                    foreach (AudioListener l in root.GetComponentsInChildren<AudioListener>(true))
                        l.enabled = false;
                }
            }
            return true;
        }

        /// <summary>The scene's player camera: an enabled MainCamera first, then any camera rendering to the headset.</summary>
        public static Camera FindSceneCamera(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            Camera fallback = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Camera c in root.GetComponentsInChildren<Camera>())
                {
                    if (!c.isActiveAndEnabled || c.targetTexture != null)
                        continue;
                    if (c.CompareTag("MainCamera"))
                        return c;
                    if (fallback == null || (c.stereoTargetEye != StereoTargetEyeMask.None && fallback.stereoTargetEye == StereoTargetEyeMask.None))
                        fallback = c;
                }
            }
            return fallback;
        }

        void LockPlayer(Scene scene)
        {
            GorillaLocomotion.Player player = GorillaLocomotion.Player.Instance;
            if (player == null || player.gameObject.scene != scene)
                return;

            lockedPlayer = player;
            lockedPlayerWasDisabled = player.disableMovement;
            player.disableMovement = true;
        }

        void UnlockPlayer()
        {
            if (lockedPlayer != null)
                lockedPlayer.disableMovement = lockedPlayerWasDisabled;
            lockedPlayer = null;
        }

        void RestoreLoadingPriority()
        {
            if (priorityOverridden)
                Application.backgroundLoadingPriority = priorityToRestore;
            priorityOverridden = false;
        }

        // ------------------------------------------------------------------------------------------------------
        // Overlay
        // ------------------------------------------------------------------------------------------------------

        void Awake()
        {
            if (overlayMaterial == null)
            {
                Debug.LogError("[Loading] SceneTransition has no overlay material.", this);
                return;
            }

            material = new Material(overlayMaterial) { name = "Fire Overlay (runtime)" };

            // One oversized triangle in clip space (the shader writes the vertices as they are), never culled.
            overlayMesh = new Mesh { name = "Fire Overlay Triangle" };
            overlayMesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
            overlayMesh.triangles = new[] { 0, 1, 2 };
            overlayMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

            overlay = new GameObject("Fire Overlay");
            overlay.transform.SetParent(transform, false);
            overlay.AddComponent<MeshFilter>().sharedMesh = overlayMesh;

            overlayRenderer = overlay.AddComponent<MeshRenderer>();
            overlayRenderer.sharedMaterial = material;
            overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            overlayRenderer.receiveShadows = false;
            overlayRenderer.lightProbeUsage = LightProbeUsage.Off;
            overlayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            overlayRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            overlayRenderer.allowOcclusionWhenDynamic = false;
            overlayRenderer.enabled = false;
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        }

        void OnDestroy()
        {
            UnlockPlayer();
            RestoreLoadingPriority();
            ReleaseOverlay();
            if (Active == this)
                Active = null;
        }

        void Update()
        {
            if (material == null)
                return;

            float dt = Mathf.Min(Time.unscaledDeltaTime, MaxAnimationStep);
            fireTime += dt;
            flash *= Mathf.Exp(-dt * 9f);
            if (flash < 0.002f)
                flash = 0f;

            // The flames cool down a little while they hold the view.
            if (CurrentPhase == Phase.Covered)
                heat = Mathf.MoveTowards(heat, HoldHeat, dt * 0.35f);

            material.SetFloat(CoverId, cover);
            material.SetFloat(RevealId, reveal);
            material.SetFloat(HeatId, heat);
            material.SetFloat(FlashId, flash);
            material.SetFloat(GlowId, glow);
            material.SetFloat(FireTimeId, fireTime);
            material.SetMatrix(SpaceRotationId, SpaceRotation());

            if (forceVisibleFrames > 0)
                forceVisibleFrames--;
        }

        // World -> tracking space of the XR rig (the camera's parent). The head moves the same way in both rigs' tracking
        // spaces, so flames expressed there stay continuous when the camera changes.
        Matrix4x4 SpaceRotation()
        {
            Transform space = targetCamera != null ? targetCamera.transform.parent : null;
            return space != null ? Matrix4x4.Rotate(Quaternion.Inverse(space.rotation)) : Matrix4x4.identity;
        }

        bool OverlayVisible =>
            forceVisibleFrames > 0 || (CurrentPhase != Phase.Idle && CurrentPhase != Phase.Finished) || flash > 0f || glow > 0f;

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (overlayRenderer != null)
                overlayRenderer.enabled = cam == targetCamera && OverlayVisible;
        }

        void SetTargetCamera(Camera cam)
        {
            targetCamera = cam;
            if (overlay != null && cam != null)
                overlay.layer = LayerRenderedBy(cam);
        }

        static int LayerRenderedBy(Camera cam)
        {
            int mask = cam.cullingMask;
            if ((mask & (1 << cam.gameObject.layer)) != 0)
                return cam.gameObject.layer;
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) != 0)
                    return layer;
            }
            return cam.gameObject.layer;
        }

        void ReleaseOverlay()
        {
            if (overlay != null) Destroy(overlay);
            if (material != null) Destroy(material);
            if (overlayMesh != null) Destroy(overlayMesh);
            overlay = null;
            overlayRenderer = null;
            material = null;
            overlayMesh = null;
        }
    }
}
