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

        Light spot;
        AudioSource audioSource;
        Transform mountedTo;
        bool wasPressed;
        float flickerUntil;
        float flickerSeed;

        /// <summary>Switches the flashlight on or off.</summary>
        public void SetOn(bool on)
        {
            if (IsOn == on) return;
            IsOn = on;
            if (spot != null) spot.enabled = on;
            if (Toggled != null) Toggled(on);
        }

        /// <summary>Switches it over and gives the click and buzz, as the button does.</summary>
        public void Toggle()
        {
            SetOn(!IsOn);
            Click();
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
            }
            Configure(spot);
            spot.enabled = false;
            IsOn = false;
            if (startOn) SetOn(true);
        }

        void OnDisable()
        {
            if (spot != null) spot.enabled = false;
        }

        void OnDestroy()
        {
            if (spot != null) Destroy(spot.gameObject);
        }

        void OnValidate()
        {
            if (spot != null) Configure(spot);
        }

        void Update()
        {
            if (!Application.isPlaying || spot == null) return;
            Attach();

            bool pressed = ButtonDown();
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if ((pressed && !wasPressed) || (kb != null && keyboardKey != UnityEngine.InputSystem.Key.None && kb[keyboardKey].wasPressedThisFrame))
                Toggle();
            wasPressed = pressed;

            float level = 1f;
            if (Time.time < flickerUntil)
            {
                float n = Mathf.PerlinNoise(flickerSeed, Time.time * 18f);
                level = n < 0.35f ? 0f : n;
            }
            spot.intensity = intensity * level;
            spot.enabled = IsOn && level > 0f;
        }

        bool ButtonDown()
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
            if (!device.isValid) return false;
            InputFeatureUsage<bool> usage = button == ForestFlashlightButton.Primary ? CommonUsages.primaryButton : CommonUsages.secondaryButton;
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
            if (haptics)
            {
                InputDevice device = InputDevices.GetDeviceAtXRNode(hand);
                if (device.isValid) device.SendHapticImpulse(0u, 0.25f, 0.03f);
            }
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
