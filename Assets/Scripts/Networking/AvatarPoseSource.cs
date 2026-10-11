using UnityEngine;
using UnityEngine.XR;

namespace GorillaNet
{
    /// <summary>
    /// Where the local player's head and hands are in the world. <see cref="NetworkAvatar"/> copies these poses
    /// into the networked avatar. Swap the source to drive an avatar from something other than the VR rig
    /// (a bot, a replay, a test) by setting <see cref="NetworkAvatar.PoseSourceFactory"/>.
    /// </summary>
    public interface IAvatarPoseSource
    {
        /// <summary>World-space poses. Returns false while the source isn't ready (e.g. the rig isn't in the scene yet).</summary>
        bool TryGetPoses(out Pose head, out Pose leftHand, out Pose rightHand);
    }

    /// <summary>What one hand's controller is doing, as far as the avatar's fingers are concerned (all 0 = open hand, 1 = fully pressed).</summary>
    [System.Serializable]
    public struct HandInput
    {
        /// <summary>The grip (middle finger) squeeze: curls the ring finger.</summary>
        [Range(0f, 1f)] public float grip;
        /// <summary>The trigger (index finger) pull: curls the pointer finger.</summary>
        [Range(0f, 1f)] public float trigger;
        /// <summary>The A (right) / X (left) button: curls the thumb.</summary>
        [Range(0f, 1f)] public float thumb;

        public HandInput(float grip, float trigger, float thumb)
        {
            this.grip = grip;
            this.trigger = trigger;
            this.thumb = thumb;
        }

        /// <summary>The three values as one int (8 bits each), which is what goes over the network.</summary>
        public int Pack() => Quantize(grip) | (Quantize(trigger) << 8) | (Quantize(thumb) << 16);

        public static HandInput Unpack(int bits) =>
            new HandInput((bits & 0xFF) / 255f, ((bits >> 8) & 0xFF) / 255f, ((bits >> 16) & 0xFF) / 255f);

        static int Quantize(float value) => Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
    }

    /// <summary>
    /// A pose source that also knows what the controllers' fingers are doing. <see cref="NetworkAvatar"/> sends it along
    /// with the poses so the other players see the hands close. Optional: sources without it keep the fingers open.
    /// </summary>
    public interface IAvatarHandInputSource
    {
        bool TryGetHandInput(out HandInput leftHand, out HandInput rightHand);
    }

    /// <summary>Reads the poses from the Gorilla locomotion rig (<see cref="GorillaLocomotion.Player"/>).</summary>
    public class GorillaRigPoseSource : IAvatarPoseSource, IAvatarHandInputSource
    {
        InputDevice leftDevice;
        InputDevice rightDevice;

        /// <summary>Grip, trigger and A / X straight from the controllers (UnityEngine.XR, the same source the hand grabbers use).</summary>
        public bool TryGetHandInput(out HandInput leftHand, out HandInput rightHand)
        {
            leftHand = Read(ref leftDevice, XRNode.LeftHand);
            rightHand = Read(ref rightDevice, XRNode.RightHand);
            return true;
        }

        static HandInput Read(ref InputDevice device, XRNode node)
        {
            var input = new HandInput();
            if (!device.isValid)
                device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid)
                return input;

            if (device.TryGetFeatureValue(CommonUsages.grip, out float grip))
                input.grip = grip;
            else if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool gripButton))
                input.grip = gripButton ? 1f : 0f;

            if (device.TryGetFeatureValue(CommonUsages.trigger, out float trigger))
                input.trigger = trigger;
            else if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButton))
                input.trigger = triggerButton ? 1f : 0f;

            // A on the right controller, X on the left one.
            if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary))
                input.thumb = primary ? 1f : 0f;
            return input;
        }

        public bool TryGetPoses(out Pose head, out Pose leftHand, out Pose rightHand)
        {
            head = leftHand = rightHand = Pose.identity;

            var player = GorillaLocomotion.Player.Instance;
            if (player == null || player.leftHandFollower == null || player.rightHandFollower == null)
                return false;

            Transform headTransform = player.headCollider != null ? player.headCollider.transform : null;
            if (headTransform == null && Camera.main != null)
                headTransform = Camera.main.transform;
            if (headTransform == null)
                return false;

            head = new Pose(headTransform.position, headTransform.rotation);

            // Hands sit where the visible hand spheres are (the locomotion followers, which stop at walls and arm length),
            // turned like the tracked controllers; the followers themselves never rotate.
            leftHand = new Pose(player.leftHandFollower.position,
                player.leftHandTransform != null ? player.leftHandTransform.rotation : player.leftHandFollower.rotation);
            rightHand = new Pose(player.rightHandFollower.position,
                player.rightHandTransform != null ? player.rightHandTransform.rotation : player.rightHandFollower.rotation);
            return true;
        }
    }

    /// <summary>
    /// Shows another source's poses as if in a mirror standing <c>distance</c> meters in front of where the player looked
    /// when the first pose arrived. Used for the solo mirror avatar: you wave, your mirror image waves back.
    /// The mirror plane stays where it was put; walk around it and the image stays behind it.
    /// </summary>
    public class MirroredPoseSource : IAvatarPoseSource, IAvatarHandInputSource
    {
        readonly IAvatarPoseSource inner;
        readonly float distance;
        bool planeReady;
        Vector3 planePoint;
        Vector3 planeNormal;

        public MirroredPoseSource(IAvatarPoseSource inner, float distance)
        {
            this.inner = inner;
            this.distance = distance;
        }

        public bool TryGetPoses(out Pose head, out Pose leftHand, out Pose rightHand)
        {
            head = leftHand = rightHand = Pose.identity;
            if (!inner.TryGetPoses(out Pose h, out Pose l, out Pose r))
                return false;

            if (!planeReady)
            {
                Vector3 forward = Vector3.ProjectOnPlane(h.rotation * Vector3.forward, Vector3.up);
                forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
                planePoint = h.position + forward * distance;
                planeNormal = -forward; // the mirror faces the player
                planeReady = true;
            }

            head = Mirror(h);
            // In a mirror your right hand is the image's left hand.
            leftHand = Mirror(r);
            rightHand = Mirror(l);
            return true;
        }

        public bool TryGetHandInput(out HandInput leftHand, out HandInput rightHand)
        {
            leftHand = rightHand = default;
            if (!(inner is IAvatarHandInputSource source) || !source.TryGetHandInput(out HandInput l, out HandInput r))
                return false;
            // The image's left hand is your right hand.
            leftHand = r;
            rightHand = l;
            return true;
        }

        Pose Mirror(Pose pose)
        {
            Vector3 position = pose.position - 2f * Vector3.Dot(pose.position - planePoint, planeNormal) * planeNormal;
            Vector3 forward = Vector3.Reflect(pose.rotation * Vector3.forward, planeNormal);
            Vector3 up = Vector3.Reflect(pose.rotation * Vector3.up, planeNormal);
            return new Pose(position, Quaternion.LookRotation(forward, up));
        }
    }
}
