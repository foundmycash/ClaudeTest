using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace Ambience
{
    /// <summary>
    /// Every so often, at a random moment, plays one of five short, unsettling cave sounds from somewhere the player
    /// is probably not looking: a deep drone, a metal groan, a whistle through a crack, a far-off thud and something
    /// that is almost a breath. Quiet, dark and far away on purpose; it is meant to make you pause, not jump.
    ///
    /// The sounds are made in code when the game starts (see <see cref="CaveSoundSynth"/>), on a background thread, so
    /// there are no audio files to add. To use your own sound for any of the five, drop a clip into the matching slot
    /// of <see cref="customClips"/>; empty slots keep the generated sound.
    ///
    /// It starts by itself in play mode and in builds (<see cref="AutoStart"/>), so no scene has to contain it. To
    /// tune it for good, add this component to any object in a scene; that one is used instead of the automatic one.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Audio/Cave Ambience")]
    public class CaveAmbience : MonoBehaviour
    {
        /// <summary>Set to false to stop the system from starting itself. Then it only runs where you add the component.</summary>
        public static readonly bool AutoStart = true;

        public const int DefaultSeed = 1337;

        // The spot a sound is heard from is "outside the player's view" when it is further than this from straight ahead.
        const float ViewHalfAngle = 70f;

        [Header("Timing")]
        [Tooltip("Shortest and longest wait, in seconds, between two sounds. Each wait is a random time in between, so there is no pattern to learn.")]
        public Vector2 interval = new Vector2(45f, 150f);

        [Tooltip("The first sound comes after a random wait in this range (seconds), so it never lands while a scene is still loading.")]
        public Vector2 firstSoundDelay = new Vector2(25f, 70f);

        [Tooltip("Scenes by name where nothing plays (the wait just runs on). Empty = play everywhere.")]
        public string[] silentScenes = { "Loading" };

        [Header("Loudness")]
        [Tooltip("Overall level. The sounds are already quiet and far away; raise this if you cannot hear them over the headset volume, lower it to make them subtler.")]
        [Range(0f, 1f)] public float volume = 0.8f;

        [Tooltip("Each sound is played at a random level between volume x (1 - this) and volume.")]
        [Range(0f, 0.8f)] public float volumeVariation = 0.3f;

        [Tooltip("Each sound's pitch is random within 1 +/- this, so hearing the same one twice does not feel identical.")]
        [Range(0f, 0.3f)] public float pitchVariation = 0.06f;

        [Header("Where it comes from")]
        [Tooltip("Closest and furthest distance, in metres, from the player's head. Further sounds are also duller.")]
        public Vector2 distance = new Vector2(8f, 25f);

        [Tooltip("Chance that a sound comes from outside the area the player is facing (about 140 degrees straight ahead). 1 = always behind or beside you.")]
        [Range(0f, 1f)] public float fromBehindChance = 0.7f;

        [Tooltip("Use the project's audio spatializer (Meta XR Audio here) for proper 3D direction on headphones, if one is set up.")]
        public bool useSpatializer = true;

        [Header("The five sounds")]
        [Tooltip("Optional. Slot 1-5 replaces generated sound 1-5 (Hollow Drone, Metal Groan, Fissure Whistle, Far Thud, Wrong Breath); an empty slot keeps the generated one. Slots after 5 add more sounds.")]
        public AudioClip[] customClips = new AudioClip[CaveSoundSynth.SoundCount];

        [Tooltip("Sample rate of the generated sounds. 32000 is plenty for these dark sounds and keeps memory small (about 2.7 MB in total).")]
        [Min(8000)] public int sampleRate = 32000;

        [Tooltip("Changes the details of the generated sounds. The same seed always gives the same five sounds.")]
        public int seed = DefaultSeed;

        public static CaveAmbience Instance { get; private set; }

        /// <summary>Raised when a sound starts: its slot (0-4 are the generated ones) and where it is playing from.</summary>
        public event Action<int, Vector3> SoundPlayed;

        /// <summary>How many sounds are ready to be played (0 until the generation has finished).</summary>
        public int SoundCount => pool.Count;

        const int VoiceCount = 2;

        readonly List<AudioClip> pool = new List<AudioClip>();
        readonly List<AudioClip> generated = new List<AudioClip>();
        AudioSource[] voices;
        AudioLowPassFilter[] filters;
        AudioListener listener;
        int lastIndex = -1;
        bool autoCreated;
        Coroutine schedule;

        // Filled in by the worker thread; read on the main thread only after renderDone is set.
        float[][] rendered;
        string renderError;
        volatile bool renderDone;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (!AutoStart || Instance != null || FindFirstObjectByType<CaveAmbience>() != null)
                return;

            var host = new GameObject("Cave Ambience");
            DontDestroyOnLoad(host);
            host.AddComponent<CaveAmbience>().autoCreated = true;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // A component placed in a scene (with the user's settings) wins over the automatic one.
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
            BuildVoices();
        }

        IEnumerator Start()
        {
            if (Instance != this)
                yield break;

            if (NeedsGeneratedSounds())
            {
                StartRender();
                while (!renderDone)
                    yield return null;
                if (renderError != null)
                {
                    Debug.LogError("[CaveAmbience] Could not generate the sounds: " + renderError, this);
                    enabled = false;
                    yield break;
                }
            }

            BuildPool();
            if (pool.Count == 0)
                yield break;

            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            schedule = StartCoroutine(Schedule());
        }

        // Entering a scene starts a fresh, unpredictable wait, so nothing ever plays right after a load.
        void OnActiveSceneChanged(Scene from, Scene to)
        {
            if (schedule != null)
                StopCoroutine(schedule);
            schedule = StartCoroutine(Schedule());
        }

        IEnumerator Schedule()
        {
            yield return new WaitForSeconds(Random.Range(firstSoundDelay.x, firstSoundDelay.y));
            while (true)
            {
                if (!InSilentScene())
                    PlayNow();
                yield return new WaitForSeconds(Random.Range(interval.x, interval.y));
            }
        }
        void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            if (Instance == this)
                Instance = null;
            foreach (AudioClip clip in generated)
                if (clip != null)
                    Destroy(clip);
        }

        void OnValidate()
        {
            interval.x = Mathf.Max(1f, interval.x);
            interval.y = Mathf.Max(interval.x, interval.y);
            firstSoundDelay.x = Mathf.Max(0f, firstSoundDelay.x);
            firstSoundDelay.y = Mathf.Max(firstSoundDelay.x, firstSoundDelay.y);
            distance.x = Mathf.Max(1f, distance.x);
            distance.y = Mathf.Max(distance.x, distance.y);
        }

        /// <summary>Plays a random sound right now (never the same one twice in a row), from a random unseen spot.</summary>
        [ContextMenu("Play a random sound now")]
        public void PlayNow()
        {
            if (pool.Count == 0)
                return;

            int index = pool.Count == 1 ? 0 : Random.Range(0, pool.Count - 1);
            if (pool.Count > 1 && lastIndex >= 0 && index >= lastIndex)
                index++;
            PlayNow(index);
        }

        /// <summary>Plays sound <paramref name="index"/> (0-4 are the generated ones, unless replaced) right now.</summary>
        public void PlayNow(int index)
        {
            if (index < 0 || index >= pool.Count || !TryGetListener(out Transform head))
                return;

            lastIndex = index;
            Vector3 position = PickPosition(head, out float dist);

            AudioSource voice = voices[0];
            int slot = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                if (!voices[i].isPlaying)
                {
                    voice = voices[i];
                    slot = i;
                    break;
                }
            }

            // Further away = duller: the high end of a distant sound is the first thing the rock soaks up.
            float far = Mathf.InverseLerp(distance.x, distance.y, dist);
            filters[slot].cutoffFrequency = Mathf.Lerp(8000f, 2500f, far);

            voice.minDistance = distance.x;
            voice.maxDistance = distance.y + 20f;
            voice.transform.position = position;
            voice.clip = pool[index];
            voice.volume = volume * Random.Range(1f - volumeVariation, 1f);
            voice.pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
            voice.Play();

            SoundPlayed?.Invoke(index, position);
        }

        bool NeedsGeneratedSounds()
        {
            for (int i = 0; i < CaveSoundSynth.SoundCount; i++)
                if (CustomClip(i) == null)
                    return true;
            return false;
        }

        AudioClip CustomClip(int i) => customClips != null && i < customClips.Length ? customClips[i] : null;

        void StartRender()
        {
            int rate = sampleRate, s = seed;
            var worker = new Thread(() =>
            {
                try { rendered = CaveSoundSynth.RenderAll(rate, s); }
                catch (Exception e) { renderError = e.ToString(); }
                renderDone = true;
            })
            {
                Name = "CaveAmbience synth",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            worker.Start();
        }

        void BuildPool()
        {
            pool.Clear();
            int slots = Mathf.Max(CaveSoundSynth.SoundCount, customClips != null ? customClips.Length : 0);
            for (int i = 0; i < slots; i++)
            {
                AudioClip custom = CustomClip(i);
                if (custom != null)
                {
                    pool.Add(custom);
                }
                else if (i < CaveSoundSynth.SoundCount && rendered != null)
                {
                    AudioClip clip = AudioClip.Create("Cave " + CaveSoundSynth.Names[i], rendered[i].Length, 1, sampleRate, false);
                    clip.SetData(rendered[i], 0);
                    generated.Add(clip);
                    pool.Add(clip);
                }
            }
            rendered = null;
        }

        void BuildVoices()
        {
            bool spatialize = useSpatializer && !string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName());
            voices = new AudioSource[VoiceCount];
            filters = new AudioLowPassFilter[VoiceCount];
            for (int i = 0; i < VoiceCount; i++)
            {
                var go = new GameObject("Voice " + i);
                go.transform.SetParent(transform, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.spatialize = spatialize;
                source.priority = 200; // gameplay sounds win if the system runs out of voices
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                voices[i] = source;
                filters[i] = go.AddComponent<AudioLowPassFilter>();
            }
        }

        bool InSilentScene()
        {
            if (silentScenes == null || silentScenes.Length == 0)
                return false;
            string active = SceneManager.GetActiveScene().name;
            foreach (string name in silentScenes)
                if (name == active)
                    return true;
            return false;
        }

        // The sound is heard from wherever the player's AudioListener is (the head, in VR).
        bool TryGetListener(out Transform head)
        {
            if (listener == null || !listener.isActiveAndEnabled)
                listener = FindFirstObjectByType<AudioListener>();
            head = listener != null ? listener.transform : null;
            return head != null;
        }

        Vector3 PickPosition(Transform head, out float dist)
        {
            Vector3 ahead = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            ahead = ahead.sqrMagnitude > 0.01f ? ahead.normalized : Vector3.forward;

            // Angle from straight ahead; with the "behind" chance, never inside the cone the player is looking at.
            float yaw = Random.value < fromBehindChance
                ? Random.Range(ViewHalfAngle, 360f - ViewHalfAngle)
                : Random.Range(0f, 360f);
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * ahead;

            // A little below to well above the head: caves are tall, and sound from above is the hardest to place.
            float elevation = Random.Range(-10f, 30f);
            dir = Quaternion.AngleAxis(-elevation, Vector3.Cross(Vector3.up, dir)) * dir;

            dist = Random.Range(distance.x, Mathf.Max(distance.x, distance.y));
            return head.position + dir * dist;
        }
    }
}
