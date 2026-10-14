using System;

namespace MonsterAI
{
    /// <summary>
    /// Decides from a stream of microphone samples whether the player just made a loud noise. It measures loudness
    /// only (the level in dB below full scale); it does not recognise words, and it keeps no audio: each chunk is
    /// reduced to one number and thrown away. No Unity types, so it can be tested with made-up signals.
    ///
    /// What keeps it from firing on things that are not the player being loud:
    /// - A high-pass filter first, so rumble, wind and a bumped headset (all low frequency) count for little.
    /// - The level is smoothed (fast to rise, slow to fall) and has to stay above the threshold for a minimum time,
    ///   so a click, a clap or a controller knock does not count, but a shout does.
    /// - It must also be well above the room's noise floor, which it tracks by itself (it follows the quietest recent
    ///   level and creeps up slowly), so a noisy room is not a constant alarm.
    /// - After it fires it stays quiet for a cooldown.
    /// </summary>
    public sealed class NoiseAnalyzer
    {
        /// <summary>Loudness (dB, 0 = full scale) the smoothed level must reach. Quiet room about -60, normal speech about -35 to -25, shouting above -20.</summary>
        public float thresholdDb = -28f;

        /// <summary>The level must also be this many dB above the room's noise floor.</summary>
        public float minAboveFloorDb = 12f;

        /// <summary>The level must stay over the thresholds this long (seconds) to count.</summary>
        public float minLoudSeconds = 0.15f;

        /// <summary>Seconds after a detection in which no new one is reported.</summary>
        public float cooldownSeconds = 1.5f;

        /// <summary>Frequencies below this (Hz) are filtered out. 0 = no filter.</summary>
        public float highPassHz = 120f;

        /// <summary>How fast the smoothed level rises towards a louder sound, in seconds (smaller = quicker).</summary>
        public float attackSeconds = 0.03f;

        /// <summary>How fast it falls after a loud sound, in seconds.</summary>
        public float releaseSeconds = 0.25f;

        /// <summary>How fast the noise floor may climb, in dB per second (it falls at once).</summary>
        public float floorRiseDbPerSecond = 2f;

        public const float SilenceDb = -100f;
        const float StartFloorDb = -60f;

        /// <summary>The smoothed level of the last chunk, in dB.</summary>
        public float LevelDb { get; private set; } = SilenceDb;

        /// <summary>The tracked noise floor in dB.</summary>
        public float FloorDb { get; private set; } = StartFloorDb;

        /// <summary>True while the level is over the thresholds (a detection may follow once it has lasted long enough).</summary>
        public bool IsLoud { get; private set; }

        /// <summary>How many detections have fired so far.</summary>
        public int Detections { get; private set; }

        float clock;
        float loudTimer;
        float nextAllowed;
        bool first = true;
        float hpX, hpX2;
        float hpY, hpY2;

        public void Reset()
        {
            LevelDb = SilenceDb;
            FloorDb = StartFloorDb;
            IsLoud = false;
            clock = 0f;
            loudTimer = 0f;
            nextAllowed = 0f;
            first = true;
            hpX = 0f; hpX2 = 0f;
            hpY = 0f; hpY2 = 0f;
        }

        /// <summary>Forgets the loudness timers but keeps the learned noise floor (after a gap in the audio).</summary>
        public void ResumeAfterGap()
        {
            loudTimer = 0f;
            IsLoud = false;
            hpX = 0f; hpX2 = 0f;
            hpY = 0f; hpY2 = 0f;
        }

        /// <summary>Feeds one chunk of mono samples (-1..1). Returns true if this chunk completed a loud-noise detection.</summary>
        public bool ProcessChunk(float[] samples, int count, int sampleRate)
        {
            return ProcessChunk(samples, 0, count, sampleRate);
        }

        /// <summary>Feeds <paramref name="count"/> samples starting at <paramref name="offset"/>. Chunks of about 20 ms are ideal; the loudness of a chunk is one measurement.</summary>
        public bool ProcessChunk(float[] samples, int offset, int count, int sampleRate)
        {
            if (count <= 0 || sampleRate <= 0)
                return false;

            float dt = count / (float)sampleRate;
            double sum = 0.0;
            if (highPassHz > 1f)
            {
                // Two one-pole high-pass stages in a row (12 dB per octave): y[n] = a * (y[n-1] + x[n] - x[n-1]).
                // One stage is not enough to hold back a loud 40 Hz rumble.
                float rc = 1f / (2f * (float)Math.PI * highPassHz);
                float a = rc / (rc + 1f / sampleRate);
                float x1 = hpX, y1 = hpY, x2 = hpX2, y2 = hpY2;
                for (int i = 0; i < count; i++)
                {
                    float x = samples[offset + i];
                    float s1 = a * (y1 + x - x1);
                    x1 = x;
                    y1 = s1;
                    float s2 = a * (y2 + s1 - x2);
                    x2 = s1;
                    y2 = s2;
                    sum += s2 * s2;
                }
                hpX = x1; hpY = y1; hpX2 = x2; hpY2 = y2;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    sum += samples[offset + i] * samples[offset + i];
            }

            float rms = (float)Math.Sqrt(sum / count);
            return Step(DbFromAmplitude(rms), dt);
        }

        /// <summary>Feeds one loudness measurement (dB) that covers <paramref name="dt"/> seconds. Returns true on a detection.</summary>
        public bool Step(float db, float dt)
        {
            if (dt <= 0f)
                return false;

            clock += dt;

            if (first)
            {
                LevelDb = db;
                FloorDb = Math.Min(StartFloorDb, db);
                first = false;
            }

            // smoothed level: quick up, slow down
            float tau = db > LevelDb ? attackSeconds : releaseSeconds;
            float k = tau <= 1e-4f ? 1f : 1f - (float)Math.Exp(-dt / tau);
            LevelDb += (db - LevelDb) * k;

            // noise floor: follows the level down at once, creeps up slowly
            if (LevelDb < FloorDb)
                FloorDb = LevelDb;
            else
                FloorDb = Math.Min(LevelDb, FloorDb + floorRiseDbPerSecond * dt);

            bool over = LevelDb >= thresholdDb && LevelDb >= FloorDb + minAboveFloorDb;
            IsLoud = over;
            if (over)
                loudTimer += dt;
            else
                loudTimer = Math.Max(0f, loudTimer - 2f * dt);

            if (loudTimer >= minLoudSeconds && clock >= nextAllowed)
            {
                nextAllowed = clock + cooldownSeconds;
                loudTimer = 0f;
                Detections++;
                return true;
            }
            return false;
        }

        public static float DbFromAmplitude(float amplitude)
        {
            return amplitude < 1e-5f ? SilenceDb : 20f * (float)Math.Log10(amplitude);
        }
    }
}
