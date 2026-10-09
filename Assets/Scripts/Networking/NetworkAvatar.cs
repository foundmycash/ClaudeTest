using System;
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

        [Tooltip("Turns the player's color around the hue wheel (0-1). The solo mirror uses it to look different from your own avatar.")]
        [Range(0f, 1f)] public float hueShift;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>Everybody gets a different, stable color from their player number (golden-ratio steps around the hue wheel).</summary>
        public static Color ColorForPlayer(PlayerRef player, float saturation = 0.65f, float value = 0.95f, float hueShift = 0f)
        {
            if (player == PlayerRef.None)
                return Color.gray;
            return Color.HSVToRGB(Mathf.Repeat(player.PlayerId * 0.61803398f + hueShift, 1f), saturation, value);
        }

        /// <summary>The player this avatar belongs to.</summary>
        public PlayerRef Owner => Object != null && Object.IsValid ? Object.StateAuthority : PlayerRef.None;

        /// <summary>True for the avatar this peer controls (the one that follows the local rig).</summary>
        public bool IsMine => Object != null && Object.IsValid && Object.HasStateAuthority;

        Renderer[] allRenderers;
        IAvatarPoseSource poseSource;

        public override void Spawned()
        {
            allRenderers = GetComponentsInChildren<Renderer>(true);

            var block = new MaterialPropertyBlock();
            Color color = Color.Lerp(Color.white, ColorForPlayer(Owner, colorSaturation, colorValue, hueShift), tintStrength);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            foreach (var r in tintedRenderers)
            {
                if (r != null)
                    r.SetPropertyBlock(block);
            }

            bool visible = !IsMine || showOwnAvatar;
            foreach (var r in allRenderers)
                r.enabled = visible;

            if (IsMine)
            {
                // Put the parts where the player is right away, so nobody sees the avatar blink in at the room origin.
                if (poseSource == null)
                    poseSource = PoseSourceFactory(Runner);
                FollowPoseSource();
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            poseSource = null;
        }

        /// <summary>Use this source instead of <see cref="PoseSourceFactory"/> for this avatar. Call it before the avatar is spawned.</summary>
        public void SetPoseSource(IAvatarPoseSource source)
        {
            poseSource = source;
        }

        void LateUpdate()
        {
            // After the rig has moved this frame (Player.Update), before the next network tick reads the transforms.
            if (poseSource != null && IsMine)
                FollowPoseSource();
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
