using System;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace MonsterAI
{
    public enum MicStatus
    {
        /// <summary>Not listening (switched off, or nobody asked yet).</summary>
        Off,
        /// <summary>Android is showing, or about to show, the microphone permission dialog.</summary>
        WaitingForPermission,
        /// <summary>The player said no. Vision and proximity still work; checked again now and then in case it is granted in the system settings.</summary>
        PermissionDenied,
        /// <summary>This device has no microphone (or the system hides it).</summary>
        NoDevice,
        /// <summary>The microphone was asked to start and has not delivered audio yet.</summary>
        Starting,
        /// <summary>Listening.</summary>
        Listening,
        /// <summary>The microphone could not be started or stopped delivering audio. It tries again after a pause.</summary>
        Failed,
    }

    /// <summary>
    /// Listens to the player's microphone for loud noise, and raises <see cref="NoiseDetected"/> when they make
    /// some. It measures loudness only (see <see cref="NoiseAnalyzer"/>); it does not recognise speech.
    ///
    /// Privacy: the audio never leaves this script. The microphone writes into a one second looping buffer in memory,
    /// the loudness of each 20 ms is measured and the samples are never copied, stored, saved to a file or sent
    /// anywhere. The buffer is released when listening stops.
    ///
    /// Without a microphone (no device, permission denied, an error) this does nothing and says so once in the
    /// Console; the monster's vision and proximity keep working. The component lives for the whole game and starts by
    /// itself when a monster asks for it (<see cref="EnsureExists"/>). Add it to an object yourself to change the
    /// settings. On Android the player is asked for permission (Quest shows a system dialog the first time).
    ///
    /// Only one thing can have the microphone open. If you later add voice chat, either feed this its audio with
    /// <see cref="Feed"/> and turn "Use Device Microphone" off, or read the level from the voice chat instead.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Monster/Microphone Noise Detector")]
    public class MicrophoneNoiseDetector : MonoBehaviour
    {
        [Header("Microphone")]
        [Tooltip("Open the device microphone. Turn off to feed audio yourself with Feed() (voice chat, tests).")]
        public bool useDeviceMicrophone = true;

        [Tooltip("The microphone to use. Empty = the default one (the headset on a Quest).")]
        public string deviceName = "";

        [Tooltip("Sample rate to ask for. Voice loudness does not need more; the device may pick another one.")]
        [Range(8000, 48000)] public int sampleRate = 16000;

        [Tooltip("Ask for microphone permission as soon as listening is wanted (Android / Quest). Turn off if you want to ask at a better moment yourself by calling StartListening().")]
        public bool requestPermission = true;

        [Header("What counts as loud")]
        [Tooltip("Loudness in dB (0 = the loudest the microphone can record) the sound must reach. A quiet room is about -60, normal speech -35 to -25, shouting above -20. Raise it to make the monster less sensitive. The Console shows nothing; look at Level Db on this component while the game runs.")]
        [Range(-60f, 0f)] public float thresholdDb = -28f;

        [Tooltip("The sound must also be this many dB above the room's own noise (tracked automatically), so a noisy room does not set it off.")]
        [Range(0f, 40f)] public float minAboveFloorDb = 12f;

        [Tooltip("The noise must last this long, in seconds. Claps, clicks and controller knocks are shorter; a shout is longer.")]
        [Range(0.02f, 1f)] public float minLoudSeconds = 0.15f;

        [Tooltip("Seconds after the microphone starts delivering audio in which nothing is reported. It still measures, so the room's noise floor is learned first. Covers the start-up click and whatever is playing (loading transition) when the scene begins.")]
        [Range(0f, 10f)] public float warmupSeconds = 4f;

        [Tooltip("Seconds after a detection before another one can be reported.")]
        [Min(0f)] public float cooldownSeconds = 1.5f;

        [Tooltip("Frequencies below this (Hz) are ignored: wind, rumble, a bumped headset. 0 = off.")]
        [Range(0f, 400f)] public float highPassHz = 120f;

        [Tooltip("How fast the noise floor may rise in dB per second, so a steady background hum is learned as 'normal'.")]
        [Range(0.1f, 20f)] public float floorRiseDbPerSecond = 2f;

        [Header("Read only (while the game runs)")]
        [SerializeField] MicStatus status = MicStatus.Off;
        [SerializeField] string statusMessage = "";
        [SerializeField] float levelDb = NoiseAnalyzer.SilenceDb;
        [SerializeField] float floorDb = NoiseAnalyzer.SilenceDb;

        const int ClipSeconds = 1;
        const float RetrySeconds = 10f;
        const float PermissionPollSeconds = 5f;
        const float StartTimeoutSeconds = 3f;

        public static MicrophoneNoiseDetector Instance { get; private set; }

        /// <summary>Raised when the player makes a loud noise. The argument is the smoothed level in dB.</summary>
        public event Action<float> NoiseDetected;

        public MicStatus Status => status;
        public string StatusMessage => statusMessage;
        public bool IsListening => status == MicStatus.Listening;
        public float LevelDb => levelDb;
        public float FloorDb => floorDb;
        public NoiseAnalyzer Analyzer => analyzer;

        readonly NoiseAnalyzer analyzer = new NoiseAnalyzer();
        bool wanted;
        string device;                  // null = default microphone
        AudioClip clip;
        int clipSamples;
        int usedRate;
        int readPos;
        float[] chunk;
        float nextAttempt;
        float startedAt;
        float warmupUntil;
        bool permissionRequested;
        MicStatus loggedStatus = MicStatus.Off;
        float nextWarning;
        bool injectedWarned;

        /// <summary>Returns the detector, creating one on a new object that survives scene loads if there is none.</summary>
        public static MicrophoneNoiseDetector EnsureExists()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject("Microphone Noise Detector");
            DontDestroyOnLoad(go);
            return go.AddComponent<MicrophoneNoiseDetector>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("MicrophoneNoiseDetector: there is already one (" + Instance.name + "). This extra one is removed.", this);
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void OnEnable()
        {
            if (Instance == this && wanted)
                nextAttempt = 0f;
        }

        void OnDisable()
        {
            ReleaseMicrophone();
            if (status != MicStatus.Off)
                SetStatus(MicStatus.Off, "");
        }

        void OnDestroy()
        {
            ReleaseMicrophone();
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Starts listening (and asks for permission on Android). Safe to call again.</summary>
        public void StartListening()
        {
            wanted = true;
            if (!enabled)
                enabled = true;
            if (status == MicStatus.Off)
                nextAttempt = 0f;
        }

        /// <summary>Stops listening and gives the microphone back.</summary>
        public void StopListening()
        {
            wanted = false;
            ReleaseMicrophone();
            SetStatus(MicStatus.Off, "");
        }

        /// <summary>
        /// Gives the detector audio from somewhere else (voice chat, a test). Samples are mono, -1..1. A loud noise is
        /// reported the same way as from the device microphone.
        /// </summary>
        public void Feed(float[] samples, int count, int rate)
        {
            if (useDeviceMicrophone && status == MicStatus.Listening)
            {
                if (!injectedWarned)
                {
                    injectedWarned = true;
                    Debug.LogWarning("MicrophoneNoiseDetector.Feed was called while the device microphone is also listening; both streams are mixed into one level. Turn off 'Use Device Microphone'.", this);
                }
            }
            if (samples == null || rate <= 0)
                return;

            // Same 20 ms pieces the device microphone is measured in.
            int piece = Mathf.Max(64, rate / 50);
            for (int offset = 0; offset < count; offset += piece)
                Process(samples, offset, Mathf.Min(piece, count - offset), rate);
        }

        void ApplySettings()
        {
            analyzer.thresholdDb = thresholdDb;
            analyzer.minAboveFloorDb = minAboveFloorDb;
            analyzer.minLoudSeconds = minLoudSeconds;
            analyzer.cooldownSeconds = cooldownSeconds;
            analyzer.highPassHz = highPassHz;
            analyzer.floorRiseDbPerSecond = floorRiseDbPerSecond;
        }

        void Update()
        {
            if (!useDeviceMicrophone || !wanted)
                return;

            switch (status)
            {
                case MicStatus.Listening:
                    Pump();
                    break;

                case MicStatus.Starting:
                    CheckStarted();
                    break;

                default:
                    if (Time.unscaledTime >= nextAttempt)
                        TryStart();
                    break;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Starting

        // The Microphone API throws for a device it cannot open, so every call into it is guarded: a broken
        // microphone becomes one quiet warning and a retry later, never an error every frame.
        void Fail(string message)
        {
            ReleaseMicrophone();
            SetStatus(MicStatus.Failed, message + " Sound detection is off for now (trying again in " + RetrySeconds + " s); the monster still sees and senses you.");
            nextAttempt = Time.unscaledTime + RetrySeconds;
        }

        void TryStart()
        {
            if (!HasPermission())
                return;

            try
            {
                string[] devices = Microphone.devices;
                if (devices == null || devices.Length == 0)
                {
                    SetStatus(MicStatus.NoDevice, "No microphone found on this device. Sound detection is off; the monster still sees and senses you.");
                    nextAttempt = Time.unscaledTime + RetrySeconds;
                    return;
                }

                device = string.IsNullOrEmpty(deviceName) ? null : deviceName;

                int min, max;
                Microphone.GetDeviceCaps(device, out min, out max);
                int rate = sampleRate;
                if (max > 0)
                    rate = Mathf.Clamp(rate, Mathf.Max(min, 8000), max);

                clip = Microphone.Start(device, true, ClipSeconds, rate);
                if (clip == null)
                {
                    Fail("The microphone could not be started.");
                    return;
                }

                usedRate = clip.frequency > 0 ? clip.frequency : rate;
                clipSamples = clip.samples;
                int chunkSize = Mathf.Max(64, usedRate / 50);           // 20 ms
                if (chunk == null || chunk.Length != chunkSize)
                    chunk = new float[chunkSize];
                readPos = 0;
                startedAt = Time.unscaledTime;
                ApplySettings();
                analyzer.Reset();
                SetStatus(MicStatus.Starting, "");
            }
            catch (Exception e)
            {
                Fail("The microphone could not be opened (" + e.Message + ").");
            }
        }

        void CheckStarted()
        {
            try
            {
                int pos = Microphone.GetPosition(device);
                if (pos > 0)
                {
                    readPos = pos;
                    warmupUntil = Time.unscaledTime + warmupSeconds;
                    SetStatus(MicStatus.Listening, "Listening (" + usedRate + " Hz).");
                    return;
                }
            }
            catch (Exception e)
            {
                Fail("The microphone stopped working (" + e.Message + ").");
                return;
            }

            if (Time.unscaledTime - startedAt > StartTimeoutSeconds)
                Fail("The microphone started but delivered no audio.");
        }

        bool HasPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                return true;
            }

            if (!requestPermission)
            {
                SetStatus(MicStatus.Off, "");
                nextAttempt = Time.unscaledTime + PermissionPollSeconds;
                return false;
            }

            if (!permissionRequested)
            {
                permissionRequested = true;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionDenied += _ => OnPermissionAnswer(false);
                callbacks.PermissionDeniedAndDontAskAgain += _ => OnPermissionAnswer(false);
                callbacks.PermissionGranted += _ => OnPermissionAnswer(true);
                SetStatus(MicStatus.WaitingForPermission, "Asking for microphone permission.");
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
            }
            else if (status != MicStatus.PermissionDenied)
            {
                SetStatus(MicStatus.WaitingForPermission, "Waiting for the microphone permission dialog.");
            }
            // Poll again later: it may have been granted from the system settings.
            nextAttempt = Time.unscaledTime + (status == MicStatus.PermissionDenied ? PermissionPollSeconds : 0.5f);
            return false;
#else
            return true;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void OnPermissionAnswer(bool granted)
        {
            if (granted)
            {
                nextAttempt = 0f;
                SetStatus(MicStatus.Off, "");
            }
            else
            {
                SetStatus(MicStatus.PermissionDenied, "Microphone permission was denied. Sound detection is off; the monster still sees and senses you.");
                nextAttempt = Time.unscaledTime + PermissionPollSeconds;
            }
        }
#endif

        // ---------------------------------------------------------------------------------------------------------
        // Reading

        void Pump()
        {
            try
            {
                ReadAvailable();
            }
            catch (Exception e)
            {
                Fail("The microphone stopped working (" + e.Message + ").");
            }
        }

        void ReadAvailable()
        {
            if (clip == null || !Microphone.IsRecording(device))
            {
                Fail("The microphone stopped delivering audio.");
                return;
            }

            int pos = Microphone.GetPosition(device);
            int available = pos - readPos;
            if (available < 0)
                available += clipSamples;

            // Fell far behind (the app was paused): skip to now instead of replaying old sound.
            if (available > clipSamples / 2)
            {
                readPos = pos;
                analyzer.ResumeAfterGap();
                return;
            }

            int size = chunk.Length;
            while (available >= size)
            {
                // The ring wraps once a second; a chunk that would straddle the end is skipped (20 ms of sound).
                if (readPos + size > clipSamples)
                {
                    available -= clipSamples - readPos;
                    readPos = 0;
                    continue;
                }

                if (!clip.GetData(chunk, readPos))
                {
                    break;
                }
                readPos += size;
                if (readPos >= clipSamples)
                    readPos -= clipSamples;
                available -= size;

                Process(chunk, 0, size, usedRate);
            }
        }

        void Process(float[] samples, int offset, int count, int rate)
        {
            ApplySettings();
            bool detected = analyzer.ProcessChunk(samples, offset, count, rate);
            levelDb = analyzer.LevelDb;
            floorDb = analyzer.FloorDb;
            // Only the device microphone warms up; injected audio (Feed, tests) is reported at once.
            if (detected && !(status == MicStatus.Listening && Time.unscaledTime < warmupUntil))
                NoiseDetected?.Invoke(levelDb);
        }

        void ReleaseMicrophone()
        {
            if (clip == null)
                return;

            try
            {
                if (Microphone.IsRecording(device))
                    Microphone.End(device);
            }
            catch (Exception)
            {
                // nothing to release
            }
            // The 1 s ring buffer is the only copy of the audio that ever existed; drop it.
            Destroy(clip);
            clip = null;
        }

        // The microphone is released when the app goes to the background (taking the headset off) and re-opened on return.
        void OnApplicationPause(bool paused)
        {
            if (!wanted || !useDeviceMicrophone)
                return;
            if (paused)
            {
                ReleaseMicrophone();
                if (status == MicStatus.Listening || status == MicStatus.Starting)
                    status = MicStatus.Off;
            }
            else
            {
                nextAttempt = 0f;
            }
        }

        void SetStatus(MicStatus newStatus, string message)
        {
            status = newStatus;
            statusMessage = message;
            // A warning when it changes into a problem state, and at most one a minute however often it retries; never one per frame.
            bool problem = newStatus == MicStatus.PermissionDenied || newStatus == MicStatus.NoDevice || newStatus == MicStatus.Failed;
            if (problem && (newStatus != loggedStatus || Time.unscaledTime >= nextWarning))
            {
                nextWarning = Time.unscaledTime + 60f;
                Debug.LogWarning("MicrophoneNoiseDetector: " + message, this);
            }
            if (newStatus != MicStatus.Starting)
                loggedStatus = newStatus;
        }

        void OnValidate()
        {
            if (Application.isPlaying)
                ApplySettings();
        }
    }
}
