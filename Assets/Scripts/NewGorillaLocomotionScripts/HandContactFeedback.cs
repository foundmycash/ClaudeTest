namespace GorillaLocomotion
{
    using UnityEngine;
    using UnityEngine.XR;

    /// <summary>
    /// Makes a hand landing on a surface felt and heard. Listens to <see cref="Player.HandContact"/>, which fires once
    /// on the frame a hand goes from free to touching, so nothing repeats while the hand rests on or slides along the
    /// surface.
    ///
    /// Strength comes from the hand's speed straight into the surface (m/s), the same quantity that decides how hard a
    /// real hit is. It maps smoothly onto the controller's vibration amplitude and length, and onto the volume. The
    /// sound is one clip with a small random pitch change per hit.
    ///
    /// Local only: haptics are for this player's own controllers and the sound is not networked.
    ///
    /// <see cref="Player"/> adds this by itself with the defaults below. To tune it, add the component to the rig
    /// yourself (the Player then leaves it alone). To use another sound, assign <see cref="clip"/>, or replace
    /// Resources/HandContact/HandImpact.wav.
    /// </summary>
    [DisallowMultipleComponent]
    public class HandContactFeedback : MonoBehaviour
    {
        const string DefaultClipPath = "HandContact/HandImpact";

        [Header("Impact strength")]
        [Tooltip("Hand speed into the surface (m/s) below which a touch gives no feedback at all.")]
        [Min(0f)] public float minImpactSpeed = 0.3f;

        [Tooltip("Hand speed into the surface (m/s) at which feedback is at full strength.")]
        [Min(0.01f)] public float maxImpactSpeed = 4f;

        [Tooltip("How strength grows with speed. 1 = proportional. Below 1 light taps are felt more, above 1 only hard hits stand out.")]
        [Range(0.3f, 3f)] public float responseExponent = 0.75f;

        [Header("Haptics")]
        public bool haptics = true;
        [Tooltip("Vibration amplitude of the lightest hit that still counts, and of the hardest.")]
        [Range(0f, 1f)] public float minAmplitude = 0.1f;
        [Range(0f, 1f)] public float maxAmplitude = 1f;
        [Tooltip("Vibration length in seconds for the lightest and the hardest hit.")]
        [Min(0.005f)] public float minDuration = 0.02f;
        [Min(0.005f)] public float maxDuration = 0.08f;

        [Header("Sound")]
        public bool sound = true;
        [Tooltip("Impact sound. Empty = Resources/HandContact/HandImpact.")]
        public AudioClip clip;
        [Tooltip("Each hit's pitch is random within 1 +/- this.")]
        [Range(0f, 0.3f)] public float pitchVariation = 0.06f;
        [Tooltip("Volume of the lightest hit that still counts, and of the hardest.")]
        [Range(0f, 1f)] public float minVolume = 0.2f;
        [Range(0f, 1f)] public float maxVolume = 1f;
        [Tooltip("How many hits can ring at the same time.")]
        [Min(1)] public int voices = 4;
        [Min(1f)] public float maxHearingDistance = 12f;

        [Header("Filtering")]
        [Tooltip("A hand must have been off the surfaces this long (s) for a new contact to count. Stops sliding over rough geometry from re-triggering.")]
        [Min(0f)] public float minAirTime = 0.05f;

        [Tooltip("Shortest gap (s) between two feedbacks on the same hand.")]
        [Min(0f)] public float cooldown = 0.08f;

        [Tooltip("Hand speeds above this (m/s) are a teleport or tracking glitch, not a hit.")]
        [Min(1f)] public float maxPlausibleSpeed = 15f;

        [Tooltip("Contacts in this many seconds after startup are ignored (the rig is still being placed).")]
        [Min(0f)] public float ignoreSecondsAfterStart = 0.5f;

        Player player;
        AudioSource[] sources;
        int nextSource;
        float lastPitch = 1f;
        float enabledAt;
        readonly float[] nextAllowed = new float[2];
        InputDevice leftDevice;
        InputDevice rightDevice;

        /// <summary>How many contacts have produced feedback so far, and how strong the last one was (0..1). For debugging and tests.</summary>
        public int FeedbackCount { get; private set; }
        public float LastStrength { get; private set; }

        /// <summary>0..1 strength for a hand speed into the surface; 0 below <see cref="minImpactSpeed"/>.</summary>
        public float Strength(float impactSpeed)
        {
            if (impactSpeed < minImpactSpeed)
                return 0f;
            float t = Mathf.InverseLerp(minImpactSpeed, Mathf.Max(maxImpactSpeed, minImpactSpeed + 0.01f), impactSpeed);
            return Mathf.Pow(t, responseExponent);
        }

        public float AmplitudeFor(float strength) => Mathf.Lerp(minAmplitude, maxAmplitude, strength);
        public float DurationFor(float strength) => Mathf.Lerp(minDuration, maxDuration, strength);
        public float VolumeFor(float strength) => Mathf.Lerp(minVolume, maxVolume, strength);

        void Awake()
        {
            if (clip == null)
                clip = Resources.Load<AudioClip>(DefaultClipPath);
        }

        void OnEnable()
        {
            player = GetComponent<Player>();
            if (player == null)
                player = GetComponentInParent<Player>();
            if (player == null)
                player = Player.Instance;
            if (player == null)
            {
                Debug.LogWarning("HandContactFeedback: no GorillaLocomotion.Player found.", this);
                return;
            }

            enabledAt = Time.unscaledTime;
            nextAllowed[0] = nextAllowed[1] = 0f;
            player.HandContact += OnHandContact;
        }

        void OnDisable()
        {
            if (player != null)
                player.HandContact -= OnHandContact;
            player = null;
        }

        void OnHandContact(HandContactInfo info)
        {
            float now = Time.unscaledTime;
            if (now - enabledAt < ignoreSecondsAfterStart)
                return;
            if (info.handSpeed > maxPlausibleSpeed || info.airTime < minAirTime)
                return;

            if (info.impactSpeed < minImpactSpeed)
                return;

            int hand = info.leftHand ? 0 : 1;
            if (now < nextAllowed[hand])
                return;
            nextAllowed[hand] = now + cooldown;

            float strength = Strength(info.impactSpeed);
            FeedbackCount++;
            LastStrength = strength;
            if (haptics)
                Vibrate(info.leftHand, AmplitudeFor(strength), DurationFor(strength));
            if (sound)
                PlayImpact(info.position, VolumeFor(strength));
        }

        void Vibrate(bool left, float amplitude, float duration)
        {
            if (left)
                Send(ref leftDevice, XRNode.LeftHand, amplitude, duration);
            else
                Send(ref rightDevice, XRNode.RightHand, amplitude, duration);
        }

        static void Send(ref InputDevice device, XRNode node, float amplitude, float duration)
        {
            if (!device.isValid)
                device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse)
                device.SendHapticImpulse(0, amplitude, duration);
        }

        void PlayImpact(Vector3 position, float volume)
        {
            if (clip == null)
                return;
            if (sources == null)
                BuildSources();

            // Next source that has finished; if all are still ringing, cut the oldest.
            AudioSource source = sources[nextSource];
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource candidate = sources[(nextSource + i) % sources.Length];
                if (!candidate.isPlaying)
                {
                    source = candidate;
                    break;
                }
            }
            nextSource = (nextSource + 1) % sources.Length;

            source.transform.position = position;
            source.pitch = NextPitch();
            source.volume = volume;
            source.clip = clip;
            source.Play();
        }

        // Random, but never nearly the same pitch twice in a row, which is what makes repeats sound mechanical.
        float NextPitch()
        {
            float pitch = 1f;
            for (int i = 0; i < 4; i++)
            {
                pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
                if (Mathf.Abs(pitch - lastPitch) >= pitchVariation * 0.3f)
                    break;
            }
            lastPitch = pitch;
            return pitch;
        }

        void BuildSources()
        {
            sources = new AudioSource[Mathf.Max(1, voices)];
            var root = new GameObject("HandContactAudio").transform;
            root.SetParent(transform, false);
            for (int i = 0; i < sources.Length; i++)
            {
                var go = new GameObject("Voice " + i);
                go.transform.SetParent(root, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = 1f;
                source.maxDistance = maxHearingDistance;
                sources[i] = source;
            }
        }
    }
}
