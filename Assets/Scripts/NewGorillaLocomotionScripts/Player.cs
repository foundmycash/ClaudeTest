namespace GorillaLocomotion
{
    using UnityEngine;

    public class Player : MonoBehaviour
    {
        private static Player _instance;

        public static Player Instance { get { return _instance; } }

        public SphereCollider headCollider;
        public CapsuleCollider bodyCollider;

        public Transform leftHandFollower;
        public Transform rightHandFollower;

        public Transform rightHandTransform;
        public Transform leftHandTransform;

        private Vector3 lastLeftHandPosition;
        private Vector3 lastRightHandPosition;
        private Vector3 lastHeadPosition;

        private Rigidbody playerRigidBody;

        public int velocityHistorySize;
        public float maxArmLength = 1.5f;
        public float unStickDistance = 1f;

        public float velocityLimit;
        public float maxJumpSpeed;
        public float jumpMultiplier;
        public float minimumRaycastDistance = 0.05f;
        public float defaultSlideFactor = 0.03f;
        public float defaultPrecision = 0.995f;

        private Vector3[] velocityHistory;
        private int velocityIndex;
        private Vector3 currentVelocity;
        private Vector3 denormalizedVelocityAverage;
        private bool jumpHandIsLeft;
        private Vector3 lastPosition;

        public Vector3 rightHandOffset;
        public Vector3 leftHandOffset;

        public LayerMask locomotionEnabledLayers;

        public bool wasLeftHandTouching;
        public bool wasRightHandTouching;

        public bool disableMovement = false;

        [Tooltip("Adds a HandContactFeedback (haptics + impact sound) to this object on startup if it doesn't have one. Turn off if you place and tune the component yourself and want none by default.")]
        public bool autoAddContactFeedback = true;

        [Header("Size")]
        [Tooltip("How big you are compared to the world. 1 = the rig as it is built; 0.9 = 10% smaller (the world, your reach and your head height all feel 10% bigger). Applied once at startup to the XR rig this object sits in, together with Max Arm Length and Un Stick Distance, and the gorilla body models follow it (see BodyScale). Not applied to a kinematic rig (the loading scene).")]
        [Range(0.5f, 1.5f)] public float bodyScale = 0.9f;

        /// <summary>The size factor applied to the player's rig (1 when none is applied). The gorilla avatar models use it so bodies match the rig.</summary>
        public static float BodyScale { get; private set; } = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            BodyScale = 1f;
        }

        [Header("Locomotion feel")]
        [Tooltip("Smooths the speed you let go with. It follows your hands quickly (the original averaged 8 frames, so letting go felt late), faint pushes fade in instead of being cut off, the top speed is approached gradually instead of hitting a wall, speed you are already carrying helps your next push, and the launch eases up to speed over a few frames. Off = the original behaviour (8 frame average, hard thresholds, instant launch).")]
        public bool smoothLaunch = true;

        [Tooltip("Hands the rig from your hands to physics without a hitch. With it off, Unity's Rigidbody interpolation fights the hand movement and the rig stalls or jerks back for a frame or two every time you let go. Turn off to get the original behaviour back.")]
        public bool seamlessRelease = true;

        [Tooltip("Smooth Launch: how long (seconds) your hand speed is averaged over to get the speed you let go with. Smaller = snappier but twitchier, larger = smoother but later. The original was 8 frames, about 0.11 s at 72 Hz.")]
        [Range(0.01f, 0.15f)] public float launchResponseTime = 0.05f;

        [Tooltip("Smooth Launch: the fraction of Max Jump Speed where the speed starts easing into the cap. Below it the speed is untouched. 1 = hard cap like the original.")]
        [Range(0.3f, 1f)] public float softCapStart = 0.7f;

        [Tooltip("Smooth Launch: seconds the launch takes to ease from the speed you were pushing at up to the full launch speed (Jump Multiplier and momentum). 0 = instant like the original.")]
        [Range(0f, 0.15f)] public float launchRampTime = 0.08f;

        [Tooltip("Smooth Launch: how much of the speed you were already carrying is added to your next push when it points the same way (it only ever adds, it never slows a push down). Pushes in a row build speed gradually up to the cap. 0 = every push starts from scratch like the original.")]
        [Range(0f, 0.8f)] public float momentumBuildUp = 0.35f;

        [Tooltip("Smooth Launch: seconds for carried speed to halve while a hand is holding a surface.")]
        [Range(0.1f, 2f)] public float momentumHalfLife = 0.4f;

        /// <summary>Raised on the frame a hand goes from free to touching a surface (see <see cref="HandContactInfo"/>).</summary>
        public event System.Action<HandContactInfo> HandContact;

        private RaycastHit lastCastHit;
        private float leftHandAirTime;
        private float rightHandAirTime;

        // Smooth launch state
        private const float MomentumFullPushSpeed = 2f;   // a push this fast (m/s) gets the whole momentum bonus, a lighter one a share of it
        private const float TeleportSpeed = 40f;          // a frame this fast was a teleport, not movement
        private const float BodyVelocityResponse = 0.02f; // seconds, how fast bodyVelocity follows the rig
        private Vector3 launchEstimate;                   // the body velocity your hands are producing, averaged over launchResponseTime
        private Vector3 bodyVelocity;                     // the same, barely averaged: how fast the rig is moving right now
        private Vector3 momentum;                         // horizontal speed carried over from the last flight
        private Vector3 pendingLaunch;                    // the velocity to let go with if the hands let go now
        private Vector3 pendingDelta;                     // what the launch still adds on top of that, eased in after letting go
        private Vector3 rampDelta;
        private float rampProgress = 1f;
        private bool wasGripping;

        // Seamless release state
        private RigidbodyInterpolation interpolationMode;
        private bool interpolationHeld;
        private bool handingOver;
        private Vector3 bridgeVelocity;

        // a kinematic rig (the loading scene) has no physics flight to hand over to, and no interpolation fighting the hands
        private bool SeamlessRelease { get { return seamlessRelease && !playerRigidBody.isKinematic; } }
        private float lastUpdateTime;                     // Time.time of the last Update, so FixedUpdate knows how far ahead of it the physics step is

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
            }
            else
            {
                _instance = this;
                if (autoAddContactFeedback && GetComponent<HandContactFeedback>() == null)
                {
                    gameObject.AddComponent<HandContactFeedback>();
                }
                ApplyBodyScale();
            }
            InitializeValues();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                BodyScale = 1f;
            }
        }

        // Scales the XR rig this object is part of (head, hands, followers, colliders all hang under it) once, before the first
        // positions are remembered. The arm limits are lengths in the world, so they shrink with it.
        private void ApplyBodyScale()
        {
            Rigidbody body = GetComponent<Rigidbody>();
            Transform rig = transform.parent;
            if (rig == null || (body != null && body.isKinematic) || Mathf.Approximately(bodyScale, 1f))
            {
                BodyScale = 1f;
                return;
            }

            rig.localScale *= bodyScale;
            maxArmLength *= bodyScale;
            unStickDistance *= bodyScale;
            BodyScale = bodyScale;
        }

        public void InitializeValues()
        {
            playerRigidBody = GetComponent<Rigidbody>();
            velocityHistory = new Vector3[velocityHistorySize];
            lastLeftHandPosition = leftHandFollower.transform.position;
            lastRightHandPosition = rightHandFollower.transform.position;
            lastHeadPosition = headCollider.transform.position;
            velocityIndex = 0;
            lastPosition = transform.position;

            if (interpolationHeld)
            {
                playerRigidBody.interpolation = interpolationMode;
            }
            interpolationMode = playerRigidBody.interpolation;
            interpolationHeld = false;
            handingOver = false;
            launchEstimate = Vector3.zero;
            bodyVelocity = Vector3.zero;
            momentum = Vector3.zero;
            pendingLaunch = Vector3.zero;
            pendingDelta = Vector3.zero;
            rampDelta = Vector3.zero;
            rampProgress = 1f;
            wasGripping = false;
        }

        // The rig's interpolation is switched off while a hand holds on and until the physics body takes over again, see UpdateLaunchState.
        private void OnDisable()
        {
            if (interpolationHeld && playerRigidBody != null)
            {
                playerRigidBody.interpolation = interpolationMode;
            }
            interpolationHeld = false;
            handingOver = false;
        }

        private Vector3 CurrentLeftHandPosition()
        {
            if ((PositionWithOffset(leftHandTransform, leftHandOffset) - headCollider.transform.position).magnitude < maxArmLength)
            {
                return PositionWithOffset(leftHandTransform, leftHandOffset);
            }
            else
            {
                return headCollider.transform.position + (PositionWithOffset(leftHandTransform, leftHandOffset) - headCollider.transform.position).normalized * maxArmLength;
            }
        }

        private Vector3 CurrentRightHandPosition()
        {
            if ((PositionWithOffset(rightHandTransform, rightHandOffset) - headCollider.transform.position).magnitude < maxArmLength)
            {
                return PositionWithOffset(rightHandTransform, rightHandOffset);
            }
            else
            {
                return headCollider.transform.position + (PositionWithOffset(rightHandTransform, rightHandOffset) - headCollider.transform.position).normalized * maxArmLength;
            }
        }

        private Vector3 PositionWithOffset(Transform transformToModify, Vector3 offsetVector)
        {
            return transformToModify.position + transformToModify.rotation * offsetVector;
        }

        private void Update()
        {
            bool leftHandColliding = false;
            bool rightHandColliding = false;
            Vector3 finalPosition;
            Vector3 rigidBodyMovement = Vector3.zero;
            Vector3 firstIterationLeftHand = Vector3.zero;
            Vector3 firstIterationRightHand = Vector3.zero;
            RaycastHit hitInfo;
            bool leftHasHit = false;
            bool rightHasHit = false;
            RaycastHit leftHit = default;
            RaycastHit rightHit = default;

            bodyCollider.transform.eulerAngles = new Vector3(0, headCollider.transform.eulerAngles.y, 0);

            //left hand

            Vector3 leftHandMotion = CurrentLeftHandPosition() - lastLeftHandPosition;
            Vector3 distanceTraveled = leftHandMotion + Vector3.down * 2f * 9.8f * Time.deltaTime * Time.deltaTime;

            if (IterativeCollisionSphereCast(lastLeftHandPosition, minimumRaycastDistance, distanceTraveled, defaultPrecision, out finalPosition, true))
            {
                leftHit = lastCastHit;
                leftHasHit = true;
                //this lets you stick to the position you touch, as long as you keep touching the surface this will be the zero point for that hand
                if (wasLeftHandTouching)
                {
                    firstIterationLeftHand = lastLeftHandPosition - CurrentLeftHandPosition();
                }
                else
                {
                    firstIterationLeftHand = finalPosition - CurrentLeftHandPosition();
                }
                playerRigidBody.linearVelocity = Vector3.zero;

                leftHandColliding = true;
            }

            //right hand

            Vector3 rightHandMotion = CurrentRightHandPosition() - lastRightHandPosition;
            distanceTraveled = rightHandMotion + Vector3.down * 2f * 9.8f * Time.deltaTime * Time.deltaTime;

            if (IterativeCollisionSphereCast(lastRightHandPosition, minimumRaycastDistance, distanceTraveled, defaultPrecision, out finalPosition, true))
            {
                rightHit = lastCastHit;
                rightHasHit = true;

                if (wasRightHandTouching)
                {
                    firstIterationRightHand = lastRightHandPosition - CurrentRightHandPosition();
                }
                else
                {
                    firstIterationRightHand = finalPosition - CurrentRightHandPosition();
                }

                playerRigidBody.linearVelocity = Vector3.zero;

                rightHandColliding = true;
            }

            //average or add

            if ((leftHandColliding || wasLeftHandTouching) && (rightHandColliding || wasRightHandTouching))
            {
                //this lets you grab stuff with both hands at the same time
                rigidBodyMovement = (firstIterationLeftHand + firstIterationRightHand) / 2;
            }
            else
            {
                rigidBodyMovement = firstIterationLeftHand + firstIterationRightHand;
            }

            //check valid head movement

            if (IterativeCollisionSphereCast(lastHeadPosition, headCollider.radius, headCollider.transform.position + rigidBodyMovement - lastHeadPosition, defaultPrecision, out finalPosition, false))
            {
                rigidBodyMovement = finalPosition - lastHeadPosition;
                //last check to make sure the head won't phase through geometry
                if (Physics.Raycast(lastHeadPosition, headCollider.transform.position - lastHeadPosition + rigidBodyMovement, out hitInfo, (headCollider.transform.position - lastHeadPosition + rigidBodyMovement).magnitude + headCollider.radius * defaultPrecision * 0.999f, locomotionEnabledLayers.value))
                {
                    rigidBodyMovement = lastHeadPosition - headCollider.transform.position;
                }
            }

            if (rigidBodyMovement != Vector3.zero)
            {
                transform.position = transform.position + rigidBodyMovement;
            }

            lastHeadPosition = headCollider.transform.position;

            //do final left hand position

            distanceTraveled = CurrentLeftHandPosition() - lastLeftHandPosition;

            if (IterativeCollisionSphereCast(lastLeftHandPosition, minimumRaycastDistance, distanceTraveled, defaultPrecision, out finalPosition, !((leftHandColliding || wasLeftHandTouching) && (rightHandColliding || wasRightHandTouching))))
            {
                lastLeftHandPosition = finalPosition;
                leftHandColliding = true;
                if (!leftHasHit)
                {
                    leftHit = lastCastHit;
                    leftHasHit = true;
                }
            }
            else
            {
                lastLeftHandPosition = CurrentLeftHandPosition();
            }

            //do final right hand position

            distanceTraveled = CurrentRightHandPosition() - lastRightHandPosition;

            if (IterativeCollisionSphereCast(lastRightHandPosition, minimumRaycastDistance, distanceTraveled, defaultPrecision, out finalPosition, !((leftHandColliding || wasLeftHandTouching) && (rightHandColliding || wasRightHandTouching))))
            {
                lastRightHandPosition = finalPosition;
                rightHandColliding = true;
                if (!rightHasHit)
                {
                    rightHit = lastCastHit;
                    rightHasHit = true;
                }
            }
            else
            {
                lastRightHandPosition = CurrentRightHandPosition();
            }

            StoreVelocities();

            if (rightHandColliding || leftHandColliding)
            {
                // the velocity to let go with if the hands let go now: the last frame a hand held on is the one that counts, so these are only
                // reset while holding (UpdateLaunchState gives them to the physics body once the hands let go)
                pendingLaunch = Vector3.zero;
                pendingDelta = Vector3.zero;

                if (!disableMovement)
                {
                    bool launching = false;
                    if (smoothLaunch)
                    {
                        launching = ComputeSmoothLaunch();
                    }
                    else if (denormalizedVelocityAverage.magnitude > velocityLimit)
                    {
                        if (denormalizedVelocityAverage.magnitude * jumpMultiplier > maxJumpSpeed)
                        {
                            pendingLaunch = denormalizedVelocityAverage.normalized * maxJumpSpeed;
                        }
                        else
                        {
                            pendingLaunch = jumpMultiplier * denormalizedVelocityAverage;
                        }
                        launching = true;
                    }

                    if (launching && !SeamlessRelease)
                    {
                        playerRigidBody.linearVelocity = pendingLaunch;
                    }
                }
            }

            //check to see if left hand is stuck and we should unstick it

            if (leftHandColliding && (CurrentLeftHandPosition() - lastLeftHandPosition).magnitude > unStickDistance && !Physics.SphereCast(headCollider.transform.position, minimumRaycastDistance * defaultPrecision, CurrentLeftHandPosition() - headCollider.transform.position, out hitInfo, (CurrentLeftHandPosition() - headCollider.transform.position).magnitude - minimumRaycastDistance, locomotionEnabledLayers.value))
            {
                lastLeftHandPosition = CurrentLeftHandPosition();
                leftHandColliding = false;
            }

            //check to see if right hand is stuck and we should unstick it

            if (rightHandColliding && (CurrentRightHandPosition() - lastRightHandPosition).magnitude > unStickDistance && !Physics.SphereCast(headCollider.transform.position, minimumRaycastDistance * defaultPrecision, CurrentRightHandPosition() - headCollider.transform.position, out hitInfo, (CurrentRightHandPosition() - headCollider.transform.position).magnitude - minimumRaycastDistance, locomotionEnabledLayers.value))
            {
                lastRightHandPosition = CurrentRightHandPosition();
                rightHandColliding = false;
            }

            leftHandFollower.position = lastLeftHandPosition;
            rightHandFollower.position = lastRightHandPosition;

            ReportContact(true, leftHandColliding, wasLeftHandTouching, ref leftHandAirTime, leftHandMotion, leftHasHit, leftHit, lastLeftHandPosition);
            ReportContact(false, rightHandColliding, wasRightHandTouching, ref rightHandAirTime, rightHandMotion, rightHasHit, rightHit, lastRightHandPosition);

            wasLeftHandTouching = leftHandColliding;
            wasRightHandTouching = rightHandColliding;

            UpdateLaunchState(leftHandColliding || rightHandColliding);
        }

        // Smooth launch: works out pendingLaunch (the speed you were pushing at) and pendingDelta (what the launch adds on top, eased in after
        // letting go) from the smoothed hand velocity. Returns false if the push is too faint to launch at all.
        private bool ComputeSmoothLaunch()
        {
            Vector3 push = launchEstimate;
            float pushSpeed = push.magnitude;

            // a faint push fades in over a band around velocityLimit instead of switching on at it
            float weight = 1f;
            if (velocityLimit > 0f)
            {
                weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f * velocityLimit, 1.5f * velocityLimit, pushSpeed));
            }
            if (weight <= 0f)
            {
                return false;
            }

            Vector3 target = push * jumpMultiplier;

            // speed you were already carrying helps a push that points the same way, so pushes in a row build up gradually
            if (momentumBuildUp > 0f)
            {
                Vector3 flat = new Vector3(target.x, 0f, target.z);
                float flatSpeed = flat.magnitude;
                if (flatSpeed > 0.05f)
                {
                    Vector3 direction = flat / flatSpeed;
                    float along = Mathf.Max(0f, Vector3.Dot(momentum, direction));
                    target += direction * (momentumBuildUp * along * Mathf.Clamp01(pushSpeed / MomentumFullPushSpeed));
                }
            }

            // ease into the top speed instead of hitting it: untouched up to the knee, then a smooth curve that approaches maxJumpSpeed
            if (maxJumpSpeed > 0f)
            {
                float speed = target.magnitude;
                float knee = maxJumpSpeed * softCapStart;
                if (speed > knee)
                {
                    float room = maxJumpSpeed - knee;
                    float eased = room > 0.0001f ? knee + room * (float)System.Math.Tanh((speed - knee) / room) : maxJumpSpeed;
                    target *= eased / speed;
                }
            }
            target *= weight;

            if (launchRampTime > 0f)
            {
                // the launch eases up from how fast the rig is moving right now, so letting go never drops the speed first
                Vector3 start = bodyVelocity * weight;
                if (maxJumpSpeed > 0f)
                {
                    start = Vector3.ClampMagnitude(start, maxJumpSpeed);
                }
                pendingLaunch = start;
                pendingDelta = target - start;
            }
            else
            {
                pendingLaunch = target;
            }
            return true;
        }

        // Runs at the end of every Update with the final grip state. Carries speed from the air into the next push, eases the launch up to speed
        // after letting go, and (seamlessRelease) hands the rig between the hands and the physics body.
        //
        // Why the hand-over matters: the hands move the rig by writing its transform, the flight is done by the interpolated Rigidbody. Unity's
        // interpolation keeps pulling the transform back to its last physics poses, which the hand movement hides because it corrects the
        // position every frame. The moment you let go nothing corrects it any more, so the rig stalls or jerks backwards for a frame or two
        // (up to a whole physics step) every release. So: no interpolation while a hand holds, the first frames after letting go are moved here
        // with the same velocity and gravity, and the first physics step after that (FixedUpdate) takes the rig over with interpolation back on.
        private void UpdateLaunchState(bool gripping)
        {
            float dt = Time.deltaTime;

            if (smoothLaunch)
            {
                if (gripping)
                {
                    momentum *= Mathf.Pow(0.5f, dt / Mathf.Max(momentumHalfLife, 0.01f));
                }
                else
                {
                    momentum = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(launchEstimate, Vector3.up), maxJumpSpeed > 0f ? maxJumpSpeed : float.MaxValue);
                }
            }

            if (gripping)
            {
                rampProgress = 1f;
                handingOver = false;
                if (SeamlessRelease)
                {
                    playerRigidBody.interpolation = RigidbodyInterpolation.None;
                    interpolationHeld = true;
                    playerRigidBody.linearVelocity = Vector3.zero;
                }
            }
            else if (wasGripping)
            {
                // just let go
                rampDelta = pendingDelta;
                rampProgress = pendingDelta.sqrMagnitude > 0f ? 0f : 1f;
                if (SeamlessRelease)
                {
                    handingOver = true;
                    bridgeVelocity = pendingLaunch;
                }
            }

            if (!gripping)
            {
                if (rampProgress < 1f)
                {
                    float next = launchRampTime > 0f ? Mathf.Min(1f, rampProgress + dt / launchRampTime) : 1f;
                    Vector3 add = rampDelta * (Mathf.SmoothStep(0f, 1f, next) - Mathf.SmoothStep(0f, 1f, rampProgress));
                    rampProgress = next;
                    if (handingOver)
                    {
                        bridgeVelocity += add;
                    }
                    else
                    {
                        playerRigidBody.linearVelocity += add;
                    }
                }

                if (handingOver)
                {
                    transform.position += bridgeVelocity * dt;
                    if (playerRigidBody.useGravity)
                    {
                        bridgeVelocity += Physics.gravity * dt;
                    }
                }
            }

            wasGripping = gripping;
            lastUpdateTime = Time.time;
        }

        // The first physics step after letting go: the physics body takes the rig over from UpdateLaunchState's bridging.
        private void FixedUpdate()
        {
            if (!handingOver || playerRigidBody == null)
            {
                return;
            }

            // This step runs a little after the last frame's Update; carry the rig forward by that much so the physics body picks up exactly where
            // the bridging left off (without it the first interpolated frame covers only a fraction of the distance and the rig hesitates).
            float ahead = Mathf.Max(0f, Time.fixedTime - lastUpdateTime);
            Vector3 gravity = playerRigidBody.useGravity ? Physics.gravity : Vector3.zero;

            // writing the pose is what makes Unity start the interpolation again from exactly here instead of from its old physics poses
            transform.position += bridgeVelocity * ahead + 0.5f * gravity * ahead * ahead;
            bridgeVelocity += gravity * ahead;
            Physics.SyncTransforms();
            playerRigidBody.interpolation = interpolationMode;
            interpolationHeld = false;
            playerRigidBody.linearVelocity = bridgeVelocity;
            handingOver = false;
        }

        private bool IterativeCollisionSphereCast(Vector3 startPosition, float sphereRadius, Vector3 movementVector, float precision, out Vector3 endPosition, bool singleHand)
        {
            RaycastHit hitInfo;
            Vector3 movementToProjectedAboveCollisionPlane;
            Surface gorillaSurface;
            float slipPercentage;
            //first spherecast from the starting position to the final position
            if (CollisionsSphereCast(startPosition, sphereRadius * precision, movementVector, precision, out endPosition, out hitInfo))
            {
                //if we hit a surface, do a bit of a slide. this makes it so if you grab with two hands you don't stick 100%, and if you're pushing along a surface while braced with your head, your hand will slide a bit

                //take the surface normal that we hit, then along that plane, do a spherecast to a position a small distance away to account for moving perpendicular to that surface
                lastCastHit = hitInfo;
                Vector3 firstPosition = endPosition;
                gorillaSurface = hitInfo.collider.GetComponent<Surface>();
                slipPercentage = gorillaSurface != null ? gorillaSurface.slipPercentage : (!singleHand ? defaultSlideFactor : 0.001f);
                movementToProjectedAboveCollisionPlane = Vector3.ProjectOnPlane(startPosition + movementVector - firstPosition, hitInfo.normal) * slipPercentage;
                if (CollisionsSphereCast(endPosition, sphereRadius, movementToProjectedAboveCollisionPlane, precision * precision, out endPosition, out hitInfo))
                {
                    //if we hit trying to move perpendicularly, stop there and our end position is the final spot we hit
                    return true;
                }
                //if not, try to move closer towards the true point to account for the fact that the movement along the normal of the hit could have moved you away from the surface
                else if (CollisionsSphereCast(movementToProjectedAboveCollisionPlane + firstPosition, sphereRadius, startPosition + movementVector - (movementToProjectedAboveCollisionPlane + firstPosition), precision * precision * precision, out endPosition, out hitInfo))
                {
                    //if we hit, then return the spot we hit
                    return true;
                }
                else
                {
                    //this shouldn't really happe, since this means that the sliding motion got you around some corner or something and let you get to your final point. back off because something strange happened, so just don't do the slide
                    endPosition = firstPosition;
                    return true;
                }
            }
            //as kind of a sanity check, try a smaller spherecast. this accounts for times when the original spherecast was already touching a surface so it didn't trigger correctly
            else if (CollisionsSphereCast(startPosition, sphereRadius * precision * 0.66f, movementVector.normalized * (movementVector.magnitude + sphereRadius * precision * 0.34f), precision * 0.66f, out endPosition, out hitInfo))
            {
                lastCastHit = hitInfo;
                endPosition = startPosition;
                return true;
            } else
            {
                endPosition = Vector3.zero;
                return false;
            }
        }

        private bool CollisionsSphereCast(Vector3 startPosition, float sphereRadius, Vector3 movementVector, float precision, out Vector3 finalPosition, out RaycastHit hitInfo)
        {
            //kind of like a souped up spherecast. includes checks to make sure that the sphere we're using, if it touches a surface, is pushed away the correct distance (the original sphereradius distance). since you might
            //be pushing into sharp corners, this might not always be valid, so that's what the extra checks are for

            //initial spherecase
            RaycastHit innerHit;
            if (Physics.SphereCast(startPosition, sphereRadius * precision, movementVector, out hitInfo, movementVector.magnitude + sphereRadius * (1 - precision), locomotionEnabledLayers.value))
            {
                //if we hit, we're trying to move to a position a sphereradius distance from the normal
                finalPosition = hitInfo.point + hitInfo.normal * sphereRadius;

                //check a spherecase from the original position to the intended final position
                if (Physics.SphereCast(startPosition, sphereRadius * precision * precision, finalPosition - startPosition, out innerHit, (finalPosition - startPosition).magnitude + sphereRadius * (1 - precision * precision), locomotionEnabledLayers.value))
                {
                    finalPosition = startPosition + (finalPosition - startPosition).normalized * Mathf.Max(0, hitInfo.distance - sphereRadius * (1f - precision * precision));
                    hitInfo = innerHit;
                }
                //bonus raycast check to make sure that something odd didn't happen with the spherecast. helps prevent clipping through geometry
                else if (Physics.Raycast(startPosition, finalPosition - startPosition, out innerHit, (finalPosition - startPosition).magnitude + sphereRadius * precision * precision * 0.999f, locomotionEnabledLayers.value))
                {
                    finalPosition = startPosition;
                    hitInfo = innerHit;
                    return true;
                }
                return true;
            }
            //anti-clipping through geometry check
            else if (Physics.Raycast(startPosition, movementVector, out hitInfo, movementVector.magnitude + sphereRadius * precision * 0.999f, locomotionEnabledLayers.value))
            {
                finalPosition = startPosition;
                return true;
            }
            else
            {
                finalPosition = Vector3.zero;
                return false;
            }
        }

        // Raises HandContact on the frame a hand starts touching. The impact speed is how fast the hand (the tracked
        // controller, in world space, so it includes the rig's own movement) was travelling into the surface, taken from
        // the movement the controller really made this frame, not the clipped one the surface allowed.
        private void ReportContact(bool leftHand, bool touching, bool wasTouching, ref float airTime, Vector3 handMotion, bool hasHit, RaycastHit hit, Vector3 position)
        {
            if (!touching)
            {
                airTime += Time.deltaTime;
                return;
            }

            if (!wasTouching && HandContact != null)
            {
                Vector3 velocity = handMotion / Mathf.Max(Time.deltaTime, 0.0001f);
                // A cast that starts inside a collider reports no useful normal; fall back to "straight against the motion".
                Vector3 normal = hasHit && hit.normal.sqrMagnitude > 0.5f ? hit.normal : (velocity.sqrMagnitude > 0.000001f ? -velocity.normalized : Vector3.up);

                HandContact(new HandContactInfo
                {
                    leftHand = leftHand,
                    position = position,
                    normal = normal,
                    impactSpeed = Mathf.Max(0f, -Vector3.Dot(velocity, normal)),
                    handSpeed = velocity.magnitude,
                    airTime = airTime,
                    surface = hasHit ? hit.collider : null,
                });
            }

            airTime = 0f;
        }

        public bool IsHandTouching(bool forLeftHand)
        {
            if (forLeftHand)
            {
                return wasLeftHandTouching;
            }
            else
            {
                return wasRightHandTouching;
            }
        }

        public void Turn(float degrees)
        {
            transform.RotateAround(headCollider.transform.position, transform.up, degrees);
            Quaternion turn = Quaternion.Euler(0, degrees, 0);
            denormalizedVelocityAverage = turn * denormalizedVelocityAverage;
            for (int i = 0; i < velocityHistory.Length; i++)
            {
                velocityHistory[i] = turn * velocityHistory[i];
            }
            launchEstimate = turn * launchEstimate;
            bodyVelocity = turn * bodyVelocity;
            momentum = turn * momentum;
            pendingLaunch = turn * pendingLaunch;
            pendingDelta = turn * pendingDelta;
            rampDelta = turn * rampDelta;
            bridgeVelocity = turn * bridgeVelocity;
        }

        private void StoreVelocities()
        {
            // a frame that took no time (paused) has no movement to measure, and dividing by it would poison the averages for good
            float dt = Time.deltaTime;
            if (dt < 0.00001f)
            {
                return;
            }

            velocityIndex = (velocityIndex + 1) % velocityHistorySize;
            Vector3 oldestVelocity = velocityHistory[velocityIndex];
            currentVelocity = (transform.position - lastPosition) / dt;
            denormalizedVelocityAverage += (currentVelocity - oldestVelocity) / (float)velocityHistorySize;
            velocityHistory[velocityIndex] = currentVelocity;
            lastPosition = transform.position;

            // the smooth launch follows the same movement with a time based average, so it feels the same at 72, 90 or 120 Hz (a teleport is skipped)
            if (currentVelocity.sqrMagnitude < TeleportSpeed * TeleportSpeed)
            {
                launchEstimate = Vector3.Lerp(launchEstimate, currentVelocity, 1f - Mathf.Exp(-dt / Mathf.Max(launchResponseTime, 0.001f)));
                bodyVelocity = Vector3.Lerp(bodyVelocity, currentVelocity, 1f - Mathf.Exp(-dt / BodyVelocityResponse));
            }
        }
    }

    /// <summary>What <see cref="Player.HandContact"/> reports when a hand lands on a surface.</summary>
    public struct HandContactInfo
    {
        public bool leftHand;
        /// <summary>Where the hand is (right at the surface), world space.</summary>
        public Vector3 position;
        /// <summary>Surface normal at the contact, pointing out of the surface.</summary>
        public Vector3 normal;
        /// <summary>Hand speed straight into the surface, m/s. 0 for a hand that arrived sliding along it.</summary>
        public float impactSpeed;
        /// <summary>Total hand speed, m/s.</summary>
        public float handSpeed;
        /// <summary>How long the hand was free before this contact, seconds.</summary>
        public float airTime;
        /// <summary>The collider that was hit (null if unknown).</summary>
        public Collider surface;
    }
}