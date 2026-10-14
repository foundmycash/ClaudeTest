using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ambience
{
    /// <summary>
    /// Quiet looping wind under everything: Resources/Ambience/Wind, played flat (not 3D) at a low volume, faded in
    /// when the game starts and faded out in the scenes listed in <see cref="silentScenes"/>.
    ///
    /// Starts by itself (<see cref="AutoStart"/>), like <see cref="CaveAmbience"/>. To tune it, add this component to
    /// any object in a scene; that one replaces the automatic one. To use another sound, assign <see cref="clip"/> or
    /// replace the file.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Audio/Wind Ambience")]
    public class WindAmbience : MonoBehaviour
    {
        /// <summary>Set to false to stop the wind from starting itself.</summary>
        public static readonly bool AutoStart = true;

        const string DefaultClipPath = "Ambience/Wind";

        [Tooltip("Empty = Resources/Ambience/Wind.")]
        public AudioClip clip;

        [Tooltip("Loudness. Kept low so the wind stays a background bed.")]
        [Range(0f, 1f)] public float volume = 0.15f;

        [Tooltip("Seconds to fade in at the start and to fade in or out when changing scene.")]
        [Min(0.01f)] public float fadeSeconds = 3f;

        [Tooltip("Scenes by name where the wind is faded out.")]
        public string[] silentScenes = { "Loading" };

        public static WindAmbience Instance { get; private set; }

        AudioSource source;
        bool autoCreated;
        float level;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (!AutoStart || Instance != null || FindFirstObjectByType<WindAmbience>() != null)
                return;

            var host = new GameObject("Wind Ambience");
            DontDestroyOnLoad(host);
            host.AddComponent<WindAmbience>().autoCreated = true;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                if (Instance.autoCreated)
                {
                    Destroy(Instance.gameObject);
                }
                else
                {
                    Destroy(this);
                    return;
                }
            }

            Instance = this;
            if (!autoCreated)
                DontDestroyOnLoad(gameObject);

            if (clip == null)
                clip = Resources.Load<AudioClip>(DefaultClipPath);
            if (clip == null)
            {
                Debug.LogWarning("[WindAmbience] No clip found at Resources/" + DefaultClipPath, this);
                enabled = false;
                return;
            }

            source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.volume = 0f;
            source.bypassReverbZones = true;
            source.ignoreListenerPause = false;
            source.Play();
        }

        void Update()
        {
            bool silent = false;
            string scene = SceneManager.GetActiveScene().name;
            for (int i = 0; i < silentScenes.Length; i++)
                if (silentScenes[i] == scene) { silent = true; break; }

            level = Mathf.MoveTowards(level, silent ? 0f : 1f, Time.unscaledDeltaTime / fadeSeconds);
            source.volume = volume * level;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
