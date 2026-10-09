using UnityEngine;

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

    /// <summary>Reads the poses from the Gorilla locomotion rig (<see cref="GorillaLocomotion.Player"/>).</summary>
    public class GorillaRigPoseSource : IAvatarPoseSource
    {
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
    public class MirroredPoseSource : IAvatarPoseSource
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

        Pose Mirror(Pose pose)
        {
            Vector3 position = pose.position - 2f * Vector3.Dot(pose.position - planePoint, planeNormal) * planeNormal;
            Vector3 forward = Vector3.Reflect(pose.rotation * Vector3.forward, planeNormal);
            Vector3 up = Vector3.Reflect(pose.rotation * Vector3.up, planeNormal);
            return new Pose(position, Quaternion.LookRotation(forward, up));
        }
    }
}
