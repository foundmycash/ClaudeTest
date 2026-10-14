using System;
using UnityEngine;
using UnityEngine.XR;

namespace HavocForest
{
    /// <summary>Where the flashlight is held.</summary>
    public enum ForestFlashlightMount
    {
        /// <summary>On the head (like a head torch): the beam goes where the player looks.</summary>
        Head,
        RightHand,
        LeftHand
    }

    /// <summary>The controller button that switches the flashlight.</summary>
    public enum ForestFlashlightButton
    {
        /// <summary>A on the right controller, X on the left.</summary>
        Primary,
        /// <summary>B on the right controller, Y on the left.</summary>
        Secondary
    }

    /// <summary>
    /// The player's flashlight: a real-time spot light with a cookie (the beam pattern), switched with a controller
    /// button (A by default). It is created when the game starts and follows the head or a hand of the Gorilla rig.
    ///
    /// The forest shaders and the volumetric fog both light with it, so the beam shows on the ground and trees and as a
    /// shaft in the fog. It costs one real-time light per pixel while it is on (see the README, Night and flashlight).
    /// Horror hooks: <see cref="SetOn"/>, <see cref="Flicker"/>, <see cref="IsOn"/> and the <see cref="Toggled"/> event.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Forest Flashlight")]
    public class ForestFlashlight : MonoBehaviour
    {
        [Header("Switch")]
        [Tooltip("The controller whose button switches the light.")]
        public XRNode hand = XRNode.RightHand;

        [Tooltip("Primary = A (right controller) / X (left). Secondary = B / Y.")]
        public ForestFlashlightButton button = ForestFlashlightButton.Primary;

        [Tooltip("Keyboard key that switches it too, for testing in the editor without a headset.")]
        public UnityEngine.InputSystem.Key keyboardKey = UnityEngine.InputSystem.Key.F;

        [Tooltip("On when the game starts.")]
        public bool startOn;

        [Tooltip("A short buzz in the controller when it is switched.")]
        public bool haptics = true;

        [Tooltip("Sound played when it is switched. Empty = a built-in click.")]
        public AudioClip clickSound;

        [Range(0f, 1f)] public float clickVolume = 0.4f;

        [Header("Light")]
        [Tooltip("The beam pattern (a Cookie texture, Spot light type).")]
        public Texture cookie;

        public Color color = new Color(1f, 0.93f, 0.82f);

        [Tooltip("Brightness. URP spot lights fall off with the square of the distance, so this needs to be high for the beam to reach far.")]
        [Min(0f)] public float intensity = 24f;

        [Tooltip("How far the light reaches, in metres.")]
        [Min(1f)] public float range = 30f;

        [Tooltip("Width of the cone in degrees. The cookie's bright disc covers about the middle 90% of it.")]
        [Range(5f, 150f)] public float spotAngle = 60f;

        [Tooltip("Inner cone (full brightness) as a share of the cone.")]
        [Range(0f, 1f)] public float innerAngle = 0.5f;

        [Header("High beam (B)")]
        [Tooltip("The button that switches the high beam on and off (the low beam keeps 'Button').")]
        public ForestFlashlightButton highBeamButton = ForestFlashlightButton.Secondary;

        [Tooltip("Keyboard key for the high beam, for testing in the editor.")]
        public UnityEngine.InputSystem.Key highBeamKey = UnityEngine.InputSystem.Key.G;

        [Tooltip("Each activation lasts a random time between these two (seconds, both included), then the light short-circuits.")]
        [Min(1f)] public float highBeamMinSeconds = 15f;
        [Min(1f)] public float highBeamMaxSeconds = 23f;

        [Tooltip("After a short circuit the high beam cannot be used for this long (seconds). Switching it off by hand costs nothing.")]
        [Min(0f)] public float cooldownSeconds = 30f;

        [Tooltip("The high beam's brightness / reach as a multiple of the low beam's.")]
        [Min(1f)] public float highIntensityMultiplier = 2.6f;
        [Min(1f)] public float highRangeMultiplier = 1.5f;
        public Color highColor = new Color(0.88f, 0.95f, 1f);

        [Tooltip("The fog is cleared harder in the high beam.")]
        [Range(0f, 1f)] public float highFogClearStrength = 0.95f;

        [Tooltip("The stutter when the high beam starts (seconds).")]
        [Min(0f)] public float startupFlickerSeconds = 0.4f;

        [Tooltip("The monster is driven off by the high beam (not the low beam): it drops a chase and cannot start one while lit.")]
        public bool repelMonster = true;
        [Min(1f)] public float repelRange = 22f;
        [Tooltip("How long the monster stays put-off after the beam last touched it.")]
        [Min(0f)] public float repelSeconds = 4f;

        [Header("Sounds (empty = a built-in sound)")]
        public AudioClip lowHumSound;
        public AudioClip highStartSound;
        public AudioClip highHumSound;
        public AudioClip shortCircuitSound;
        [Range(0f, 1f)] public float lowHumVolume = 0.05f;
        [Range(0f, 1f)] public float highStartVolume = 0.6f;
        [Range(0f, 1f)] public float highHumVolume = 0.3f;
        [Range(0f, 1f)] public float shortCircuitVolume = 0.8f;

        [Header("Gauge")]
        [Tooltip("The ring and battery that show the high beam's time left, then its cooldown. Hidden while the flashlight is off.")]
        public bool showGauge = true;
        [Tooltip("Position in front of the head camera (metres): low in the right of the view.")]
        public Vector3 gaugeOffset = new Vector3(0.17f, -0.15f, 0.55f);
        [Min(0.01f)] public float gaugeSize = 0.07f;

        [Header("Fog")]
        [Tooltip("The beam pushes the volumetric fog back inside its cone, so the lit ground and trees stay visible through the fog. Needs the project's Volumetric Fog; off = the fog is the same everywhere. Unity's built-in fog is not affected.")]
        public bool clearFogInBeam = true;

        [Tooltip("How much of the fog is removed inside the beam. 1 = none left (also no visible shaft of light); around 0.75 keeps a faint shaft.")]
        [Range(0f, 1f)] public float fogClearStrength = 0.75f;

        [Tooltip("How much wider than the light's cone the cleared cone is (1 = the same width).")]
        [Range(0.5f, 1.5f)] public float fogClearWidth = 1f;

        [Tooltip("Seconds the fog takes to part (or close again) when the flashlight is switched.")]
        [Min(0f)] public float fogClearFade = 0.3f;

        [Header("Mount")]
        [Tooltip("Where the flashlight is held. In Gorilla locomotion the hands swing all the time, so the head is the calmer choice.")]
        public ForestFlashlightMount mount = ForestFlashlightMount.Head;

        [Tooltip("Offset from the head / hand, in its own space (metres). A little below the eyes gives the lit ground some shape.")]
        public Vector3 positionOffset = new Vector3(0f, -0.12f, 0.05f);

        [Tooltip("Rotation from the head / hand (degrees). For a hand mount, tilt it so the beam points along the controller.")]
        public Vector3 rotationOffset = new Vector3(4f, 0f, 0f);

        [Tooltip("The camera for the Head mount. Empty = Camera.main.")]
        public Camera headCamera;

        /// <summary>True while the flashlight is switched on (also during a flicker).</summary>
        public bool IsOn { get; private set; }

        /// <summary>Raised when the flashlight is switched on (true) or off (false).</summary>
        public event Action<bool> Toggled;

        /// <summary>The spot light, once the game runs.</summary>
        public Light Light { get { return spot; } }

        static readonly int FogClearPosId = Shader.PropertyToID("_FogClearPos");
        static readonly int FogClearDirId = Shader.PropertyToID("_FogClearDir");
        static readonly int FogClearParamsId = Shader.PropertyToID("_FogClearParams");

        /// <summary>True while the high beam is lit (it is also <see cref="IsOn"/>).</summary>
        public bool IsHighBeam { get { return IsOn && highActive; } }

        /// <summary>Seconds the high beam has left (0 when it is not lit).</summary>
        public float HighBeamRemaining { get { return highActive ? Mathf.Max(0f, highEnd - Time.time) : 0f; } }

        /// <summary>Seconds until the high beam can be used again after a short circuit (0 = ready).</summary>
        public float CooldownRemaining { get { return Mathf.Max(0f, cooldownEnd - Time.time); } }

        /// <summary>Raised when the high beam starts (true) or stops (false, by hand or by short circuit).</summary>
        public event Action<bool> HighBeamChanged;

        Light spot;
        float fogClearAmount;       // 0..1, follows the light (including flicker) with a short fade
        AudioSource audioSource;    // one-shots (click, start, zap)
        AudioSource humSource;      // the looping operating noise, one source for both beams
        Transform mountedTo;
        bool wasPressed;
        bool wasHighPressed;
        float flickerUntil;
        float flickerSeed;

        bool highActive;
        float highEnd;
        float highDuration = 1f;
        float cooldownEnd;
        bool wasCooldown;
        float curRange;
        float curFogStrength;
        float nextScan;
        float nextFind;
        MonsterAI.MonsterHunter[] hunters;
        readonly System.Collections.Generic.List<AudioClip> madeClips = new System.Collections.Generic.List<AudioClip>();
        AudioClip lowHumClip, highStartClip, highHumClip, zapClip;

        /// <summary>Switches the flashlight on or off. Off (by hand) also ends the high beam, without a cooldown.</summary>
        public void SetOn(bool on)
        {
            if (IsOn == on) return;
            IsOn = on;
            if (!on) EndHighBeam(false);
            if (spot != null) spot.enabled = on;
            RefreshHum();
            if (Toggled != null) Toggled(on);
        }

        /// <summary>Switches it over and gives the click and buzz, as the A button does (low beam, or off).</summary>
        public void Toggle()
        {
            SetOn(!IsOn);
            Click();
        }

        /// <summary>
        /// What the B button does: starts the high beam, or ends it (back to the low beam) when it is lit. While the
        /// cooldown runs it does nothing. Returns true when the high beam is lit afterwards.
        /// </summary>
        public bool ToggleHighBeam()
        {
            if (highActive)
            {
                EndHighBeam(false);
                Click();
                return false;
            }
            if (Time.time < cooldownEnd)
            {
                Haptic(0.1f, 0.02f);        // dead button
                return false;
            }
            StartHighBeam();
            return true;
        }

        void StartHighBeam()
        {
            if (spot == null) return;
            if (!IsOn) SetOn(true);
            highDuration = UnityEngine.Random.Range(Mathf.Min(highBeamMinSeconds, highBeamMaxSeconds), Mathf.Max(highBeamMinSeconds, highBeamMaxSeconds));
            highEnd = Time.time + highDuration;
            highActive = true;
            Flicker(startupFlickerSeconds);
            PlayOneShot(highStartClip, highStartVolume);
            Haptic(0.5f, 0.12f);
            RefreshHum();
            if (HighBeamChanged != null) HighBeamChanged(true);
        }

        // Ends the high beam (back to the low beam, which stays on). A short circuit also zaps and starts the cooldown.
        void EndHighBeam(bool shortCircuit)
        {
            if (!highActive) return;        // guards against double zaps / double cooldowns
            highActive = false;
            if (shortCircuit)
            {
                cooldownEnd = Time.time + cooldownSeconds;
                flickerUntil = 0f;
                Flicker(0.3f);
                PlayOneShot(zapClip, shortCircuitVolume);
                Haptic(0.9f, 0.2f);
            }
            RefreshHum();
            if (HighBeamChanged != null) HighBeamChanged(false);
        }

        void PlayOneShot(AudioClip clip, float volume)
        {
            if (audioSource == null || clip == null || volume <= 0f) return;
            audioSource.PlayOneShot(clip, volume);
        }

        // The operating noise follows the mode: quiet for the low beam, louder for the high beam, none when off.
        void RefreshHum()
        {
            if (humSource == null) return;
            if (!IsOn)
            {
                humSource.Stop();
                return;
            }
            AudioClip clip = highActive ? highHumClip : lowHumClip;
            humSource.volume = highActive ? highHumVolume : lowHumVolume;
            humSource.pitch = highActive ? 1f : 0.7f;
            if (clip == null) { humSource.Stop(); return; }
            if (humSource.clip != clip || !humSource.isPlaying)
            {
                humSource.clip = clip;
                humSource.Play();
            }
        }

        /// <summary>Makes the beam stutter for a while (a horror event). It stays on or off as it was.</summary>
        public void Flicker(float seconds)
        {
            flickerUntil = Mathf.Max(flickerUntil, Time.time + seconds);
            flickerSeed = UnityEngine.Random.value * 100f;
        }

        /// <summary>Sets a light up the way the flashlight uses it (also for previews and tests).</summary>
        public void Configure(Light l)
        {
            l.type = LightType.Spot;
            l.cookie = cookie;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.spotAngle = spotAngle;
            l.innerSpotAngle = spotAngle * innerAngle;
            l.shadows = LightShadows.None;                  // no real-time shadows on Quest
            l.renderMode = LightRenderMode.ForcePixel;      // always a per-pixel light, first in the per-object light lists
        }

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (spot == null)
            {
                var go = new GameObject("Flashlight");
                spot = go.AddComponent<Light>();
                audioSource = go.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
                audioSource.spatialBlend = 0.6f;
                audioSource.volume = clickVolume;
                if (clickSound == null) clickSound = MakeClick();

                humSource = go.AddComponent<AudioSource>();
                humSource.playOnAwake = false;
                humSource.loop = true;
                humSource.spatialBlend = 0.6f;
                // Resources/Flashlight/Buzz.mp3 is the operating noise (low beam: quiet and lower in pitch), Zap.mp3 the short circuit.
                AudioClip buzz = Resources.Load<AudioClip>("Flashlight/Buzz");
                AudioClip zap = Resources.Load<AudioClip>("Flashlight/Zap");
                lowHumClip = lowHumSound != null ? lowHumSound : buzz != null ? buzz : Made(MakeLowHum());
                highHumClip = highHumSound != null ? highHumSound : buzz != null ? buzz : Made(MakeHighHum());
                highStartClip = highStartSound != null ? highStartSound : Made(MakeHighStart());
                zapClip = shortCircuitSound != null ? shortCircuitSound : zap != null ? zap : Made(MakeZap());
            }
            Configure(spot);
            spot.enabled = false;
            IsOn = false;
            highActive = false;
            fogClearAmount = 0f;
            curRange = range;
            curFogStrength = fogClearStrength;
            Application.onBeforeRender += PushFogClear;
            if (startOn) SetOn(true);
        }

        void OnDisable()
        {
            Application.onBeforeRender -= PushFogClear;
            Shader.SetGlobalVector(FogClearParamsId, Vector4.zero);     // the fog goes back to normal
            highActive = false;                                         // no timer keeps running, no cooldown for it
            if (humSource != null) humSource.Stop();
            if (audioSource != null) audioSource.Stop();
            if (gauge != null) gauge.SetActive(false);
            if (spot != null) spot.enabled = false;
        }

        void OnDestroy()
        {
            if (spot != null) Destroy(spot.gameObject);
            if (gauge != null) Destroy(gauge);
            if (gaugeMaterial != null) Destroy(gaugeMaterial);
            if (gaugeMesh != null) Destroy(gaugeMesh);
            foreach (AudioClip c in madeClips) if (c != null) Destroy(c);
            madeClips.Clear();
        }

        void OnValidate()
        {
            if (spot != null) Configure(spot);
        }

        void Update()
        {
            if (!Application.isPlaying || spot == null) return;
            Attach();

            bool pressed = ButtonDown(button);
            bool highPressed = highBeamButton != button && ButtonDown(highBeamButton);
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if ((pressed && !wasPressed) || (kb != null && keyboardKey != UnityEngine.InputSystem.Key.None && kb[keyboardKey].wasPressedThisFrame))
                Toggle();
            if ((highPressed && !wasHighPressed) || (kb != null && highBeamKey != UnityEngine.InputSystem.Key.None && kb[highBeamKey].wasPressedThisFrame))
                ToggleHighBeam();
            wasPressed = pressed;
            wasHighPressed = highPressed;

            if (highActive && Time.time >= highEnd) EndHighBeam(true);      // short circuit, once

            bool high = highActive;
            float level = 1f;
            if (Time.time < flickerUntil)
            {
                float n = Mathf.PerlinNoise(flickerSeed, Time.time * 18f);
                level = n < 0.35f ? 0f : n;
            }
            if (high)
            {
                // unstable: a slight constant waver, dips that get worse as the light is about to give out
                float t = Time.time;
                level *= 1f - 0.08f * Mathf.PerlinNoise(flickerSeed + 7f, t * 9f);
                if (highEnd - t < 3f && Mathf.PerlinNoise(flickerSeed + 13f, t * 14f) < 0.3f) level *= 0.35f;
            }
            float baseIntensity = high ? intensity * highIntensityMultiplier : intensity;
            curRange = high ? range * highRangeMultiplier : range;
            curFogStrength = high ? highFogClearStrength : fogClearStrength;
            spot.color = high ? highColor : color;
            spot.range = curRange;
            spot.intensity = baseIntensity * level;
            spot.enabled = IsOn && level > 0f;

            // The fog parts with the light: it follows the flicker too, and fades instead of snapping.
            float target = (IsOn && clearFogInBeam) ? level : 0f;
            fogClearAmount = fogClearFade > 0f ? Mathf.MoveTowards(fogClearAmount, target, Time.deltaTime / fogClearFade) : target;

            if (high && repelMonster) RepelMonsters();
            UpdateGauge();
        }

        // The high beam drives the monster off: anything in the cone, close enough and not behind a wall.
        void RepelMonsters()
        {
            if (Time.time < nextScan) return;
            nextScan = Time.time + 0.1f;
            if (hunters == null || Time.time >= nextFind)
            {
                hunters = FindObjectsByType<MonsterAI.MonsterHunter>(FindObjectsSortMode.None);
                nextFind = Time.time + 3f;
            }
            Transform lt = spot.transform;
            float half = spotAngle * 0.5f + 6f;
            float maxDist = Mathf.Min(repelRange, curRange);
            for (int i = 0; i < hunters.Length; i++)
            {
                MonsterAI.MonsterHunter h = hunters[i];
                if (h == null || !h.isActiveAndEnabled) continue;
                Vector3 p = h.transform.position + Vector3.up * 1.2f;
                Vector3 d = p - lt.position;
                if (d.sqrMagnitude > maxDist * maxDist || Vector3.Angle(lt.forward, d) > half) continue;
                RaycastHit hit;
                if (Physics.Linecast(lt.position, p, out hit, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(h.transform) && hit.transform.root != h.transform.root) continue;
                h.Repel(repelSeconds);
            }
        }

        void LateUpdate()
        {
            if (Application.isPlaying) PushFogClear();
        }

        // Hands the beam's cone to the volumetric fog shader (global values, see _FogClearParams there). Run again just
        // before rendering (Application.onBeforeRender) so it uses the same head pose as the light.
        void PushFogClear()
        {
            if (spot == null) return;
            float strength = curFogStrength * fogClearAmount;
            if (strength <= 0f)
            {
                Shader.SetGlobalVector(FogClearParamsId, Vector4.zero);
                return;
            }
            Transform t = spot.transform;
            float half = Mathf.Clamp(spotAngle * 0.5f * fogClearWidth, 1f, 85f);
            float inner = half * Mathf.Clamp01(innerAngle);
            Shader.SetGlobalVector(FogClearPosId, t.position);
            Shader.SetGlobalVector(FogClearDirId, t.forward);
            Shader.SetGlobalVector(FogClearParamsId, new Vector4(strength, Mathf.Cos(half * Mathf.Deg2Rad), Mathf.Cos(inner * Mathf.Deg2Rad), curRange));
        }

        bool ButtonDown(ForestFlashlightButton which)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
            if (!device.isValid) return false;
            InputFeatureUsage<bool> usage = which == ForestFlashlightButton.Primary ? CommonUsages.primaryButton : CommonUsages.secondaryButton;
            bool value;
            return device.TryGetFeatureValue(usage, out value) && value;
        }

        void Click()
        {
            if (audioSource != null && clickSound != null && clickVolume > 0f)
            {
                audioSource.volume = clickVolume;
                audioSource.PlayOneShot(clickSound);
            }
            Haptic(0.25f, 0.03f);
        }

        void Haptic(float amplitude, float seconds)
        {
            if (!haptics) return;
            InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
            if (device.isValid) device.SendHapticImpulse(0u, amplitude, seconds);
        }

        // ---- Gauge: a ring around a battery, one small quad on the head camera, drawn by Resources/Flashlight/FlashlightHud ----

        GameObject gauge;
        Material gaugeMaterial;
        Mesh gaugeMesh;
        Transform gaugeParent;
        float gaugeAlpha;
        float readyFlashUntil;

        static readonly int GFill = Shader.PropertyToID("_Fill");
        static readonly int GColor = Shader.PropertyToID("_Color");
        static readonly int GGlow = Shader.PropertyToID("_Glow");
        static readonly int GAlpha = Shader.PropertyToID("_Alpha");

        void BuildGauge()
        {
            Shader shader = Resources.Load<Shader>("Flashlight/FlashlightHud");
            if (shader == null) { showGauge = false; return; }
            gaugeMaterial = new Material(shader) { name = "Flashlight HUD" };
            gaugeMesh = new Mesh { name = "Flashlight HUD Quad" };
            gaugeMesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            gaugeMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            gaugeMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            gaugeMesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));
            gauge = new GameObject("Flashlight Gauge");
            gauge.AddComponent<MeshFilter>().sharedMesh = gaugeMesh;
            var mr = gauge.AddComponent<MeshRenderer>();
            mr.sharedMaterial = gaugeMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            gauge.SetActive(false);
        }

        void UpdateGauge()
        {
            if (!showGauge) { if (gauge != null) gauge.SetActive(false); return; }
            if (gauge == null) { BuildGauge(); if (gauge == null) return; }

            Camera cam = headCamera != null ? headCamera : Camera.main;
            if (cam != null && gaugeParent != cam.transform)
            {
                gaugeParent = cam.transform;
                gauge.transform.SetParent(gaugeParent, false);
            }
            gauge.transform.localPosition = gaugeOffset;
            gauge.transform.localRotation = Quaternion.identity;
            gauge.transform.localScale = Vector3.one * gaugeSize;

            float cd = CooldownRemaining;
            if (wasCooldown && cd <= 0f) readyFlashUntil = Time.time + 1.5f;       // "available again"
            wasCooldown = cd > 0f;

            float fill, glow, alpha;
            Color c;
            if (highActive)
            {
                float left = HighBeamRemaining;
                fill = left / Mathf.Max(0.01f, highDuration);
                bool low = left < 3f;
                c = low ? Color.Lerp(new Color(1f, 0.75f, 0.35f), new Color(1f, 0.15f, 0.08f), 0.5f + 0.5f * Mathf.Sin(Time.time * 14f)) : new Color(1f, 0.82f, 0.5f);
                glow = 1.1f;
                alpha = 0.95f;
            }
            else if (cd > 0f)
            {
                fill = 1f - cd / Mathf.Max(0.01f, cooldownSeconds);                // recharging
                c = new Color(0.65f, 0.1f, 0.07f);
                glow = 0.7f;
                alpha = 0.75f;
            }
            else if (Time.time < readyFlashUntil)
            {
                fill = 1f;
                c = new Color(0.6f, 1f, 0.7f);
                glow = 0.9f + 0.6f * Mathf.Abs(Mathf.Sin(Time.time * 8f));
                alpha = 1f;
            }
            else
            {
                fill = 1f;                                                         // ready, kept faint
                c = new Color(0.7f, 0.82f, 0.78f);
                glow = 0.6f;
                alpha = 0.4f;
            }

            gaugeAlpha = Mathf.MoveTowards(gaugeAlpha, IsOn ? alpha : 0f, Time.deltaTime * 4f);
            bool visible = gaugeAlpha > 0.003f && gaugeParent != null;
            if (gauge.activeSelf != visible) gauge.SetActive(visible);
            if (!visible) return;
            gaugeMaterial.SetFloat(GFill, fill);
            gaugeMaterial.SetColor(GColor, c);
            gaugeMaterial.SetFloat(GGlow, glow);
            gaugeMaterial.SetFloat(GAlpha, gaugeAlpha);
        }

        // Follows the head or a hand: parented to it, so it moves with the tracked pose of the same frame.
        void Attach()
        {
            Transform target = MountTransform();
            if (target == null || target == mountedTo) return;
            mountedTo = target;
            Transform t = spot.transform;
            t.SetParent(target, false);
            t.localPosition = positionOffset;
            t.localRotation = Quaternion.Euler(rotationOffset);
        }

        Transform MountTransform()
        {
            if (mount == ForestFlashlightMount.Head)
            {
                Camera cam = headCamera != null ? headCamera : Camera.main;
                return cam != null ? cam.transform : null;
            }
            GorillaLocomotion.Player player = GorillaLocomotion.Player.Instance;
            if (player == null) return null;
            if (mount == ForestFlashlightMount.RightHand) return player.rightHandFollower != null ? player.rightHandFollower : player.rightHandTransform;
            return player.leftHandFollower != null ? player.leftHandFollower : player.leftHandTransform;
        }

        AudioClip Made(AudioClip clip)
        {
            madeClips.Add(clip);
            return clip;
        }

        const int SynthRate = 22050;

        static AudioClip FromSamples(string name, float[] data)
        {
            AudioClip clip = AudioClip.Create(name, data.Length, 1, SynthRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Low beam: a faint transformer-like hum with a slow wobble. Whole cycles per second, so the loop is seamless.
        static AudioClip MakeLowHum()
        {
            var d = new float[SynthRate];
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SynthRate;
                float wobble = 0.8f + 0.2f * Mathf.Sin(2f * Mathf.PI * 3f * t);
                d[i] = (Mathf.Sin(2f * Mathf.PI * 100f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * 300f * t) * 0.25f + Mathf.Sin(2f * Mathf.PI * 1500f * t) * 0.05f) * wobble;
            }
            return FromSamples("Flashlight Low Hum", d);
        }

        // High beam: a harsh buzz (stacked harmonics), a thin whine and sparse crackle.
        static AudioClip MakeHighHum()
        {
            var d = new float[SynthRate];
            var rng = new System.Random(11);
            float crackle = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SynthRate;
                float buzz = 0f;
                for (int h = 1; h <= 6; h++) buzz += Mathf.Sin(2f * Mathf.PI * 120f * h * t) / h;
                float whine = Mathf.Sin(2f * Mathf.PI * 2400f * t) * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 7f * t));
                if (rng.NextDouble() < 0.0015) crackle = (float)(rng.NextDouble() * 2.0 - 1.0);
                crackle *= 0.97f;
                d[i] = Mathf.Clamp(buzz * 0.35f + whine * 0.12f + crackle * 0.5f, -1f, 1f);
            }
            return FromSamples("Flashlight High Hum", d);
        }

        // High beam start: a thump, then a charging whine climbing and stuttering.
        static AudioClip MakeHighStart()
        {
            var d = new float[SynthRate * 6 / 10];
            var rng = new System.Random(5);
            float phase = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SynthRate;
                float f = 150f + 1500f * Mathf.Pow(t / 0.6f, 2f);
                phase += 2f * Mathf.PI * f / SynthRate;
                float gate = Mathf.Sin(t * 2f * Mathf.PI * 30f) > -0.4f ? 1f : 0.3f;
                float env = Mathf.Clamp01(t / 0.35f) * Mathf.Clamp01((0.6f - t) / 0.08f);
                float thump = Mathf.Sin(2f * Mathf.PI * 70f * t) * Mathf.Exp(-t * 25f) + (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 120f) * 0.6f;
                d[i] = Mathf.Clamp(Mathf.Sin(phase) * 0.45f * env * gate + thump * 0.8f, -1f, 1f);
            }
            return FromSamples("Flashlight High Start", d);
        }

        // Short circuit: a sharp pop, a crackling burst and a falling whine.
        static AudioClip MakeZap()
        {
            var d = new float[SynthRate * 45 / 100];
            var rng = new System.Random(3);
            float phase = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SynthRate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float burst = noise * Mathf.Exp(-t * 14f) * (Mathf.Sin(t * 2f * Mathf.PI * 70f) > -0.2f ? 1f : 0.2f);
                float pop = noise * Mathf.Exp(-t * 200f);
                phase += 2f * Mathf.PI * Mathf.Lerp(3200f, 250f, Mathf.Clamp01(t / 0.3f)) / SynthRate;
                d[i] = Mathf.Clamp(pop * 1.2f + burst * 0.6f + Mathf.Sin(phase) * Mathf.Exp(-t * 9f) * 0.35f, -1f, 1f);
            }
            return FromSamples("Flashlight Short Circuit", d);
        }

        // A short mechanical click: a sharp noise burst with a dull knock under it.
        static AudioClip MakeClick()
        {
            const int rate = 44100;
            int n = rate * 40 / 1000;
            var data = new float[n];
            var rng = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 900f);
                float knock = Mathf.Sin(2f * Mathf.PI * 1900f * t) * Mathf.Exp(-t * 260f) * 0.5f;
                data[i] = (noise * 0.8f + knock) * 0.9f;
            }
            AudioClip clip = AudioClip.Create("Flashlight Click", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
