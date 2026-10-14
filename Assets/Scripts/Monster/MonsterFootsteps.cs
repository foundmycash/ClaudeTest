using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// The monster's footsteps, and the only sound it makes: no growl, breathing, alert or chase music. It does not
    /// listen to the microphone either; that is only measured by the detector and never played back.
    ///
    /// Timing follows how the monster really moves:
    /// - If <see cref="MonsterLegsLink"/> reports planted feet each sound is played when a foot lands, at the foot's
    ///   position. It does: every hand the crawl gait plants (its default), or the Legs Animator's own steps when it is
    ///   driven by a walk animation.
    /// - Otherwise (crawl gait off, no walk animation) a step is played every Stride Length metres walked, so faster
    ///   walking means quicker steps and standing still means none. Tune Stride Length until the sound lands with the
    ///   visible foot. This also covers the first fraction of a second of a chase, before the first hand has landed.
    /// - Nothing plays unless the monster is really moving (measured speed, not the speed it was told to have), and
    ///   when it stops the sounds still ringing are faded out in a split second. Nothing loops.
    ///
    /// Clips: drag your footstep sounds into <see cref="footstepClips"/> (a few variations; one is picked at random,
    /// never the same twice in a row). The project had no monster footstep sound, so while that list is empty a heavy
    /// stomp is generated at startup as a placeholder; assign real clips and it is not used.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-30)]
    [AddComponentMenu("Monster/Monster Footsteps")]
    public class MonsterFootsteps : MonoBehaviour
    {
        public enum Timing
        {
            /// <summary>Legs Animator steps when they arrive, distance-based steps if they do not.</summary>
            Auto,
            /// <summary>Only when the Legs Animator plants a foot.</summary>
            LegsAnimator,
            /// <summary>Only by distance walked.</summary>
            Distance,
        }

        [Header("Sounds (assign your own footsteps here)")]
        [Tooltip("The footstep clips. Several variations sound better; one is picked at random each step. EMPTY = a generated placeholder stomp is used so the monster is not silent while you test.")]
        public AudioClip[] footstepClips;

        [Tooltip("Use the generated placeholder stomp while Footstep Clips is empty. Turn off to be silent until you assign clips.")]
        public bool usePlaceholderWhenEmpty = true;

        [Header("Timing")]
        public Timing timing = Timing.Auto;

        [Tooltip("Distance timing: metres walked between two steps. A tall crawler takes long strides. At 3.5 m/s, 2.2 m is a step every 0.63 s; at 8 m/s every 0.28 s.")]
        [Min(0.3f)] public float strideLength = 2.2f;

        [Tooltip("Shortest time in seconds between two steps, so a burst of events can never become a rattle.")]
        [Min(0.02f)] public float minStepInterval = 0.12f;

        [Tooltip("Below this speed (m/s) the monster counts as standing and makes no steps.")]
        [Min(0f)] public float minSpeed = 0.35f;

        [Tooltip("Auto timing: the Legs Animator steps are used while one has arrived within this many seconds; otherwise the distance timing plays the steps (with the Monster Legs Link's Crawl Gait off, a monster without a walk animation never gets a step event from the Legs Animator).")]
        [Min(0.3f)] public float legsGraceSeconds = 1.5f;

        [Header("Loudness and pitch")]
        [Range(0f, 1f)] public float volume = 1f;

        [Tooltip("How much quieter a slow step is than a full-speed one (1 = the same).")]
        [Range(0.2f, 1f)] public float slowStepLoudness = 0.6f;

        [Tooltip("Each step's pitch is random within 1 +/- this.")]
        [Range(0f, 0.3f)] public float pitchVariation = 0.07f;

        [Tooltip("How much higher the pitch gets at full speed (a fraction, 0.1 = 10 %). Quick steps sound lighter.")]
        [Range(0f, 0.4f)] public float speedPitchRise = 0.1f;

        [Tooltip("Speed (m/s) counted as 'full speed' for the loudness and pitch. Empty = the monster's Max Chase Speed.")]
        [Min(0f)] public float fullSpeed = 0f;

        [Header("3D sound")]
        [Tooltip("Up to this many metres the steps are at full volume.")]
        [Min(0.1f)] public float minDistance = 5f;

        // How far the steps carry is not set here: it is the monster's hearing range (MonsterSenses), the same radius as its screech.

        [Tooltip("Use the project's audio spatializer (Meta XR Audio here) for proper 3D direction on headphones, if one is set up.")]
        public bool useSpatializer = true;

        [Tooltip("How many steps can ring at the same time.")]
        [Range(1, 8)] public int voices = 3;

        [Tooltip("Seconds the ringing steps take to fade out when the monster stops.")]
        [Range(0.02f, 0.5f)] public float stopFadeSeconds = 0.12f;

        /// <summary>How many steps have been played so far (debugging and tests).</summary>
        public int StepsPlayed { get; private set; }
        public float LastStepTime { get; private set; } = -999f;
        public float LastStepVolume { get; private set; }
        /// <summary>True if any step sound is still sounding.</summary>
        public bool IsAnythingPlaying
        {
            get
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] != null && sources[i].isPlaying)
                        return true;
                }
                return false;
            }
        }
        /// <summary>True while the timing comes from the Legs Animator, false while it comes from the distance walked.</summary>
        public bool UsingLegSteps => !UseDistanceTiming;

        MonsterHunter hunter;
        MonsterLegsLink legs;
        AudioSource[] sources;
        float[] startVolumes;
        int nextVoice;
        AudioClip[] placeholder;
        int lastClip = -1;
        bool wasMoving;
        float distanceCarry;
        bool fading;
        float fadeLeft;

        bool UseDistanceTiming
        {
            get
            {
                if (timing == Timing.Distance)
                    return true;
                if (timing == Timing.LegsAnimator)
                    return false;
                // Auto: distance timing, unless planted feet are being reported right now (the crawl gait does; so does a Legs
                // Animator driven by a walk animation). A monster whose legs report nothing keeps its sound going this way.
                return legs == null || !legs.HasLegs || Time.time - legs.LastStepTime > legsGraceSeconds;
            }
        }

        void Awake()
        {
            hunter = GetComponent<MonsterHunter>();
            legs = GetComponent<MonsterLegsLink>();
            BuildVoices();
        }

        void OnEnable()
        {
            if (legs != null)
                legs.Stepped += OnLegStep;
        }

        void OnDisable()
        {
            if (legs != null)
                legs.Stepped -= OnLegStep;
            HardStop();
        }

        void BuildVoices()
        {
            int count = Mathf.Max(1, voices);
            sources = new AudioSource[count];
            startVolumes = new float[count];
            bool spatialize = useSpatializer && !string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName());

            // The steps carry exactly as far as the monster can hear (the same radius as its screech).
            MonsterSenses senses = GetComponent<MonsterSenses>();
            float maxDistance = senses != null ? senses.HearingRange : MonsterSenses.DefaultHearingRange;
            float nearDistance = Mathf.Clamp(minDistance, 0.1f, maxDistance * 0.5f);
            AnimationCurve rolloff = BuildRolloff(nearDistance, maxDistance);

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Footstep Voice " + (i + 1));
                go.transform.SetParent(transform, false);
                AudioSource s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = false;
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Custom;
                s.minDistance = nearDistance;
                s.maxDistance = maxDistance;
                s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, rolloff);
                s.dopplerLevel = 0f;
                s.spatialize = spatialize;
                s.spatializePostEffects = spatialize;
                sources[i] = s;
            }
        }

        // Natural 1/distance falloff, eased down to nothing over the last fifth so the steps are silent at the hearing range
        // (a plain logarithmic rolloff never reaches zero). X is the distance as a share of the maximum.
        static AnimationCurve BuildRolloff(float near, float max)
        {
            const int Points = 16;
            var keys = new Keyframe[Points + 1];
            for (int i = 0; i <= Points; i++)
            {
                float x = i / (float)Points;
                float d = Mathf.Max(x * max, near);
                float v = Mathf.Min(1f, near / d);
                if (x > 0.8f)
                    v *= Mathf.SmoothStep(1f, 0f, (x - 0.8f) / 0.2f);
                keys[i] = new Keyframe(x, v);
            }
            var curve = new AnimationCurve(keys);
            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);
            return curve;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (hunter == null)
                return;

            float speed = hunter.ActualSpeed;
            bool moving = hunter.IsMoving && speed >= minSpeed;

            if (moving)
            {
                if (!wasMoving)
                {
                    distanceCarry = strideLength * 0.5f;      // the first step comes after half a stride, not instantly and not a whole stride late
                    fading = false;
                }

                if (UseDistanceTiming)
                {
                    distanceCarry += speed * dt;
                    if (distanceCarry >= strideLength)
                    {
                        distanceCarry -= strideLength;
                        if (distanceCarry > strideLength)
                            distanceCarry = 0f;
                        PlayStep(transform.position, 1f);
                    }
                }
            }
            else if (wasMoving)
            {
                BeginFadeOut();
            }
            wasMoving = moving;

            if (fading)
                UpdateFade(dt);
        }

        void OnLegStep(Vector3 position, float power, bool isRight)
        {
            if (timing == Timing.Distance || hunter == null)
                return;
            // A foot being re-planted while the monster stands is not a footstep.
            if (!wasMoving || hunter.ActualSpeed < minSpeed)
                return;
            PlayStep(position, power);
        }

        void PlayStep(Vector3 position, float power)
        {
            float now = Time.time;
            if (now - LastStepTime < minStepInterval)
                return;

            AudioClip clip = PickClip();
            if (clip == null)
                return;

            float full = fullSpeed > 0f ? fullSpeed : (hunter != null ? hunter.maxChaseSpeed : 8f);
            float speedT = Mathf.InverseLerp(minSpeed, Mathf.Max(full, minSpeed + 0.1f), hunter != null ? hunter.ActualSpeed : minSpeed);
            float vol = volume * Mathf.Lerp(slowStepLoudness, 1f, speedT) * Mathf.Clamp(power, 0.6f, 1.2f);
            vol = Mathf.Clamp01(vol);
            float pitch = 1f + Random.Range(-pitchVariation, pitchVariation) + speedPitchRise * speedT;

            int index = nextVoice;
            nextVoice = (nextVoice + 1) % sources.Length;
            AudioSource s = sources[index];
            s.Stop();
            s.transform.position = position;
            s.clip = clip;
            s.volume = vol;
            s.pitch = pitch;
            s.Play();
            startVolumes[index] = vol;

            StepsPlayed++;
            LastStepTime = now;
            LastStepVolume = vol;
            fading = false;
        }

        AudioClip PickClip()
        {
            AudioClip[] pool = ValidClips();
            if (pool == null || pool.Length == 0)
                return null;
            if (pool.Length == 1)
                return pool[0];

            int pick = Random.Range(0, pool.Length);
            if (pick == lastClip)
                pick = (pick + 1) % pool.Length;      // never the same clip twice in a row
            lastClip = pick;
            return pool[pick];
        }

        AudioClip[] ValidClips()
        {
            if (footstepClips != null)
            {
                for (int i = 0; i < footstepClips.Length; i++)
                {
                    if (footstepClips[i] != null)
                        return footstepClips;
                }
            }

            if (!usePlaceholderWhenEmpty)
                return null;
            if (placeholder == null)
                placeholder = new[] { MakePlaceholder(1), MakePlaceholder(2), MakePlaceholder(3) };
            return placeholder;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Stopping

        void BeginFadeOut()
        {
            fading = true;
            fadeLeft = stopFadeSeconds;
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null && sources[i].isPlaying)
                    startVolumes[i] = sources[i].volume;
            }
        }

        void UpdateFade(float dt)
        {
            fadeLeft -= dt;
            float k = Mathf.Clamp01(fadeLeft / stopFadeSeconds);
            bool any = false;
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource s = sources[i];
                if (s == null || !s.isPlaying)
                    continue;
                if (fadeLeft <= 0f)
                {
                    s.Stop();
                }
                else
                {
                    s.volume = startVolumes[i] * k;
                    any = true;
                }
            }
            if (!any)
                fading = false;
        }

        /// <summary>Silences everything at once.</summary>
        public void HardStop()
        {
            fading = false;
            if (sources == null)
                return;
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].Stop();
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Placeholder stomp

        /// <summary>
        /// A heavy footfall: a low thump that drops in pitch, a short dull crunch of ground and leaves, and a tiny
        /// click at the start. Different seed = a slightly different step. Only a stand-in for real recordings.
        /// </summary>
        static AudioClip MakePlaceholder(int seed)
        {
            const int Rate = 22050;
            int length = (int)(0.6f * Rate);
            float[] data = new float[length];
            var rng = new System.Random(seed * 7919);

            float baseFreq = 36f * (1f + 0.08f * (seed - 2));
            float phase = 0f;
            float lowpass = 0f;
            float crunchCutoff = 0.12f + 0.03f * seed;
            float peak = 0.0001f;

            for (int i = 0; i < length; i++)
            {
                float t = i / (float)Rate;

                float freq = baseFreq + 50f * Mathf.Exp(-t / 0.05f);
                phase += 2f * Mathf.PI * freq / Rate;
                float thump = Mathf.Sin(phase) * Mathf.Exp(-t / 0.13f);

                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lowpass += crunchCutoff * (noise - lowpass);
                float crunch = lowpass * Mathf.Exp(-t / 0.07f) * (rng.NextDouble() > 0.55 ? 1f : 0.35f);

                float click = t < 0.003f ? (float)(rng.NextDouble() * 2.0 - 1.0) * (1f - t / 0.003f) * 0.5f : 0f;

                float v = thump * 0.95f + crunch * 1.4f + click;
                v = (float)System.Math.Tanh(v * 1.4f);
                data[i] = v;
                peak = Mathf.Max(peak, Mathf.Abs(v));
            }

            float scale = 0.9f / peak;
            int fade = (int)(0.03f * Rate);
            for (int i = 0; i < length; i++)
            {
                float f = i > length - fade ? (length - i) / (float)fade : 1f;
                data[i] *= scale * f;
            }

            AudioClip clip = AudioClip.Create("Monster Footstep (placeholder " + seed + ")", length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
