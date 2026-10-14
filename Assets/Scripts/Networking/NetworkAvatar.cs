using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace GorillaNet
{
    /// <summary>
    /// A player's body in the room: a head and two hands that every other player sees moving.
    /// Spawned once per player by <see cref="AvatarSpawner"/>; the player who spawned it is its state authority.
    ///
    /// The head and the hands are child objects with a NetworkTransform each. On the authority this script copies the
    /// local rig's poses into them every frame (so the NetworkTransforms need "Disable Shared Mode Interpolation", the
    /// poses are written in LateUpdate and not in FixedUpdateNetwork), on everyone else the NetworkTransforms
    /// interpolate them. The authority doesn't render its own avatar, you'd be looking at the inside of your head.
    ///
    /// The prefab must not have colliders: the avatar sits exactly where the player's hands and head are, so a collider
    /// would catch the Gorilla locomotion sphere casts and stick the player to their own hands.
    /// </summary>
    public class NetworkAvatar : NetworkBehaviour
    {
        /// <summary>
        /// Creates the pose source for the avatar this peer owns. The default reads the Gorilla rig;
        /// replace it to drive avatars from somewhere else (bots, tests).
        /// </summary>
        public static Func<NetworkRunner, IAvatarPoseSource> PoseSourceFactory = runner => new GorillaRigPoseSource();

        /// <summary>Every avatar that is spawned on this peer right now: your own and everybody else's. For systems that need to know who is in the room (the monster hunts them).</summary>
        public static readonly List<NetworkAvatar> Active = new List<NetworkAvatar>();

        /// <summary>Raised on this peer when an avatar has spawned (after its owner is known).</summary>
        public static event Action<NetworkAvatar> AvatarSpawned;

        /// <summary>Raised on this peer when an avatar is about to go away.</summary>
        public static event Action<NetworkAvatar> AvatarDespawned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            AvatarSpawned = null;
            AvatarDespawned = null;
        }

        [Header("Parts (each one has a NetworkTransform)")]
        public Transform head;
        public Transform leftHand;
        public Transform rightHand;

        [Header("Look")]
        [Tooltip("Renderers that get the player's color. Other renderers keep their material as it is.")]
        public Renderer[] tintedRenderers;
        [Range(0f, 1f)] public float colorSaturation = 0.65f;
        [Range(0f, 1f)] public float colorValue = 0.95f;

        [Tooltip("How much of the player's color is multiplied into the tinted renderers (0 = the material's own look, 1 = full color). The gorilla's fur is dark, so a strong tint is needed to tell players apart.")]
        [Range(0f, 1f)] public float tintStrength = 1f;

        [Tooltip("Debug: also draw your own avatar, to see what the other players see.")]
        public bool showOwnAvatar;

        /// <summary>Same as <see cref="showOwnAvatar"/>, but takes effect right away on an avatar that is already spawned.</summary>
        public bool ShowOwnAvatar
        {
            get => showOwnAvatar;
            set
            {
                if (showOwnAvatar == value)
                    return;
                showOwnAvatar = value;
                ApplyVisibility();
            }
        }

        [Tooltip("Turns the player's color around the hue wheel (0-1). The solo mirror uses it to look different from your own avatar.")]
        [Range(0f, 1f)] public float hueShift;

        [Header("Fingers: PC debug (editor and PC builds only, does nothing on the Quest)")]
        [Tooltip("Lets you test the fingers without a headset. Hold the keys, or drag the sliders below on the spawned avatar while the game runs; they are added to whatever the controllers do (the larger value wins), and other players see them too.\n" +
                 "Left hand:  1 = trigger,  2 = grip,  3 = X button.\n" +
                 "Right hand: 8 = trigger,  9 = grip,  0 = A button.\n" +
                 "(Pressing a key during the wake-up intro skips the intro.)")]
        public bool pcFingerDebug = true;
        public HandInput debugLeftHand;
        public HandInput debugRightHand;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>Everybody gets a different, stable color from their player number (golden-ratio steps around the hue wheel).</summary>
        public static Color ColorForPlayer(PlayerRef player, float saturation = 0.65f, float value = 0.95f, float hueShift = 0f)
        {
            if (player == PlayerRef.None)
                return Color.gray;
            return ColorForPlayerId(player.PlayerId, saturation, value, hueShift);
        }

        /// <summary>The same color from a plain player number (the <see cref="OfflineRig"/> has no <see cref="PlayerRef"/>).</summary>
        public static Color ColorForPlayerId(int playerId, float saturation = 0.65f, float value = 0.95f, float hueShift = 0f) =>
            Color.HSVToRGB(Mathf.Repeat(playerId * 0.61803398f + hueShift, 1f), saturation, value);

        /// <summary>The player this avatar belongs to.</summary>
        public PlayerRef Owner => Object != null && Object.IsValid ? Object.StateAuthority : PlayerRef.None;

        /// <summary>True for the avatar this peer controls (the one that follows the local rig).</summary>
        public bool IsMine => Object != null && Object.IsValid && Object.HasStateAuthority;

        /// <summary>The fingers of each hand (a <see cref="HandInput"/> packed into an int), written by the authority, read by everybody else.</summary>
        [Networked] public int LeftFingerBits { get; set; }
        [Networked] public int RightFingerBits { get; set; }

        Renderer[] allRenderers;
        IAvatarPoseSource poseSource;
        AvatarBodyRig bodyRig;
        int pendingLeftBits;
        int pendingRightBits;

        public override void Spawned()
        {
            allRenderers = GetComponentsInChildren<Renderer>(true);
            bodyRig = GetComponentInChildren<AvatarBodyRig>(true);

            var block = new MaterialPropertyBlock();
            Color color = Color.Lerp(Color.white, ColorForPlayer(Owner, colorSaturation, colorValue, hueShift), tintStrength);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            foreach (var r in tintedRenderers)
            {
                if (r != null)
                    r.SetPropertyBlock(block);
            }

            ApplyVisibility();

            if (IsMine)
            {
                // Put the parts where the player is right away, so nobody sees the avatar blink in at the room origin.
                if (poseSource == null)
                    poseSource = PoseSourceFactory(Runner);
                FollowPoseSource();
            }

            if (!Active.Contains(this))
                Active.Add(this);
            AvatarSpawned?.Invoke(this);
        }

        void ApplyVisibility()
        {
            if (allRenderers == null)
                return;
            bool visible = !IsMine || showOwnAvatar;
            foreach (var r in allRenderers)
            {
                if (r != null)
                    r.enabled = visible;
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            AvatarDespawned?.Invoke(this);
            Active.Remove(this);
            poseSource = null;
        }

        /// <summary>
        /// Adds the keyboard keys and the debug sliders to the controllers' input (editor and PC builds only). False when it is off.
        /// Shared with <see cref="OfflineRig"/>, so the keys work the same offline.
        /// </summary>
        public static bool ApplyPcFingerDebug(bool enabled, HandInput sliderLeft, HandInput sliderRight, ref HandInput left, ref HandInput right)
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            if (!enabled)
                return false;

            left = Larger(left, sliderLeft);
            right = Larger(right, sliderRight);
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null)
            {
                left = Larger(left, new HandInput(Held(keyboard.digit2Key), Held(keyboard.digit1Key), Held(keyboard.digit3Key)));
                right = Larger(right, new HandInput(Held(keyboard.digit9Key), Held(keyboard.digit8Key), Held(keyboard.digit0Key)));
            }
#endif
            return true;
#else
            return false;
#endif
        }

#if (UNITY_EDITOR || UNITY_STANDALONE) && ENABLE_INPUT_SYSTEM
        static float Held(UnityEngine.InputSystem.Controls.KeyControl key) => key.isPressed ? 1f : 0f;
#endif

        static HandInput Larger(HandInput a, HandInput b) =>
            new HandInput(Mathf.Max(a.grip, b.grip), Mathf.Max(a.trigger, b.trigger), Mathf.Max(a.thumb, b.thumb));

        /// <summary>Use this source instead of <see cref="PoseSourceFactory"/> for this avatar. Call it before the avatar is spawned.</summary>
        public void SetPoseSource(IAvatarPoseSource source)
        {
            poseSource = source;
        }

        void LateUpdate()
        {
            // After the rig has moved this frame (Player.Update), before the next network tick reads the transforms.
            if (poseSource != null && IsMine)
            {
                FollowPoseSource();
                FollowFingers();
            }
            else if (bodyRig != null && Object != null && Object.IsValid)
            {
                // Somebody else's avatar: the fingers the authority sent. (AvatarBodyRig runs after this and eases towards them.)
                bodyRig.SetHandInput(HandInput.Unpack(LeftFingerBits), HandInput.Unpack(RightFingerBits));
            }
        }

        // Runs on the state authority only (Shared mode): sends what LateUpdate read from the controllers this frame.
        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority)
                return;
            if (LeftFingerBits != pendingLeftBits)
                LeftFingerBits = pendingLeftBits;
            if (RightFingerBits != pendingRightBits)
                RightFingerBits = pendingRightBits;
        }

        void FollowFingers()
        {
            HandInput left = default, right = default;
            bool hasController = poseSource is IAvatarHandInputSource source && source.TryGetHandInput(out left, out right);
            bool debugged = ApplyPcFingerDebug(pcFingerDebug, debugLeftHand, debugRightHand, ref left, ref right);
            if (!hasController && !debugged)
                return;

            pendingLeftBits = left.Pack();
            pendingRightBits = right.Pack();
            // Your own avatar (the solo view) shows them at once, without waiting for the network.
            if (bodyRig != null)
                bodyRig.SetHandInput(left, right);
        }

        void FollowPoseSource()
        {
            if (!poseSource.TryGetPoses(out Pose headPose, out Pose leftPose, out Pose rightPose))
                return;

            head.SetPositionAndRotation(headPose.position, headPose.rotation);
            leftHand.SetPositionAndRotation(leftPose.position, leftPose.rotation);
            rightHand.SetPositionAndRotation(rightPose.position, rightPose.rotation);
        }
    }
}
