using System.Collections;
using UnityEngine;

namespace LoadingScreen
{
    /// <summary>
    /// The dynamite bundle of the loading cinematic. <see cref="Throw"/> lobs it from far behind the player onto the
    /// wall: a ballistic arc with a sidearm spin that lands flush against the wall, a thud, a puff of dust and a short
    /// wobble. <see cref="StartCountdown"/> then beeps faster and higher until a final long tone, and the moment that
    /// tone ends is the detonation.
    ///
    /// Sound and picture share one clock: every beep is scheduled on the audio clock (<see cref="AudioSettings.dspTime"/>)
    /// so the rhythm is sample-accurate, and the LED, its light and the controller taps follow that same clock.
    ///
    /// Model space: the sticks lie along local X and local +Z is the side with the timer, which faces away from the wall.
    /// </summary>
    [DisallowMultipleComponent]
    public class Dynamite : MonoBehaviour
    {
        const float ScheduleAhead = 0.12f;   // seconds of audio-clock lead for PlayScheduled
        const float MaxAnimationStep = 1f / 30f;

        [Header("Parts")]
        public Renderer led;
        public Light ledLight;
        public ParticleSystem impactDust;
        [Tooltip("3D source for the whoosh and the impact.")]
        public AudioSource flightSource;
        [Tooltip("3D sources the beeps take turns on, so a pitched beep is never cut by the next one.")]
        public AudioSource[] beepSources;
        [Tooltip("3D source for the final long tone.")]
        public AudioSource toneSource;

        [Header("Sounds")]
        public AudioClip whooshClip;
        public AudioClip impactClip;
        public AudioClip beepClip;
        public AudioClip finalToneClip;

        [Header("Throw")]
        [Min(0.3f)] public float flightTime = 1.2f;

        [Tooltip("Where the throw starts, relative to the player's head: x = right, y = up, z = forward (negative is behind).")]
        public Vector3 throwOrigin = new Vector3(0.8f, 1.0f, -6.5f);

        [Tooltip("Turns the bundle spins on the way.")]
        [Min(0f)] public float spinTurns = 1.75f;

        [Tooltip("Side-to-side wobble while it flies (degrees), gone by the time it lands.")]
        [Min(0f)] public float wobble = 14f;

        [Tooltip("Tilt of the bundle once it is stuck to the wall (degrees).")]
        public float restingTilt = -8f;

        [Tooltip("Distance from the wall surface to the bundle's centre (metres).")]
        [Min(0f)] public float surfaceOffset = 0.03f;

        [Tooltip("Time the bundle takes to settle after hitting the wall.")]
        [Min(0f)] public float settleTime = 0.4f;

        [Header("Countdown")]
        [Tooltip("Pause between landing and the first beep.")]
        [Min(0f)] public float firstBeepDelay = 0.35f;

        [Tooltip("Gap after each beep. They shrink so the beeping speeds up; the final tone follows the last gap.")]
        public float[] beepIntervals = { 0.55f, 0.45f, 0.36f, 0.28f, 0.21f, 0.16f, 0.12f, 0.09f };

        [Tooltip("Length of the final tone when there is no tone clip. With a clip, the dynamite goes off when the clip ends.")]
        [Min(0.05f)] public float finalToneLength = 0.42f;

        [Tooltip("Pitch of the first and the last beep.")]
        public Vector2 beepPitch = new Vector2(1f, 1.32f);

        [Tooltip("Volume of the first and the last beep.")]
        public Vector2 beepVolume = new Vector2(0.55f, 1f);

        [ColorUsage(false, true)] public Color ledOnColor = new Color(4f, 0.22f, 0.12f);
        public Color ledOffColor = new Color(0.1f, 0.012f, 0.01f);
        [Min(0f)] public float ledLightIntensity = 1.5f;

        [Tooltip("Rumble the controllers on the impact and with every beep.")]
        public bool haptics = true;

        /// <summary>Audio-clock time of the detonation, valid after <see cref="StartCountdown"/>.</summary>
        public double DetonationDspTime { get; private set; }

        static readonly int ColorId = Shader.PropertyToID("_Color");

        MaterialPropertyBlock ledBlock;
        double[] beepTimes;
        double toneTime;
        float toneSeconds;
        int nextToSchedule;
        int lastBeepShown;
        bool toneScheduled;
        bool counting;
        float countdownStartRealtime;
        float countdownExpectedLength;

        double clockDsp;
        double clockRealtime;

        // ------------------------------------------------------------------------------------------------------
        // Throw
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Lights and particle buffers ready before the throw: shown for two frames out of sight.</summary>
        public IEnumerator Prewarm(Vector3 hiddenPosition)
        {
            gameObject.SetActive(true);
            transform.position = hiddenPosition;
            if (ledLight != null) ledLight.enabled = false;
            SetLed(1f);
            if (impactDust != null) impactDust.Play(true);

            yield return null;
            yield return null;

            if (impactDust != null) impactDust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Throws the bundle from behind <paramref name="head"/> onto <paramref name="wallPoint"/> and lets it settle.
        /// <paramref name="wallNormal"/> points out of the wall, toward the player.
        /// </summary>
        public IEnumerator Throw(Transform head, Vector3 wallPoint, Vector3 wallNormal)
        {
            Vector3 forward = Vector3.ProjectOnPlane(-wallNormal, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            Vector3 start = head.position + right * throwOrigin.x + Vector3.up * throwOrigin.y + forward * throwOrigin.z;
            Vector3 end = wallPoint + wallNormal * surfaceOffset;
            Quaternion stuck = Quaternion.LookRotation(wallNormal, Vector3.up) * Quaternion.Euler(0f, 0f, restingTilt);

            // Ballistic arc that arrives exactly at the wall after flightTime.
            Vector3 gravity = Physics.gravity;
            float T = flightTime;
            Vector3 v0 = (end - start - 0.5f * T * T * gravity) / T;

            gameObject.SetActive(true);
            SetLed(0f);
            if (ledLight != null) ledLight.enabled = false;
            PlayFlight(whooshClip);

            // Spin about the bundle's own up axis (the stick sweeps through the direction of travel) and unwind exactly
            // into the resting pose at the moment of impact.
            for (float t = 0f; t < T; )
            {
                t = Mathf.Min(t + Mathf.Min(Time.deltaTime, MaxAnimationStep), T);
                float u = t / T;
                transform.position = start + v0 * t + 0.5f * t * t * gravity;
                float spin = spinTurns * 360f * (1f - u);
                float sway = wobble * Mathf.Sin(u * 9f) * (1f - u);
                transform.rotation = stuck * Quaternion.AngleAxis(-spin, Vector3.up) * Quaternion.AngleAxis(sway, Vector3.right);
                yield return null;
            }

            // Impact.
            transform.SetPositionAndRotation(end, stuck);
            if (flightSource != null)
            {
                flightSource.Stop();
                if (impactClip != null)
                    flightSource.PlayOneShot(impactClip);
            }
            if (impactDust != null)
            {
                impactDust.transform.SetPositionAndRotation(wallPoint + wallNormal * 0.01f, Quaternion.LookRotation(wallNormal));
                impactDust.Play(true);
            }
            if (haptics)
                LoadingHaptics.Pulse(0.3f, 0.06f);

            // Settle: a small bounce off the wall and a damped rock about the wall normal.
            for (float s = 0f; s < settleTime; )
            {
                s += Mathf.Min(Time.deltaTime, MaxAnimationStep);
                float damping = Mathf.Exp(-s * 9f);
                float bounce = Mathf.Sin(s * 38f) * damping;
                transform.position = end + wallNormal * (0.018f * Mathf.Abs(bounce));
                transform.rotation = stuck
                    * Quaternion.AngleAxis(9f * bounce, Vector3.forward)
                    * Quaternion.AngleAxis(4f * Mathf.Sin(s * 29f) * damping, Vector3.up);
                yield return null;
            }
            transform.SetPositionAndRotation(end, stuck);
        }

        void PlayFlight(AudioClip clip)
        {
            if (flightSource == null || clip == null)
                return;
            flightSource.clip = clip;
            flightSource.Play();
        }

        // ------------------------------------------------------------------------------------------------------
        // Countdown
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Starts the beeping. Returns the audio-clock time of the detonation.</summary>
        public double StartCountdown()
        {
            double t = AudioSettings.dspTime + firstBeepDelay;
            beepTimes = new double[beepIntervals.Length];
            for (int i = 0; i < beepIntervals.Length; i++)
            {
                beepTimes[i] = t;
                t += Mathf.Max(0.04f, beepIntervals[i]);
            }
            toneTime = t;
            toneSeconds = finalToneClip != null ? finalToneClip.length / beepPitch.y : finalToneLength;
            DetonationDspTime = t + toneSeconds;

            nextToSchedule = 0;
            lastBeepShown = -1;
            toneScheduled = false;
            counting = true;
            countdownStartRealtime = Time.realtimeSinceStartup;
            countdownExpectedLength = (float)(DetonationDspTime - AudioSettings.dspTime);
            if (ledLight != null) ledLight.enabled = true;
            return DetonationDspTime;
        }

        /// <summary>True once the countdown has run out.</summary>
        public bool ReachedDetonation
        {
            get
            {
                if (!counting)
                    return false;
                // The audio clock decides; real time is only a fallback for when audio is unavailable.
                return SmoothDspTime() >= DetonationDspTime
                    || Time.realtimeSinceStartup - countdownStartRealtime > countdownExpectedLength + 0.3f;
            }
        }

        /// <summary>The bundle is blown apart: hide it and stop its sounds.</summary>
        public void Detonate()
        {
            counting = false;
            gameObject.SetActive(false);
        }

        void Update()
        {
            if (!counting)
                return;

            // Hand the next beeps to the audio system a little ahead of time so it can start them sample-accurately.
            double dsp = AudioSettings.dspTime;
            while (nextToSchedule < beepTimes.Length && beepTimes[nextToSchedule] - dsp < ScheduleAhead)
            {
                AudioSource source = beepSources[nextToSchedule % beepSources.Length];
                float k = beepTimes.Length > 1 ? nextToSchedule / (beepTimes.Length - 1f) : 1f;
                source.clip = beepClip;
                source.pitch = Mathf.Lerp(beepPitch.x, beepPitch.y, k);
                source.volume = Mathf.Lerp(beepVolume.x, beepVolume.y, k);
                source.PlayScheduled(beepTimes[nextToSchedule]);
                nextToSchedule++;
            }
            if (!toneScheduled && toneTime - dsp < ScheduleAhead && toneSource != null)
            {
                toneSource.clip = finalToneClip;
                toneSource.pitch = beepPitch.y;
                toneSource.PlayScheduled(toneTime);
                toneScheduled = true;
            }

            // The LED follows the same clock as the sound.
            double now = SmoothDspTime();
            float brightness;
            if (now >= toneTime)
            {
                brightness = 0.85f + 0.15f * Mathf.Sin((float)(now - toneTime) * 90f);
                if (lastBeepShown < beepTimes.Length)
                {
                    lastBeepShown = beepTimes.Length;
                    if (haptics) LoadingHaptics.Pulse(0.5f, toneSeconds);
                }
            }
            else
            {
                int current = -1;
                for (int i = beepTimes.Length - 1; i >= 0; i--)
                {
                    if (now >= beepTimes[i]) { current = i; break; }
                }

                brightness = 0f;
                if (current >= 0)
                {
                    float since = (float)(now - beepTimes[current]);
                    float pitch = Mathf.Lerp(beepPitch.x, beepPitch.y, current / Mathf.Max(1f, beepTimes.Length - 1f));
                    float on = beepClip != null ? beepClip.length / pitch : 0.07f;
                    brightness = since < on ? 1f : Mathf.Exp(-(since - on) * 16f);

                    if (current != lastBeepShown)
                    {
                        lastBeepShown = current;
                        if (haptics)
                            LoadingHaptics.Pulse(Mathf.Lerp(0.15f, 0.4f, current / Mathf.Max(1f, beepTimes.Length - 1f)), 0.035f);
                    }
                }
            }
            SetLed(brightness);
        }

        void SetLed(float brightness)
        {
            if (led != null)
            {
                ledBlock ??= new MaterialPropertyBlock();
                ledBlock.SetColor(ColorId, Color.Lerp(ledOffColor, ledOnColor, brightness));
                led.SetPropertyBlock(ledBlock);
            }
            if (ledLight != null)
                ledLight.intensity = ledLightIntensity * brightness;
        }

        // AudioSettings.dspTime only moves when the audio thread finishes a buffer (every 10-20 ms). Between those steps,
        // extrapolate with real time so visuals keyed to it move smoothly.
        double SmoothDspTime()
        {
            double dsp = AudioSettings.dspTime;
            double now = Time.realtimeSinceStartupAsDouble;
            if (dsp != clockDsp)
            {
                clockDsp = dsp;
                clockRealtime = now;
            }
            return clockDsp + System.Math.Min(now - clockRealtime, 0.05);
        }
    }
}
