using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// Facts about the monster's body that the hunting scripts share: where its eyes are and which way the model
    /// faces. The Blender rig faces world -Z (not the object's +Z), so anything that walks it, or asks what it can
    /// see, has to know the difference or the monster walks tail first.
    ///
    /// With a <see cref="MonsterGaze"/> on the same object the eyes and the facing come from it (it measures the way
    /// the jaw opens), so the hunting vision looks from exactly where the head looks from. Without one, set them here.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Monster/Monster Body")]
    public class MonsterBody : MonoBehaviour
    {
        [Tooltip("The way the model faces when this object is not rotated. Auto asks the Monster Gaze on this object (it measures it from the jaw); with no Monster Gaze, Auto means Forward. If the monster walks backwards or sideways, pick the right direction here.")]
        public MonsterGaze.FaceDirection faceDirection = MonsterGaze.FaceDirection.Auto;

        [Tooltip("Optional exact eye position (an empty object on the head). Empty = the eyes of the Monster Gaze, or Eye Height above this object.")]
        public Transform eyes;

        [Tooltip("Eye height above this object in metres, used only with no Eyes object and no Monster Gaze.")]
        [Min(0f)] public float eyeHeight = 2.3f;

        MonsterGaze gaze;
        bool gazeLooked;

        MonsterGaze Gaze
        {
            get
            {
                if (!gazeLooked)
                {
                    gaze = GetComponent<MonsterGaze>();
                    gazeLooked = true;
                }
                return gaze;
            }
        }

        /// <summary>The way the model faces, as a flat direction in this object's own space.</summary>
        public Vector3 FacingLocal
        {
            get
            {
                switch (faceDirection)
                {
                    case MonsterGaze.FaceDirection.Forward: return Vector3.forward;
                    case MonsterGaze.FaceDirection.Back: return Vector3.back;
                    case MonsterGaze.FaceDirection.Right: return Vector3.right;
                    case MonsterGaze.FaceDirection.Left: return Vector3.left;
                }
                MonsterGaze g = Gaze;
                return g != null ? g.FacingLocal : Vector3.forward;
            }
        }

        /// <summary>The way the model faces right now, as a flat world direction.</summary>
        public Vector3 FacingWorld
        {
            get
            {
                Vector3 f = transform.TransformDirection(FacingLocal);
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        /// <summary>Where the monster looks from.</summary>
        public Vector3 EyePosition
        {
            get
            {
                if (eyes != null)
                    return eyes.position;
                MonsterGaze g = Gaze;
                if (g != null)
                    return g.EyeWorldPosition;
                return transform.position + Vector3.up * eyeHeight;
            }
        }

        /// <summary>The rotation this object needs for the model to face the given world direction (kept upright).</summary>
        public Quaternion RotationToFace(Vector3 worldHeading)
        {
            worldHeading.y = 0f;
            if (worldHeading.sqrMagnitude < 1e-6f)
                return transform.rotation;
            // Heading = rotation * facingLocal, so rotation = LookRotation(heading) * inverse(LookRotation(facingLocal)).
            Quaternion toHeading = Quaternion.LookRotation(worldHeading.normalized, Vector3.up);
            Quaternion localFrame = Quaternion.LookRotation(FacingLocal, Vector3.up);
            return toHeading * Quaternion.Inverse(localFrame);
        }
    }
}
