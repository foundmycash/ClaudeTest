using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoadingScreen
{
    /// <summary>
    /// Runs the Loading scene from start to finish:
    ///
    ///   loading screen while the main scene loads in the background
    ///   -> the screen cracks and crumbles away -> short pause -> dynamite is thrown from behind the player onto the wall
    ///   -> beeping countdown -> explosion -> the fireball swallows the camera
    ///   -> <see cref="SceneTransition"/> switches to the main scene behind the fire and burns the fire away.
    ///
    /// The main scene is loaded additively with activation held back, so its objects only wake up while the fire
    /// hides everything; nothing of the main scene runs, renders or plays sound before that. Background loading is
    /// throttled while the loading screen is visible (smooth frame rate) and unthrottled while the fire covers the view.
    ///
    /// The progress bar is the weighted sum of the real work: the main scene's load, the cinematic's sounds (set to
    /// load in the background, so the Loading scene itself opens instantly) and getting the effects' shaders ready.
    /// Unity reports a single number for the scene load, so "environment / textures / gameplay assets" are the stages
    /// of that number.
    /// </summary>
    [DisallowMultipleComponent]
    public class LoadingSequence : MonoBehaviour
    {
        public enum Phase { Starting, Loading, Ready, ScreenExit, Pause, Throw, Countdown, Explosion, HandedOver, Failed }

        // Share of the progress bar each piece of work takes, in the order the status line names them.
        const float EnvironmentWeight = 0.30f, TexturesWeight = 0.30f, GameplayWeight = 0.22f;
        const float SceneWeight = EnvironmentWeight + TexturesWeight + GameplayWeight;
        const float AudioWeight = 0.08f, EffectsWeight = 0.10f;
        const float PrewarmTimeout = 4f;   // never hold the loading screen up for the shader warm-up

        [Header("Scene")]
        [Tooltip("Scene loaded behind the loading screen and revealed by the explosion. Must be in the build's scene list.")]
        public string mainScene = "ClaudeTest";

        [Header("Parts")]
        public LoadingScreenUI ui;
        public Dynamite dynamite;
        public ExplosionEffect explosion;
        public SceneTransition transition;

        [Tooltip("The wall the dynamite is thrown at.")]
        public Collider wall;

        [Tooltip("The player's camera. Empty uses this scene's main camera.")]
        public Camera viewCamera;

        [Tooltip("Sounds of the cinematic. They are loaded while the loading screen shows; import them with 'Preload Audio Data' off and 'Load In Background' on.")]
        public AudioClip[] cinematicAudio;

        [Header("Loading screen")]
        [Tooltip("The loading screen stays up at least this long, so a quick load does not just flash past.")]
        [Min(0f)] public float minimumLoadingTime = 3f;

        [Tooltip("Pause on 100% before the loading screen leaves.")]
        [Min(0f)] public float holdAtComplete = 0.45f;

        [Tooltip("Main-thread time background loading may use per frame while the loading screen shows. Lower keeps the frame rate smoother but loads slower.")]
        public ThreadPriority loadingPriority = ThreadPriority.BelowNormal;

        [Header("Cinematic")]
        [Tooltip("Quiet moment between the loading screen leaving and the dynamite flying in.")]
        [Min(0f)] public float pauseBeforeThrow = 0.7f;

        [Tooltip("Where the dynamite lands, relative to the spot on the wall straight ahead of the player: x = right, y = up (metres).")]
        public Vector2 targetOffset = new Vector2(0.15f, -0.12f);

        /// <summary>The sequence in the loaded Loading scene, if any (for tools and tests).</summary>
        public static LoadingSequence Current { get; private set; }

        public Phase CurrentPhase { get; private set; } = Phase.Starting;

        void Awake()
        {
            Current = this;
        }

        void OnDestroy()
        {
            if (Current == this)
                Current = null;
        }

        IEnumerator Start()
        {
            if (viewCamera == null)
                viewCamera = SceneTransition.FindSceneCamera(gameObject.scene);
            if (viewCamera == null)
            {
                Fail("No camera in the Loading scene.");
                yield break;
            }
            Transform head = viewCamera.transform;

            ThreadPriority previousPriority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = loadingPriority;

            CurrentPhase = Phase.Loading;
            ui.Show(head);

            // The big job first; the small ones run alongside it.
            AsyncOperation sceneLoad = SceneManager.LoadSceneAsync(mainScene, LoadSceneMode.Additive);
            if (sceneLoad == null)
            {
                Application.backgroundLoadingPriority = previousPriority;
                Fail($"Scene '{mainScene}' could not be loaded. Is it in the build's scene list?");
                yield break;
            }
            sceneLoad.allowSceneActivation = false;

            foreach (AudioClip clip in cinematicAudio)
            {
                if (clip != null && clip.loadState == AudioDataLoadState.Unloaded)
                    clip.LoadAudioData();
            }

            bool effectsReady = false;
            StartCoroutine(PrepareEffects(() => effectsReady = true));

            float shownSince = Time.unscaledTime;
            while (true)
            {
                float scene = Mathf.Clamp01(sceneLoad.progress / 0.9f);   // stops at 0.9 until activation
                float audio = AudioProgress();
                bool done = scene >= 1f && audio >= 1f && effectsReady;
                float actual = done ? 1f : scene * SceneWeight + audio * AudioWeight + (effectsReady ? EffectsWeight : 0f);

                // A quick load still gets a calm, readable bar: it fills over minimumLoadingTime at the fastest.
                float pace = minimumLoadingTime > 0f ? (Time.unscaledTime - shownSince) / minimumLoadingTime : 1f;
                ui.SetProgress(Mathf.Min(actual, pace));
                ui.SetStatus(StatusFor(ui.DisplayedProgress));

                if (done && ui.DisplayedProgress >= 1f)
                    break;
                yield return null;
            }

            CurrentPhase = Phase.Ready;
            ui.SetStatus("Ready", false);
            yield return Wait(holdAtComplete);

            CurrentPhase = Phase.ScreenExit;
            yield return ui.Exit();

            CurrentPhase = Phase.Pause;
            yield return Wait(pauseBeforeThrow);

            // Dynamite: thrown at the wall in front of the player, then the countdown.
            CurrentPhase = Phase.Throw;
            if (!FindWallSpot(head, out Vector3 spot, out Vector3 normal))
            {
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                normal = -forward;
                spot = head.position + forward * 3f;
            }
            Vector3 right = Vector3.Cross(Vector3.up, -normal).normalized;
            spot += right * targetOffset.x + Vector3.up * targetOffset.y;
            yield return dynamite.Throw(head, spot, normal);

            CurrentPhase = Phase.Countdown;
            double detonation = dynamite.StartCountdown();
            transition.ScheduleBoom(detonation);   // the boom lands on the exact sample the countdown ends
            while (!dynamite.ReachedDetonation)
                yield return null;

            // Explosion: the fireball surges at the player and the full-screen fire closes in with it.
            CurrentPhase = Phase.Explosion;
            Vector3 blast = dynamite.transform.position;
            dynamite.Detonate();
            explosion.Detonate(blast, normal, head);
            transition.BeginApproach(viewCamera);
            transition.Flash(0.45f);
            LoadingHaptics.Pulse(1f, 0.5f);

            while (!explosion.Engulfed)
            {
                transition.SetApproach(explosion.EngulfAmount, explosion.GlowAmount);
                yield return null;
            }
            transition.SetApproach(1f, 0f);
            explosion.Stop();   // fully hidden behind the fire from here on

            CurrentPhase = Phase.HandedOver;
            transition.HandOver(sceneLoad, mainScene, gameObject.scene, previousPriority);
        }

        // Draw everything the cinematic will show once, where the camera looks but cannot see (just behind the wall),
        // so shaders compile now and not in the middle of the explosion. Waits for the player to face the wall.
        IEnumerator PrepareEffects(System.Action done)
        {
            yield return transition.Prewarm(viewCamera);

            float waited = 0f;
            Vector3 hidden = default;
            while (waited < PrewarmTimeout && !LookingAtWall(out hidden))
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (waited < PrewarmTimeout)
            {
                yield return explosion.Prewarm(hidden);
                yield return dynamite.Prewarm(hidden);
            }
            done();
        }

        bool LookingAtWall(out Vector3 hiddenPoint)
        {
            hiddenPoint = default;
            Transform head = viewCamera.transform;
            if (wall == null || !wall.Raycast(new Ray(head.position, head.forward), out RaycastHit hit, 50f))
                return false;
            hiddenPoint = hit.point - hit.normal * 1.5f;
            return true;
        }

        // Spot on the wall straight ahead of the player at eye height (or, if they look away, the nearest one).
        bool FindWallSpot(Transform head, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            if (wall == null)
                return false;

            Vector3 origin = head.position;
            Vector3 look = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (look.sqrMagnitude > 0.5f && wall.Raycast(new Ray(origin, look), out RaycastHit hit, 50f)
                && Vector3.Angle(-look, hit.normal) < 55f)
            {
                point = hit.point;
                normal = hit.normal;
                return true;
            }

            Vector3 toWall = Vector3.ProjectOnPlane(wall.bounds.center - origin, Vector3.up).normalized;
            if (!wall.Raycast(new Ray(origin, toWall), out hit, 50f))
                return false;
            Vector3 straight = -Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
            if (wall.Raycast(new Ray(origin, straight), out RaycastHit square, 50f))
                hit = square;
            point = hit.point;
            normal = hit.normal;
            return true;
        }

        float AudioProgress()
        {
            int total = 0, ready = 0;
            foreach (AudioClip clip in cinematicAudio)
            {
                if (clip == null)
                    continue;
                total++;
                AudioDataLoadState state = clip.loadState;
                if (state == AudioDataLoadState.Loaded || state == AudioDataLoadState.Failed)
                    ready++;
            }
            return total == 0 ? 1f : ready / (float)total;
        }

        static string StatusFor(float shown)
        {
            if (shown < EnvironmentWeight) return "Loading environment";
            if (shown < EnvironmentWeight + TexturesWeight) return "Loading textures";
            if (shown < SceneWeight) return "Loading gameplay assets";
            if (shown < SceneWeight + AudioWeight) return "Loading audio";
            return "Preparing effects";
        }

        static IEnumerator Wait(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
                yield return null;
        }

        void Fail(string reason)
        {
            CurrentPhase = Phase.Failed;
            Debug.LogError("[Loading] " + reason, this);
            if (ui != null && ui.enabled)
                ui.SetStatus("Something went wrong while loading", false);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (string.IsNullOrEmpty(mainScene))
                return;
            foreach (UnityEditor.EditorBuildSettingsScene s in UnityEditor.EditorBuildSettings.scenes)
            {
                if (s.enabled && System.IO.Path.GetFileNameWithoutExtension(s.path) == mainScene)
                    return;
            }
            Debug.LogWarning($"[Loading] '{mainScene}' is not an enabled scene in the build's scene list, so it cannot be loaded.", this);
        }
#endif
    }
}
