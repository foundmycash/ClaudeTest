using System;

namespace Ambience
{
    /// <summary>
    /// Renders the five cave sounds as raw mono samples (-1..1). It does not touch UnityEngine, so it can run on a
    /// worker thread and be tested outside the editor. Everything is built from noise, sine waves and a few filters,
    /// then pushed through the same dark, long-tailed "cave" space (an echo plus a damped reverb), which is what ties
    /// five very different sounds together.
    ///
    ///   0  Hollow Drone     a deep, slowly beating tone that swells and sags, with rock-rumble under it
    ///   1  Metal Groan      stick-slip creak of a huge metal beam: a slowing pulse train ringing inharmonic modes
    ///   2  Fissure Whistle  wind through a crack: narrow noise resonances wandering around a slightly wrong interval
    ///   3  Far Thud         a muffled knock far away, answered by echoes and a second, ambiguous knock
    ///   4  Wrong Breath     sub-vocal pulses through vowel formants plus breath noise, ring-modulated and bit-crushed
    ///
    /// The same (sampleRate, seed) always gives the same samples.
    /// </summary>
    public static class CaveSoundSynth
    {
        public const int SoundCount = 5;

        public static readonly string[] Names =
        {
            "Hollow Drone", "Metal Groan", "Fissure Whistle", "Far Thud", "Wrong Breath",
        };

        // Peak level (0..1) each finished clip is scaled to. They differ on purpose: the deep drone and the thud keep
        // most of their energy below 300 Hz, where the ear is far less sensitive, so these are balanced by A-weighted loudness (about -33 dB), not by peak.
        static readonly float[] PeakLevel = { 0.80f, 0.20f, 0.17f, 0.95f, 0.37f };

        const float TwoPi = 6.2831853f;

        /// <summary>Renders all five sounds. Takes a few tens of milliseconds on a PC; run it off the main thread on a headset.</summary>
        public static float[][] RenderAll(int sampleRate, int seed)
        {
            var all = new float[SoundCount][];
            for (int i = 0; i < SoundCount; i++)
                all[i] = Render(i, sampleRate, seed);
            return all;
        }

        public static float[] Render(int index, int sampleRate, int seed)
        {
            if (index < 0 || index >= SoundCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (sampleRate < 8000)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Use at least 8000 Hz.");

            var rng = new Rng(seed * 31 + index * 7919 + 17);
            float[] x;
            switch (index)
            {
                case 0: x = HollowDrone(sampleRate, rng); break;
                case 1: x = MetalGroan(sampleRate, rng); break;
                case 2: x = FissureWhistle(sampleRate, rng); break;
                case 3: x = FarThud(sampleRate, rng); break;
                default: x = WrongBreath(sampleRate, rng); break;
            }

            Finish(x, sampleRate, PeakLevel[index]);
            return x;
        }

        /// <summary>16-bit mono PCM WAV file bytes, for saving the sounds to disk.</summary>
        public static byte[] ToWav(float[] samples, int sampleRate)
        {
            int dataBytes = samples.Length * 2;
            var bytes = new byte[44 + dataBytes];
            void Put32(int at, int v) { bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8); bytes[at + 2] = (byte)(v >> 16); bytes[at + 3] = (byte)(v >> 24); }
            void Put16(int at, int v) { bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8); }
            void Tag(int at, string s) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)s[i]; }

            Tag(0, "RIFF"); Put32(4, 36 + dataBytes); Tag(8, "WAVE");
            Tag(12, "fmt "); Put32(16, 16); Put16(20, 1); Put16(22, 1);
            Put32(24, sampleRate); Put32(28, sampleRate * 2); Put16(32, 2); Put16(34, 16);
            Tag(36, "data"); Put32(40, dataBytes);
            for (int i = 0; i < samples.Length; i++)
            {
                float s = samples[i] < -1f ? -1f : samples[i] > 1f ? 1f : samples[i];
                Put16(44 + i * 2, (short)Math.Round(s * 32767f));
            }
            return bytes;
        }

        // ------------------------------------------------------------------------------------------------ the five sounds

        static float[] HollowDrone(int fs, Rng rng)
        {
            const float dry = 3.5f, total = 4.8f;
            int nd = (int)(dry * fs), n = (int)(total * fs);
            var tone = new float[n];
            var rumble = new float[n];
            var pipe = new float[n];

            // Two fundamentals a hair apart (they beat slowly), plus partials that are not quite whole multiples.
            var breathe = new SlowNoise(rng, 0.5f, fs);
            double p0 = 0, p1 = 0, p2 = 0, p3 = 0, p4 = 0;
            for (int i = 0; i < nd; i++)
            {
                float u = (float)i / nd;
                float w = TwoPi * (68f * (1f - 0.07f * u * u)) / fs; // sags about a semitone over the sound
                p0 += w; p1 += w * 1.0115f; p2 += w * 2f; p3 += w * 3.02f; p4 += w * 4.18f;
                float b = 0.5f + 0.5f * Clamp(breathe.Next() * 0.5f, -1f, 1f);
                tone[i] = Sin(p0) + 0.85f * Sin(p1) + 0.8f * Sin(p2) + 0.6f * Sin(p3) + 0.35f * b * Sin(p4);
            }

            // Rock rumble: noise squeezed below ~100 Hz and shaken slowly.
            var lpA = Biquad.LowPass(80f, 0.9f, fs);
            var lpB = Biquad.LowPass(110f, 0.7f, fs);
            var shake = new SlowNoise(rng, 1.3f, fs);
            for (int i = 0; i < nd; i++)
                rumble[i] = lpB.Process(lpA.Process(rng.Signed())) * (0.65f + 0.35f * Clamp(shake.Next(), -1f, 1f));

            // A faint pipe-like resonance: noise through one slow, tuned ring.
            var ring = new Resonator();
            for (int i = 0; i < nd; i++)
            {
                if ((i & 15) == 0)
                    ring.Set(158f * (1f - 0.07f * ((float)i / nd) * ((float)i / nd)), 0.5f, fs);
                pipe[i] = ring.Process(rng.Signed());
            }

            NormRms(tone, nd); NormRms(rumble, nd); NormRms(pipe, nd);

            var x = new float[n];
            for (int i = 0; i < nd; i++)
                x[i] = Swell((float)i / fs, dry, 1.4f) * (tone[i] + 0.45f * rumble[i] + 0.35f * pipe[i]);

            Space(x, fs, echoDelay: 0f, echoFeedback: 0f, echoMix: 0f, echoDamp: 0f, rt60: 4.2f, reverbMix: 0.8f, reverbDamp: 0.4f, preDelay: 0.03f);
            return x;
        }

        static float[] MetalGroan(int fs, Rng rng)
        {
            const float dry = 2.9f, total = 4.2f;
            int nd = (int)(dry * fs), n = (int)(total * fs);
            var modal = new float[n];
            var body = new float[n];
            var scrape = new float[n];
            var gate = new float[n];

            // Free-bar style modes: ratios that are not whole numbers are what reads as "metal".
            float[] ratios = { 1f, 2.76f, 5.40f, 8.93f, 13.34f };
            float[] amps = { 1f, 0.9f, 0.8f, 0.6f, 0.35f };
            float[] t60s = { 0.6f, 0.42f, 0.3f, 0.2f, 0.14f };
            var modes = new Resonator[5];
            for (int k = 0; k < modes.Length; k++) modes[k] = new Resonator();

            var detune = new SlowNoise(rng, 0.9f, fs);
            var slip = new SlowNoise(rng, 2.6f, fs);
            var bodyLp = Biquad.LowPass(230f, 1.2f, fs);
            var scrapeBp = Biquad.BandPass(1500f, 3f, fs);
            double nextPulse = 0.02 * fs;
            double bodyPhase = 0;
            float pulseEnv = 0f;
            float pulseDecay = (float)Math.Exp(-1.0 / (0.018 * fs));

            for (int i = 0; i < nd; i++)
            {
                float u = (float)i / nd;
                float imp = 0f;
                if (i >= nextPulse)
                {
                    // The pulse rate slows as the "beam" settles; the jitter keeps it from sounding like a motor.
                    float rate = Lerp(38f, 14f, u);
                    imp = rng.Range(0.45f, 1f);
                    pulseEnv = imp;
                    nextPulse += fs / rate * rng.Range(0.7f, 1.35f);
                }
                else
                {
                    pulseEnv *= pulseDecay;
                }

                float d = detune.Next();
                float sl = slip.Next();
                if ((i & 15) == 0)
                {
                    float baseHz = 128f * (1f - 0.05f * u) * (1f + 0.012f * d);
                    for (int k = 0; k < modes.Length; k++)
                        modes[k].Set(baseHz * ratios[k], t60s[k], fs);
                }

                float m = 0f;
                for (int k = 0; k < modes.Length; k++)
                    m += amps[k] * modes[k].Process(imp);
                modal[i] = m;

                bodyPhase += (50f - 7f * u) / fs;
                bodyPhase -= Math.Floor(bodyPhase);
                body[i] = bodyLp.Process((float)(2.0 * bodyPhase - 1.0)) * (0.45f + 0.55f * pulseEnv);
                scrape[i] = scrapeBp.Process(rng.Signed()) * pulseEnv;
                gate[i] = 0.25f + 0.75f * SmoothStep(-0.5f, 0.5f, sl); // it grips and lets go
            }

            NormRms(modal, nd); NormRms(body, nd); NormRms(scrape, nd);

            var x = new float[n];
            for (int i = 0; i < nd; i++)
                x[i] = (modal[i] + 0.55f * body[i] + 0.14f * scrape[i]) * Swell((float)i / fs, dry, 0.8f) * gate[i];

            // Overdriven, then dulled: a groan heard through a lot of rock.
            NormRms(x, nd, 0.35f);
            for (int i = 0; i < nd; i++)
                x[i] = (float)Math.Tanh(2.4f * x[i]);
            LowPassInPlace(x, 3000f, fs);

            Space(x, fs, echoDelay: 0.43f, echoFeedback: 0.42f, echoMix: 0.35f, echoDamp: 0.45f, rt60: 3.6f, reverbMix: 0.5f, reverbDamp: 0.4f, preDelay: 0.02f);
            return x;
        }

        static float[] FissureWhistle(int fs, Rng rng)
        {
            const float dry = 3.2f, total = 4.4f;
            int nd = (int)(dry * fs), n = (int)(total * fs);
            var whistle = new float[n];
            var air = new float[n];
            var gust = new float[n];

            var r1 = new Resonator(); var r2 = new Resonator(); var r3 = new Resonator();
            var drift = new SlowNoise(rng, 0.45f, fs);
            var gustNoise = new SlowNoise(rng, 1.0f, fs);
            float phi = rng.Range(0f, TwoPi);
            var hp = Biquad.HighPass(1100f, 0.7f, fs);
            var lp = Biquad.LowPass(3200f, 0.7f, fs);

            for (int i = 0; i < nd; i++)
            {
                float t = (float)i / fs;
                float d = drift.Next();
                // Pitch wanders a little, then bends down about three semitones near the end: it sounds like it is giving up.
                float semis = 1.1f * Sin(TwoPi * 0.21f * t + phi) + 0.8f * d - 3.4f * SmoothStep(1.9f, 3.2f, t);
                if ((i & 15) == 0)
                {
                    float f = 690f * (float)Math.Pow(2.0, semis / 12f);
                    r1.Set(f, 0.14f, fs);
                    r2.Set(f * 1.414f, 0.10f, fs); // a tritone above: close enough to harmony to notice it is off
                    r3.Set(f * 2.003f, 0.08f, fs);
                }

                whistle[i] = r1.Process(rng.Signed()) + 0.45f * r2.Process(rng.Signed()) + 0.18f * r3.Process(rng.Signed());
                air[i] = lp.Process(hp.Process(rng.Signed()));
                gust[i] = Clamp(0.7f + 0.3f * gustNoise.Next(), 0.25f, 1f);
            }

            NormRms(whistle, nd); NormRms(air, nd);

            var x = new float[n];
            for (int i = 0; i < nd; i++)
                x[i] = Swell((float)i / fs, dry, 1.1f) * gust[i] * (whistle[i] + 0.16f * air[i]);

            LowPassInPlace(x, 4500f, fs);
            Space(x, fs, echoDelay: 0.52f, echoFeedback: 0.30f, echoMix: 0.20f, echoDamp: 0.40f, rt60: 3.4f, reverbMix: 0.6f, reverbDamp: 0.35f, preDelay: 0.02f);
            return x;
        }

        static float[] FarThud(int fs, Rng rng)
        {
            const float total = 4.8f;
            int n = (int)(total * fs);
            var x = new float[n];

            // One knock, then a quieter, lower one that lands between the echoes of the first, so you cannot tell
            // whether you heard an echo or something answering.
            AddThud(x, fs, rng, 0.05f, 1.00f, 54f);
            AddThud(x, fs, rng, 2.35f, 0.38f, 46f);

            LowPassInPlace(x, 1500f, fs);
            Space(x, fs, echoDelay: 0.61f, echoFeedback: 0.42f, echoMix: 0.50f, echoDamp: 0.45f, rt60: 3.8f, reverbMix: 1.0f, reverbDamp: 0.4f, preDelay: 0.03f);
            return x;
        }

        static void AddThud(float[] x, int fs, Rng rng, float startSec, float gain, float f0)
        {
            int start = (int)(startSec * fs);
            int len = (int)(0.9f * fs);
            double phase = 0;
            var bodyLp = Biquad.LowPass(420f, 0.8f, fs);
            var knockBp = Biquad.BandPass(560f * rng.Range(0.9f, 1.1f), 2f, fs);
            var ring = new Resonator();
            ring.Set(f0 * 3.3f, 0.32f, fs); // the hollow ring of a big stone

            for (int k = 0; k < len && start + k < x.Length; k++)
            {
                float t = (float)k / fs;
                float f = f0 * (1f + 0.9f * (float)Math.Exp(-t / 0.05f)); // drops in pitch as it lands
                phase += TwoPi * f / fs;
                float attack = t < 0.008f ? 0.5f * (1f - Cos(Math.PI * t / 0.008f)) : 1f; // no hard click
                float sine = Sin(phase) * (float)Math.Exp(-t / 0.20f) * attack;
                float burst = rng.Signed() * (float)Math.Exp(-t / 0.035f) * attack;
                x[start + k] += gain * (sine + 2.2f * bodyLp.Process(burst) + 0.9f * ring.Process(burst * 0.5f) + 8f * knockBp.Process(burst));
            }
        }

        static float[] WrongBreath(int fs, Rng rng)
        {
            const float dry = 2.5f, total = 3.8f;
            int nd = (int)(dry * fs), n = (int)(total * fs);
            var voiced = new float[n];
            var breath = new float[n];

            // Vowel formants as ringing filters, fed by sub-audio pulses (voiced, about 44 Hz: you hear it as a flutter)
            // and by noise (breath). The vowel opens and closes once, like an "ooh-ahh-ooh" that never quite arrives.
            var vf = new Resonator[3];
            var bf = new Resonator[3];
            for (int k = 0; k < 3; k++) { vf[k] = new Resonator(); bf[k] = new Resonator(); }
            float[] t60v = { 0.03f, 0.02f, 0.012f };
            float[] t60b = { 0.014f, 0.010f, 0.006f };
            float[] weight = { 1f, 0.6f, 0.15f };
            var jitter = new SlowNoise(rng, 2.2f, fs);
            var flutter = new SlowNoise(rng, 1.7f, fs);
            var flutterLevel = new float[n];
            double nextPulse = 0.05 * fs;

            for (int i = 0; i < nd; i++)
            {
                float u = (float)i / nd;
                float open = Sin(Math.PI * u);
                float f0 = 44f * (1f - 0.12f * u) * (1f + 0.04f * jitter.Next());
                if ((i & 15) == 0)
                {
                    float[] hz = { 360f + 170f * open, 800f + 420f * open, 2350f };
                    for (int k = 0; k < 3; k++)
                    {
                        vf[k].Set(hz[k], t60v[k], fs);
                        bf[k].Set(hz[k], t60b[k], fs);
                    }
                }

                float imp = 0f;
                if (i >= nextPulse)
                {
                    imp = 1f;
                    nextPulse += fs / f0 * rng.Range(0.92f, 1.08f);
                }

                float v = 0f, b = 0f;
                for (int k = 0; k < 3; k++)
                {
                    v += weight[k] * vf[k].Process(imp);
                    b += weight[k] * bf[k].Process(rng.Signed());
                }
                voiced[i] = v;
                breath[i] = b;
                flutterLevel[i] = Clamp(0.75f + 0.25f * flutter.Next(), 0.3f, 1f);
            }

            NormRms(voiced, nd); NormRms(breath, nd);

            var x = new float[n];
            for (int i = 0; i < nd; i++)
                x[i] = (0.55f * voiced[i] + breath[i]) * Swell((float)i / fs, dry, 0.8f) * flutterLevel[i];

            // Make it ambiguous: ring-modulate with a low tone (rough, inhuman sidebands), then crush the resolution.
            NormRms(x, nd, 0.3f);
            float held = 0f;
            for (int i = 0; i < nd; i++)
            {
                float t = (float)i / fs;
                float v = x[i] * (0.55f + 0.45f * Sin(TwoPi * 29f * t));
                if (i % 3 == 0)
                    held = (float)Math.Round(v * 48f) / 48f;
                x[i] = held;
            }
            LowPassInPlace(x, 2300f, fs);

            Space(x, fs, echoDelay: 0.37f, echoFeedback: 0.30f, echoMix: 0.30f, echoDamp: 0.50f, rt60: 3.0f, reverbMix: 0.55f, reverbDamp: 0.4f, preDelay: 0.02f);
            return x;
        }

        // ------------------------------------------------------------------------------------------------ the shared space

        /// <summary>Adds the cave: a darkening feedback echo, and a long damped reverb that also reverberates the echoes. In place.</summary>
        static void Space(float[] x, int fs, float echoDelay, float echoFeedback, float echoMix, float echoDamp, float rt60, float reverbMix, float reverbDamp, float preDelay)
        {
            DampedEcho echo = echoMix > 0f ? new DampedEcho((int)(echoDelay * fs), echoFeedback, echoDamp) : null;
            var reverb = new CaveReverb(fs, rt60, reverbDamp, preDelay);
            for (int i = 0; i < x.Length; i++)
            {
                float dry = x[i];
                float e = echo != null ? echo.Process(dry) * echoMix : 0f;
                x[i] = dry + e + reverb.Process(dry + e) * reverbMix;
            }
        }

        /// <summary>Removes DC, fades the very start and the very end to exactly zero (no clicks), and scales to the peak level.</summary>
        static void Finish(float[] x, int fs, float peak)
        {
            float xm1 = 0f, ym1 = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                float y = x[i] - xm1 + 0.998f * ym1;
                xm1 = x[i];
                ym1 = y;
                x[i] = y;
            }

            int fadeIn = (int)(0.02f * fs), fadeOut = (int)(0.35f * fs);
            for (int i = 0; i < fadeIn && i < x.Length; i++)
                x[i] *= 0.5f * (1f - Cos(Math.PI * i / fadeIn));
            for (int i = 0; i < fadeOut && i < x.Length; i++)
                x[x.Length - 1 - i] *= 0.5f * (1f - Cos(Math.PI * i / fadeOut));

            float max = 0f;
            for (int i = 0; i < x.Length; i++)
                max = Math.Max(max, Math.Abs(x[i]));
            float scale = max > 1e-9f ? peak / max : 0f;
            for (int i = 0; i < x.Length; i++)
                x[i] *= scale;
        }

        // ------------------------------------------------------------------------------------------------ small helpers

        static float Sin(double x) => (float)Math.Sin(x);
        static float Cos(double x) => (float)Math.Cos(x);
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static float SmoothStep(float a, float b, float v)
        {
            float t = Clamp((v - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Rises with a raised cosine over <paramref name="attack"/> seconds, falls the same way until <paramref name="duration"/>.</summary>
        static float Swell(float t, float duration, float attack)
        {
            if (t <= 0f || t >= duration) return 0f;
            if (t < attack) return 0.5f * (1f - Cos(Math.PI * t / attack));
            return 0.5f * (1f + Cos(Math.PI * (t - attack) / (duration - attack)));
        }

        /// <summary>Scales the first <paramref name="length"/> samples so their RMS is <paramref name="target"/>.</summary>
        static void NormRms(float[] a, int length, float target = 1f)
        {
            double sum = 0;
            for (int i = 0; i < length; i++) sum += a[i] * a[i];
            double rms = Math.Sqrt(sum / Math.Max(1, length));
            if (rms < 1e-9) return;
            float g = (float)(target / rms);
            for (int i = 0; i < a.Length; i++) a[i] *= g;
        }

        static void LowPassInPlace(float[] a, float hz, int fs)
        {
            var lp = Biquad.LowPass(hz, 0.7f, fs);
            for (int i = 0; i < a.Length; i++) a[i] = lp.Process(a[i]);
        }

        // ------------------------------------------------------------------------------------------------ DSP building blocks

        /// <summary>xorshift32: small, fast, and identical on every platform (UnityEngine.Random is not usable off the main thread).</summary>
        sealed class Rng
        {
            uint s;

            public Rng(int seed)
            {
                s = (uint)seed * 2654435761u + 0x9E3779B9u;
                if (s == 0) s = 1;
                for (int i = 0; i < 4; i++) Next();
            }

            uint Next()
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                return s;
            }

            public float Value() => (Next() >> 8) * (1f / 16777216f);
            public float Signed() => Value() * 2f - 1f;
            public float Range(float a, float b) => a + (b - a) * Value();
        }

        /// <summary>Low-passed noise with about unit standard deviation: slow, random, never repeating wobble.</summary>
        sealed class SlowNoise
        {
            readonly Rng rng;
            readonly float a, gain;
            float v;

            public SlowNoise(Rng rng, float hz, int fs)
            {
                this.rng = rng;
                a = 1f - (float)Math.Exp(-TwoPi * hz / fs);
                gain = (float)Math.Sqrt(3.0 * (2.0 - a) / a);
            }

            public float Next()
            {
                v += a * (rng.Signed() * gain - v);
                return v;
            }
        }

        /// <summary>Second-order filter (RBJ cookbook), transposed direct form II.</summary>
        sealed class Biquad
        {
            float b0 = 1f, b1, b2, a1, a2, z1, z2;

            public static Biquad LowPass(float hz, float q, int fs)
            {
                Setup(hz, q, fs, out float c, out float alpha, out float a0);
                var f = new Biquad();
                f.b0 = (1f - c) * 0.5f / a0; f.b1 = (1f - c) / a0; f.b2 = f.b0;
                f.a1 = -2f * c / a0; f.a2 = (1f - alpha) / a0;
                return f;
            }

            public static Biquad HighPass(float hz, float q, int fs)
            {
                Setup(hz, q, fs, out float c, out float alpha, out float a0);
                var f = new Biquad();
                f.b0 = (1f + c) * 0.5f / a0; f.b1 = -(1f + c) / a0; f.b2 = f.b0;
                f.a1 = -2f * c / a0; f.a2 = (1f - alpha) / a0;
                return f;
            }

            public static Biquad BandPass(float hz, float q, int fs)
            {
                Setup(hz, q, fs, out float c, out float alpha, out float a0);
                var f = new Biquad();
                f.b0 = alpha / a0; f.b1 = 0f; f.b2 = -alpha / a0;
                f.a1 = -2f * c / a0; f.a2 = (1f - alpha) / a0;
                return f;
            }

            static void Setup(float hz, float q, int fs, out float c, out float alpha, out float a0)
            {
                float w = TwoPi * Math.Min(hz, fs * 0.45f) / fs;
                c = Cos(w);
                alpha = Sin(w) / (2f * q);
                a0 = 1f + alpha;
            }

            public float Process(float x)
            {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }

        /// <summary>
        /// A two-pole ringing filter: hit it and it sings at <c>freq</c> for about <c>t60</c> seconds. Cheap to retune
        /// while it runs (call <see cref="Set"/> every few samples), which is how the whistle and the vowel move.
        /// </summary>
        sealed class Resonator
        {
            float c, r2, gain, y1, y2;

            public void Set(float freq, float t60, int fs)
            {
                float r = (float)Math.Exp(-6.9078 / (t60 * fs));
                float w = TwoPi * Math.Min(freq, fs * 0.45f) / fs;
                c = 2f * r * Cos(w);
                r2 = r * r;
                gain = Sin(w);
            }

            public float Process(float x)
            {
                float y = x * gain + c * y1 - r2 * y2;
                y2 = y1;
                y1 = y;
                return y;
            }
        }

        /// <summary>Feedback delay whose repeats get duller each time round (a one-pole low-pass inside the loop).</summary>
        sealed class DampedEcho
        {
            readonly float[] buf;
            readonly float feedback, damp;
            int pos;
            float lp;

            public DampedEcho(int delaySamples, float feedback, float damp)
            {
                buf = new float[Math.Max(1, delaySamples)];
                this.feedback = feedback;
                this.damp = Clamp(damp, 0.01f, 1f);
            }

            public float Process(float x)
            {
                lp += damp * (buf[pos] - lp);
                buf[pos] = x + lp * feedback;
                if (++pos >= buf.Length) pos = 0;
                return lp;
            }
        }

        /// <summary>Eight damped comb filters and four allpasses (Schroeder/Freeverb layout) scaled up to cave size: long, dark, dense.</summary>
        sealed class CaveReverb
        {
            static readonly float[] CombMs = { 57.1f, 63.3f, 70.7f, 77.9f, 85.3f, 93.1f, 101.7f, 109.3f };
            static readonly float[] AllpassMs = { 20.4f, 15.2f, 10.0f, 6.7f };

            readonly float[][] comb = new float[CombMs.Length][];
            readonly int[] combPos = new int[CombMs.Length];
            readonly float[] combLp = new float[CombMs.Length];
            readonly float[] combG = new float[CombMs.Length];
            readonly float[][] ap = new float[AllpassMs.Length][];
            readonly int[] apPos = new int[AllpassMs.Length];
            readonly float[] pre;
            int prePos;
            readonly float damp;

            public CaveReverb(int fs, float rt60, float damp, float preDelay)
            {
                this.damp = Clamp(damp, 0f, 0.95f);
                for (int i = 0; i < comb.Length; i++)
                {
                    comb[i] = new float[(int)(CombMs[i] * 0.001f * fs)];
                    combG[i] = (float)Math.Pow(10.0, -3.0 * CombMs[i] * 0.001 / rt60);
                }
                for (int i = 0; i < ap.Length; i++)
                    ap[i] = new float[(int)(AllpassMs[i] * 0.001f * fs)];
                pre = new float[Math.Max(1, (int)(preDelay * fs))];
            }

            public float Process(float input)
            {
                float x = pre[prePos];
                pre[prePos] = input;
                if (++prePos >= pre.Length) prePos = 0;

                float sum = 0f;
                for (int i = 0; i < comb.Length; i++)
                {
                    float[] b = comb[i];
                    float y = b[combPos[i]];
                    combLp[i] = combLp[i] * damp + y * (1f - damp);
                    b[combPos[i]] = x * (1f - combG[i]) + combLp[i] * combG[i];
                    if (++combPos[i] >= b.Length) combPos[i] = 0;
                    sum += y;
                }
                sum *= 1f / comb.Length;

                for (int i = 0; i < ap.Length; i++)
                {
                    float[] b = ap[i];
                    float delayed = b[apPos[i]];
                    float y = delayed - sum;
                    b[apPos[i]] = sum + delayed * 0.5f;
                    if (++apPos[i] >= b.Length) apPos[i] = 0;
                    sum = y;
                }
                return sum;
            }
        }
    }
}
