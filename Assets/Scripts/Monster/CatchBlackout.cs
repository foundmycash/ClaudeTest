using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonsterAI
{
    /// <summary>
    /// What happens when the monster catches the local player: an impact sound, the view cuts to black at once,
    /// stays black for <see cref="blackSeconds"/>, then fades back in over <see cref="fadeInSeconds"/>. No jump scare,
    /// no camera shake: the player is simply struck and the world goes away.
    ///
    /// It starts by itself (no scene setup) and listens to <see cref="MonsterHunter.AnyTargetCaught"/>. Only the
    /// local player's own catch triggers it, never a remote player's. A catch during a blackout is ignored.
    ///
    /// The black is the same clip-space overlay the wake-up intro uses (<c>Resources/WakeUp/WakeUpEyelids</c> with the
    /// blur off), so it covers both eyes in VR. The impact sound is <c>Resources/Monster/CatchImpact</c>, played flat
    /// (not 3D) so it hits the same in both ears; put a CatchBlackout on any object to change the numbers or the clip.
    ///
    /// The player can still move while blacked out. Nothing here kills or respawns them.
    /// </summary>
    [AddComponentMenu("Monster/Catch Blackout")]
    public class CatchBlackout : MonoBehaviour
    {
        const string ShaderResourcePath = "WakeUp/WakeUpEyelids";
        const string DefaultClipPath = "Monster/CatchImpact";
        static readonly int VisibilityId = Shader.PropertyToID("_Visibility");
        static readonly int CoverColorId = Shader.PropertyToID("_CoverColor");
        static readonly int BlurId = Shader.PropertyToID("_Blur");

        static CatchBlackout instance;

        /// <summary>The active CatchBlackout (null before the scene has loaded).</summary>
        public static CatchBlackout Instance => instance;

        /// <summary>Raised when the view has finished fading back in after a catch.</summary>
        public static event System.Action BlackoutEnded;

        [Tooltip("The sound played at the moment of the catch. Empty = Resources/Monster/CatchImpact.")]
        public AudioClip impactClip;

        [Range(0f, 1f)] public float volume = 1f;

        [Tooltip("Seconds the view stays fully black.")]
        [Min(0f)] public float blackSeconds = 3f;

        [Tooltip("Seconds the view takes to fade back in afterwards.")]
        [Min(0.01f)] public float fadeInSeconds = 0.5f;

        [Tooltip("Master switch.")]
        public bool enableBlackout = true;

        AudioSource source;
        GameObject overlay;
        Material overlayMaterial;
        Mesh overlayMesh;
        Camera overlayCamera;
        Coroutine running;
        bool autoCreated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (instance != null || FindFirstObjectByType<CatchBlackout>() != null)
                return;

            var host = new GameObject("Catch Blackout");
            DontDestroyOnLoad(host);
            host.AddComponent<CatchBlackout>().autoCreated = true;
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                // A component placed in a scene (with the user's settings) wins over the automatic one.
                if (instance.autoCreated)
                    Destroy(instance.gameObject);
                else
                {
                    Destroy(this);
                    return;
                }
            }
            instance = this;

            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.loop = false;
        }

        void OnEnable()
        {
            MonsterHunter.AnyTargetCaught += OnCaught;
        }

        void OnDisable()
        {
            MonsterHunter.AnyTargetCaught -= OnCaught;
            running = null;
            ReleaseOverlay();
        }

        void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }

        void OnCaught(MonsterHunter monster, HuntTarget target)
        {
            if (!enableBlackout || running != null || target == null || !target.isLocalPlayer)
                return;

            running = StartCoroutine(Run());
        }

        /// <summary>Plays the whole catch effect now (also usable from a Context Menu to try it).</summary>
        [ContextMenu("Test: Play Catch Blackout")]
        public void Play()
        {
            if (Application.isPlaying && running == null)
                running = StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            AudioClip clip = impactClip != null ? impactClip : Resources.Load<AudioClip>(DefaultClipPath);
            if (clip != null)
                source.PlayOneShot(clip, volume);

            if (BuildOverlay())
            {
                SetVisibility(0f);

                for (float t = 0f; t < blackSeconds; t += Time.unscaledDeltaTime)
                    yield return null;

                for (float t = 0f; t < fadeInSeconds; t += Time.unscaledDeltaTime)
                {
                    SetVisibility(Mathf.SmoothStep(0f, 1f, t / fadeInSeconds));
                    yield return null;
                }
            }

            ReleaseOverlay();
            running = null;
            BlackoutEnded?.Invoke();
        }

        bool BuildOverlay()
        {
            ReleaseOverlay();

            Camera cam = FindPlayerCamera();
            Shader shader = Resources.Load<Shader>(ShaderResourcePath);
            if (cam == null || shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[CatchBlackout] No camera or the Resources/" + ShaderResourcePath + " shader is missing; the sound plays but the screen will not go black.", this);
                return false;
            }

            overlayMaterial = new Material(shader) { name = "Catch Blackout (runtime)" };
            overlayMaterial.SetColor(CoverColorId, Color.black);
            overlayMaterial.SetFloat(BlurId, 0f);

            // One oversized triangle in clip space (the shader writes the vertices as they are), never culled.
            overlayMesh = new Mesh { name = "Catch Blackout Fullscreen Triangle" };
            overlayMesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 3f, 0f), new Vector3(3f, -1f, 0f) };
            overlayMesh.triangles = new[] { 0, 1, 2 };
            overlayMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

            overlayCamera = cam;
            overlay = new GameObject("Catch Blackout Overlay");
            overlay.layer = cam.gameObject.layer;
            overlay.transform.SetParent(cam.transform, false);
            overlay.AddComponent<MeshFilter>().sharedMesh = overlayMesh;

            var renderer = overlay.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = overlayMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;

            // Only the player's camera gets the overlay, not the scene view, previews or reflection cameras.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            return true;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (overlay != null)
                overlay.GetComponent<MeshRenderer>().enabled = cam == overlayCamera;
        }

        void SetVisibility(float visibility)
        {
            if (overlayMaterial != null)
                overlayMaterial.SetFloat(VisibilityId, visibility);
        }

        void ReleaseOverlay()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            if (overlay != null) Destroy(overlay);
            if (overlayMaterial != null) Destroy(overlayMaterial);
            if (overlayMesh != null) Destroy(overlayMesh);
            overlay = null;
            overlayMaterial = null;
            overlayMesh = null;
            overlayCamera = null;
        }

        static Camera FindPlayerCamera()
        {
            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
                return main;

            foreach (Camera c in Camera.allCameras)
            {
                if (c.stereoTargetEye != StereoTargetEyeMask.None)
                    return c;
            }
            return Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null;
        }
    }
}
