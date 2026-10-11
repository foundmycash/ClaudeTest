using System.Collections.Generic;
using UnityEngine;

namespace GorillaNet
{
    /// <summary>
    /// Fits the gorilla model to a <see cref="NetworkAvatar"/>: the head sits where the player's head is, the torso
    /// hangs under it and turns with it, and the arms reach out to the two hands (two-bone IK, the arms stretch a little
    /// when the hands are further away than the arm is long). Nothing here is networked: every peer rebuilds the body
    /// from the three networked poses (head, left hand, right hand) and the finger input NetworkAvatar hands over
    /// (<see cref="SetHandInput"/>), which turns the finger bones: trigger = pointer finger, grip = ring finger, A / X = thumb.
    ///
    /// Lives on the root of PlayerAvatar.prefab, which AvatarModelBuilder builds from Player.fbx.
    /// Runs after NetworkAvatar (execution order), so the poses are final for the frame. It sleeps while the model is
    /// not drawn, which is always the case for the player's own avatar.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class AvatarBodyRig : MonoBehaviour
    {
        // One finger (or the thumb): its joints, how each one starts out and which way each one turns to close.
        internal sealed class Digit
        {
            public Transform[] joints;
            public Quaternion[] rest;
            public Vector3[] axis;     // in each joint's own space; a positive angle turns the finger towards the palm
            public Vector3[] sweepAxis; // the thumb only: the turn that swings it towards the fingertips (zero for the fingers)
            public float curl;         // 0 = open, 1 = closed (eases towards the input)
        }

        [System.Serializable]
        public class Arm
        {
            public Transform upperArm;
            public Transform foreArm;
            public Transform hand;
            [Tooltip("First bone of the index finger, the ring finger and the thumb. They tell the rig where the palm is and which way the fingers and the thumb point.")]
            public Transform pointerRoot;
            public Transform ringRoot;
            public Transform thumbRoot;

            // Rest pose data, read once from the model as it comes out of the prefab.
            internal Quaternion upperRotation, foreRotation, handRotation;
            internal Vector3 upperScale, foreScale, handScale;
            internal float upperLength, foreLength;
            internal Quaternion handFrameInverse;
            internal Vector3 palmLocal;
            internal Digit pointer, ring, thumb;
        }

        [Header("Model (filled in by AvatarModelBuilder)")]
        [Tooltip("The model's skinned mesh. The rig does nothing while it isn't enabled.")]
        public SkinnedMeshRenderer body;
        public Transform headBone;
        [Tooltip("Middle of the head in the head bone's local space. This point is put where the player's head is.")]
        public Vector3 headCenterLocal;
        public Arm leftArm = new Arm();
        public Arm rightArm = new Arm();

        [Header("Targets (taken from the NetworkAvatar above this object when left empty)")]
        public Transform headTarget;
        public Transform leftHandTarget;
        public Transform rightHandTarget;

        [Header("Tuning")]
        [Tooltip("Moves the head in its own axes (right, up, forward in meters), if the model's face should sit a little differently around the camera.")]
        public Vector3 headOffset;

        [Tooltip("How quickly the torso turns to follow where the head looks (seconds, smaller is snappier).")]
        public float bodyTurnSmoothTime = 0.12f;

        [Tooltip("The head can turn this far from the torso before the neck stops (degrees).")]
        public float neckYawLimit = 80f;
        public float neckPitchLimit = 70f;
        public float neckRollLimit = 45f;

        [Tooltip("Where the elbows point, relative to the torso (right = +x for the right arm, mirrored for the left arm). Down and a bit back looks natural.")]
        public Vector3 elbowDirection = new Vector3(0.45f, -1f, -0.6f);

        [Tooltip("The arms stretch up to this many times their length to keep hands where the player's hands are. 1 = never stretch.")]
        [Range(1f, 2f)] public float maxArmStretch = 1.5f;

        [Tooltip("Turns the hands relative to the controllers (euler degrees). Fingers follow the controller's forward direction and the thumb its up direction; adjust this if the hands look twisted in the headset.")]
        public Vector3 handRotationOffset;

        [Header("Fingers")]
        [Tooltip("Turn the finger bones with the controller input: trigger = pointer finger, grip = ring finger, A / X = thumb. Plain bone rotations, no animation.")]
        public bool animateFingers = true;

        [Tooltip("How far each joint of the pointer finger turns (degrees: base joint, middle joint, tip) when the trigger is pulled all the way. Positive curls towards the palm; scaled by how far the trigger is pulled.")]
        public Vector3 pointerCurl = new Vector3(55f, 80f, 55f);

        [Tooltip("The same for the ring finger, turned by the grip.")]
        public Vector3 ringCurl = new Vector3(60f, 85f, 60f);

        [Tooltip("The same for the thumb, turned by the A / X button: folds the thumb towards the palm.")]
        public Vector3 thumbCurl = new Vector3(15f, 35f, 35f);

        [Tooltip("An extra turn of each thumb joint (degrees, same scaling by A / X) in the other direction: swings the thumb forward towards the fingertips, so it can meet the pointer finger. Positive swings towards the fingertips, negative back towards the wrist. 0 = the thumb only folds into the palm.")]
        public Vector3 thumbSweep = new Vector3(28f, 48f, 48f);

        [Tooltip("How long a finger takes to go from open to closed (seconds). Smooths the steps of the network updates too.")]
        public float fingerTravelTime = 0.07f;

        HandInput leftInput;
        HandInput rightInput;

        /// <summary>What the fingers should do; called every frame by <see cref="NetworkAvatar"/> (or by hand, in tests).</summary>
        public void SetHandInput(HandInput left, HandInput right)
        {
            leftInput = left;
            rightInput = right;
        }

        bool initialized;
        bool needsSnap = true;
        float yaw;
        float yawVelocity;

        Quaternion headRestRelative;
        Vector3 headRestLocal;
        Vector3 headCenterOffset;

        void Awake()
        {
            FindTargets();
        }

        void FindTargets()
        {
            if (headTarget != null && leftHandTarget != null && rightHandTarget != null)
                return;
            var avatar = GetComponentInParent<NetworkAvatar>();
            if (avatar == null)
                return;
            if (headTarget == null) headTarget = avatar.head;
            if (leftHandTarget == null) leftHandTarget = avatar.leftHand;
            if (rightHandTarget == null) rightHandTarget = avatar.rightHand;
        }

        [Header("Level of detail")]
        [Tooltip("Players further than this (meters) get their body fitted every 2nd frame, beyond the next distance every 4th. The fit is two arm solves, a handful of transform writes: cheap, but every remote player pays it every frame on the Quest 2's slow CPU.")]
        public float halfRateDistance = 20f;
        public float quarterRateDistance = 40f;

        static Camera viewCamera;
        static int viewCameraCheckedFrame = -1000;
        float skippedTime;
        int skippedFrames;

        void LateUpdate()
        {
            if (body == null || !body.enabled)
            {
                needsSnap = true; // pose from scratch when the model is drawn again
                return;
            }

            skippedTime += Time.deltaTime;

            // (There used to be a skip here for bodies the camera does not see, going by Renderer.isVisible. It saved next to nothing and
            // a body that stopped being fitted could also stop being culled-in correctly, so remote players could vanish: removed.)

            // Far away: every 2nd or 4th frame is enough.
            if (headTarget != null)
            {
                Transform view = ViewPoint();
                if (view != null)
                {
                    float distanceSqr = (headTarget.position - view.position).sqrMagnitude;
                    int interval = distanceSqr > quarterRateDistance * quarterRateDistance ? 4 : distanceSqr > halfRateDistance * halfRateDistance ? 2 : 1;
                    if (interval > 1 && skippedFrames < interval - 1)
                    {
                        skippedFrames++;
                        return;
                    }
                }
            }

            // After a long time out of view the torso would swing round from where it was: put it straight on the head.
            if (skippedTime > 1f)
                needsSnap = true;

            Solve();
            skippedFrames = 0;
            skippedTime = 0f;
        }

        static Transform ViewPoint()
        {
            if ((viewCamera == null || !viewCamera.isActiveAndEnabled) && Time.frameCount - viewCameraCheckedFrame > 60)
            {
                viewCamera = Camera.main;
                viewCameraCheckedFrame = Time.frameCount;
            }
            return viewCamera != null ? viewCamera.transform : null;
        }

        /// <summary>Fits the body to the targets right now. Called every frame; call it yourself to pose the model in the editor.</summary>
        /// <param name="snapTorso">Turn the torso to face the head immediately instead of smoothing.</param>
        public void Solve(bool snapTorso = false)
        {
            FindTargets();
            if (headBone == null || headTarget == null || leftHandTarget == null || rightHandTarget == null)
                return;
            if (!initialized)
                Initialize();

            Quaternion headRotation = headTarget.rotation;
            bool snapFingers = snapTorso || needsSnap;      // a body that was not drawn comes back with its fingers where they belong

            // Torso: upright, turned the way the head looks.
            float targetYaw = HeadYaw(headRotation);
            if (snapTorso || needsSnap)
            {
                yaw = targetYaw;
                yawVelocity = 0f;
                needsSnap = false;
            }
            else
            {
                // (the time since the last fit, not just this frame's: the body is not fitted every frame when it is far away)
                yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVelocity, bodyTurnSmoothTime, Mathf.Infinity, Mathf.Max(skippedTime, Time.deltaTime));
            }
            Quaternion bodyRotation = Quaternion.Euler(0f, yaw, 0f);
            transform.rotation = bodyRotation;

            // Head: the bone turns with the player's head (within what a neck can do).
            Quaternion relative = Quaternion.Inverse(bodyRotation) * headRotation;
            Vector3 euler = relative.eulerAngles;
            float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -neckPitchLimit, neckPitchLimit);
            float turn = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.y), -neckYawLimit, neckYawLimit);
            float roll = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.z), -neckRollLimit, neckRollLimit);
            Quaternion headBoneRotation = bodyRotation * Quaternion.Euler(pitch, turn, roll) * headRestRelative;

            // Put the middle of the head where the player's head is, and the rest of the body follows from there.
            Vector3 headBonePosition = headTarget.position - headBoneRotation * (headCenterOffset + headOffset);
            transform.position = headBonePosition - bodyRotation * Vector3.Scale(headRestLocal, transform.lossyScale);
            headBone.rotation = headBoneRotation;

            Quaternion handOffset = Quaternion.Euler(handRotationOffset);
            SolveArm(leftArm, leftHandTarget, -1f, bodyRotation, handOffset);
            SolveArm(rightArm, rightHandTarget, 1f, bodyRotation, handOffset);

            if (animateFingers)
            {
                float dt = Mathf.Max(skippedTime, Time.deltaTime);
                ApplyFingers(leftArm, leftInput, dt, snapFingers);
                ApplyFingers(rightArm, rightInput, dt, snapFingers);
            }
        }

        // ---- fingers ----

        void ApplyFingers(Arm arm, HandInput input, float dt, bool snap)
        {
            float step = snap ? 1f : dt / Mathf.Max(fingerTravelTime, 1e-3f);
            MoveDigit(arm.pointer, pointerCurl, Vector3.zero, input.trigger, step);
            MoveDigit(arm.ring, ringCurl, Vector3.zero, input.grip, step);
            MoveDigit(arm.thumb, thumbCurl, thumbSweep, input.thumb, step);
        }

        // Eases the digit towards `target` (0-1) and turns each joint by its share of `degrees` from its rest rotation
        // (plus its share of `sweepDegrees` about the sweep axis, for the thumb).
        static void MoveDigit(Digit digit, Vector3 degrees, Vector3 sweepDegrees, float target, float step)
        {
            if (digit == null)
                return;
            digit.curl = Mathf.MoveTowards(digit.curl, Mathf.Clamp01(target), step);
            for (int i = 0; i < digit.joints.Length; i++)
            {
                float angle = (i == 0 ? degrees.x : i == 1 ? degrees.y : degrees.z) * digit.curl;
                Quaternion rotation = digit.rest[i] * Quaternion.AngleAxis(angle, digit.axis[i]);
                float sweep = (i == 0 ? sweepDegrees.x : i == 1 ? sweepDegrees.y : sweepDegrees.z) * digit.curl;
                if (sweep != 0f && digit.sweepAxis[i] != Vector3.zero)
                    rotation *= Quaternion.AngleAxis(sweep, digit.sweepAxis[i]);
                digit.joints[i].localRotation = rotation;
            }
        }

        // The joints of a finger, from its first bone down (a helper bone at the very tip is only used for the direction).
        // A joint closes by turning its finger towards the palm: about the axis perpendicular to both the bone and the palm's normal.
        // sweepAxisWorld (the thumb only): the axis that swings the digit from the thumb's side towards the fingertips; zero for none.
        static Digit BuildDigit(Transform root, Vector3 palmNormal, Vector3 fallbackAxis, Vector3 fallbackDirection, Vector3 sweepAxisWorld)
        {
            var chain = new List<Transform>(4);
            for (Transform t = root; t != null && chain.Count < 4; t = t.childCount > 0 ? t.GetChild(0) : null)
                chain.Add(t);

            int count = Mathf.Min(3, chain.Count);
            var digit = new Digit { joints = new Transform[count], rest = new Quaternion[count], axis = new Vector3[count], sweepAxis = new Vector3[count] };
            Vector3 direction = fallbackDirection;
            for (int i = 0; i < count; i++)
            {
                if (i + 1 < chain.Count && (chain[i + 1].position - chain[i].position).sqrMagnitude > 1e-10f)
                    direction = (chain[i + 1].position - chain[i].position).normalized;
                Vector3 axis = Vector3.Cross(direction, palmNormal);
                axis = axis.sqrMagnitude > 0.04f ? axis.normalized : fallbackAxis;
                digit.joints[i] = chain[i];
                digit.rest[i] = chain[i].localRotation;
                digit.axis[i] = Quaternion.Inverse(chain[i].rotation) * axis;
                digit.sweepAxis[i] = sweepAxisWorld == Vector3.zero ? Vector3.zero : Quaternion.Inverse(chain[i].rotation) * sweepAxisWorld.normalized;
            }
            return digit;
        }

        /// <summary>
        /// How far the head and the palms are from where the player's head and hands are (meters). Zero when the body
        /// fits; the hands miss by a little when they are further away than the arms can stretch.
        /// </summary>
        public void GetFitError(out float head, out float leftPalm, out float rightPalm)
        {
            head = leftPalm = rightPalm = float.NaN;
            FindTargets();
            if (!initialized || headBone == null || headTarget == null)
                return;
            head = Vector3.Distance(headBone.position + headBone.rotation * (headCenterOffset + headOffset), headTarget.position);
            leftPalm = Vector3.Distance(PalmPosition(leftArm), leftHandTarget.position);
            rightPalm = Vector3.Distance(PalmPosition(rightArm), rightHandTarget.position);
        }

        static Vector3 PalmPosition(Arm arm) => arm.hand.position + arm.hand.rotation * arm.palmLocal;

        void Initialize()
        {
            initialized = true;

            headRestRelative = Quaternion.Inverse(transform.rotation) * headBone.rotation;
            headRestLocal = transform.InverseTransformPoint(headBone.position);
            headCenterOffset = Quaternion.Inverse(headBone.rotation) * (headBone.TransformPoint(headCenterLocal) - headBone.position);

            InitializeArm(leftArm, -1f);
            InitializeArm(rightArm, 1f);
        }

        static void InitializeArm(Arm arm, float side)
        {
            arm.upperRotation = arm.upperArm.localRotation;
            arm.foreRotation = arm.foreArm.localRotation;
            arm.handRotation = arm.hand.localRotation;
            arm.upperScale = arm.upperArm.localScale;
            arm.foreScale = arm.foreArm.localScale;
            arm.handScale = arm.hand.localScale;
            arm.upperLength = Vector3.Distance(arm.upperArm.position, arm.foreArm.position);
            arm.foreLength = Vector3.Distance(arm.foreArm.position, arm.hand.position);

            // Which way the fingers and the thumb point, in the hand bone's own axes: the hand follows the controller
            // so that the fingers go where the controller points and the thumb goes up.
            Vector3 wrist = arm.hand.position;
            Vector3 knuckles = (arm.pointerRoot.position + arm.ringRoot.position) * 0.5f;
            Quaternion inverseHand = Quaternion.Inverse(arm.hand.rotation);
            Vector3 fingers = inverseHand * (knuckles - wrist);
            Vector3 thumb = inverseHand * (arm.thumbRoot.position - knuckles);
            arm.handFrameInverse = Quaternion.Inverse(Quaternion.LookRotation(fingers.normalized, thumb));
            arm.palmLocal = 0.55f * fingers; // the middle of the palm, between the wrist and the knuckles

            // Which way the fingers close. With the fingers pointing forward and the thumb up, a left palm faces right and a
            // right palm faces left (towards the other hand), and the fingers curl into it.
            Vector3 fingerDirection = (knuckles - wrist).normalized;
            Vector3 up = Vector3.ProjectOnPlane(arm.thumbRoot.position - knuckles, fingerDirection).normalized;
            Vector3 right = Vector3.Cross(up, fingerDirection);
            Vector3 palmNormal = side < 0f ? right : -right;
            Vector3 fallbackAxis = Vector3.Cross(fingerDirection, palmNormal).normalized;
            arm.pointer = BuildDigit(arm.pointerRoot, palmNormal, fallbackAxis, fingerDirection, Vector3.zero);
            arm.ring = BuildDigit(arm.ringRoot, palmNormal, fallbackAxis, fingerDirection, Vector3.zero);
            // The thumb also sweeps in the hand's own plane: about the hand's "right" axis (thumb side up, fingers forward), the same
            // for both hands, a positive angle takes the thumb from pointing up round towards the fingertips.
            arm.thumb = BuildDigit(arm.thumbRoot, palmNormal, fallbackAxis, fingerDirection, right);
        }

        void SolveArm(Arm arm, Transform target, float side, Quaternion bodyRotation, Quaternion handOffset)
        {
            // Start from the rest pose, so nothing builds up from frame to frame.
            arm.upperArm.localRotation = arm.upperRotation;
            arm.foreArm.localRotation = arm.foreRotation;
            arm.hand.localRotation = arm.handRotation;
            arm.upperArm.localScale = arm.upperScale;
            arm.foreArm.localScale = arm.foreScale;
            arm.hand.localScale = arm.handScale;

            Quaternion handRotation = target.rotation * handOffset * arm.handFrameInverse;
            Vector3 wristTarget = target.position - handRotation * arm.palmLocal;

            Vector3 shoulder = arm.upperArm.position;
            Vector3 toWrist = wristTarget - shoulder;
            float distance = toWrist.magnitude;
            Vector3 direction = distance > 1e-5f ? toWrist / distance : bodyRotation * Vector3.down;

            // Stretch the arm when the hand is out of reach.
            float reach = arm.upperLength + arm.foreLength;
            float stretch = distance > reach ? Mathf.Min(distance / reach, maxArmStretch) : 1f;
            float upper = arm.upperLength * stretch;
            float fore = arm.foreLength * stretch;
            float d = Mathf.Clamp(distance, Mathf.Abs(upper - fore) + 1e-3f, upper + fore - 1e-3f);

            // Elbow: on the circle of points that are `upper` from the shoulder and `fore` from the wrist, on the side the pole points to.
            float along = (upper * upper - fore * fore + d * d) / (2f * d);
            float height = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
            Vector3 pole = bodyRotation * new Vector3(side * elbowDirection.x, elbowDirection.y, elbowDirection.z);
            pole = Vector3.ProjectOnPlane(pole, direction);
            if (pole.sqrMagnitude < 1e-4f)
                pole = Vector3.ProjectOnPlane(bodyRotation * Vector3.down, direction);
            if (pole.sqrMagnitude < 1e-4f)
                pole = Vector3.ProjectOnPlane(bodyRotation * Vector3.back, direction);
            pole.Normalize();
            Vector3 elbow = shoulder + direction * along + pole * height;
            Vector3 wrist = shoulder + direction * d;

            // Upper arm: point it at the elbow (and stretch it along its length).
            arm.upperArm.localScale = new Vector3(arm.upperScale.x, arm.upperScale.y * stretch, arm.upperScale.z);
            arm.upperArm.rotation = Quaternion.FromToRotation(arm.foreArm.position - shoulder, elbow - shoulder) * arm.upperArm.rotation;

            // Forearm: point it at the wrist, then stretch it to fit exactly.
            arm.foreArm.rotation = Quaternion.FromToRotation(arm.hand.position - arm.foreArm.position, wrist - elbow) * arm.foreArm.rotation;
            float length = Vector3.Distance(arm.foreArm.position, arm.hand.position);
            float foreStretch = length > 1e-6f ? Vector3.Distance(elbow, wrist) / length : 1f;
            arm.foreArm.localScale = new Vector3(arm.foreScale.x, arm.foreScale.y * foreStretch, arm.foreScale.z);

            // Hand: keeps its size however far the forearm stretched, and turns with the controller.
            arm.hand.localScale = new Vector3(arm.handScale.x, arm.handScale.y / Mathf.Max(foreStretch, 1e-3f), arm.handScale.z);
            arm.hand.rotation = handRotation;
        }

        // Which way the head looks on the ground plane, steady even when looking almost straight up or down.
        float HeadYaw(Quaternion headRotation)
        {
            Vector3 forward = headRotation * Vector3.forward;
            Vector3 flat = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude < 0.2f)
            {
                Vector3 up = headRotation * Vector3.up;
                Vector3 alternative = forward.y < 0f ? new Vector3(up.x, 0f, up.z) : new Vector3(-up.x, 0f, -up.z);
                if (alternative.sqrMagnitude > 1e-4f)
                    flat = alternative;
                else if (flat.sqrMagnitude < 1e-6f)
                    return yaw;
            }
            return Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }
    }
}
