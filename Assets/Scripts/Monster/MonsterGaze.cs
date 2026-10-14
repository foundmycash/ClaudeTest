using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// Turns the monster's neck bone to stare at the player whenever the player is in its line of sight, and slowly
    /// lets go when they are not. Meant to look alive and wrong at the same time:
    ///
    /// - It does not react at once. When you step into view it goes perfectly still for a moment (the idle sway
    ///   stops), and only then turns, slowly, with a soft start and a soft stop.
    /// - The longer it stares, the more it locks on: the tracking gets tighter and steadier, and the head slowly
    ///   tilts to one side, further and further, like something studying you.
    /// - While it stares, now and then the head gives a tiny sharp twitch, which then settles again.
    /// - When you break line of sight it keeps staring at the spot where it last saw you, then creeps back to rest,
    ///   and starts to wander again.
    ///
    /// Line of sight = within View Distance, inside the Field Of View (centred on the way it faces), and nothing in
    /// the way (trees, rocks, the ground). Add this component to the monster (the object that moves it, not the model
    /// inside), and it finds the Neck, Head and Jaw bones by name; drag different bones in if yours are named
    /// differently. Select the monster to see the way it faces, its field of view and where it is looking in the
    /// Scene view.
    ///
    /// The neck is turned after every animator and Legs Animator has run, on top of whatever pose they leave, so it
    /// keeps working with animations added later. Nothing is allocated per frame.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    [AddComponentMenu("Monster/Monster Gaze")]
    public class MonsterGaze : MonoBehaviour
    {
        public enum FaceDirection { Auto, Forward, Back, Right, Left }

        public enum GazeState
        {
            /// <summary>Nobody in sight. The head rests and wanders a little.</summary>
            Idle,
            /// <summary>Someone just came into sight. The head freezes before it reacts.</summary>
            Noticing,
            /// <summary>Following the target with its head.</summary>
            Tracking,
            /// <summary>The target is gone. Still staring at the place it was last seen.</summary>
            Holding,
        }

        [Header("Bones")]
        [Tooltip("The neck bone that is turned. Found by name (\"Neck\") when empty.")]
        public Transform neck;

        [Tooltip("The head bone. Only used to work out where the eyes are (halfway to its tip), so the stare lines up with the target. Found by name (\"Head\") when empty.")]
        public Transform head;

        [Tooltip("The jaw bone. With Face Direction on Auto, the monster is taken to face the way its jaw opens. Found by name (\"Jaw\") when empty.")]
        public Transform jaw;

        [Tooltip("Optional. An exact eye position (an empty object on the head). Empty = halfway along the head bone.")]
        public Transform eyes;

        [Tooltip("What the monster looks at. Empty = the main camera, which is the player's head.")]
        public Transform target;

        [Header("Facing")]
        [Tooltip("The way the monster faces when it is standing still, relative to this object. Auto works it out from the way the jaw opens, so it is right whichever way the model was exported. If it stares the wrong way, pick the direction by hand.")]
        public FaceDirection faceDirection = FaceDirection.Auto;

        [Header("Line of sight")]
        [Tooltip("Furthest distance in metres at which it can see the target.")]
        [Min(0f)] public float lookDistance = 25f;

        [Tooltip("Width of its view in degrees, centred on the way it faces. It spots you inside this; once it has you, it keeps tracking a little (20 degrees) beyond it so it does not flicker at the edge.")]
        [Range(20f, 340f)] public float fieldOfView = 150f;

        [Tooltip("Layers that block its view (trees, rocks, the ground). The monster itself and the player never do. Trigger colliders are ignored.")]
        public LayerMask blockedBy = Physics.DefaultRaycastLayers;

        [Tooltip("How often, in seconds, it checks what it can see. 0.1 is plenty.")]
        [Range(0.02f, 0.5f)] public float checkInterval = 0.1f;

        [Tooltip("Seconds it still counts the target as seen after sight drops out, so a thin branch crossing the view does not make it lose you.")]
        [Range(0f, 1f)] public float sightMemory = 0.35f;

        [Header("Turning")]
        [Tooltip("How eagerly the head swings to the target. Higher = quicker and snappier, lower = heavier and slower. Around 3-4 is slow and deliberate, 8 is alert.")]
        [Range(1f, 14f)] public float stiffness = 4.5f;

        [Tooltip("1 = glides to a stop. Lower = overshoots a little and settles back, which reads as weight (0.7-0.9 is natural). Under 0.5 it wobbles.")]
        [Range(0.3f, 1.2f)] public float damping = 0.8f;

        [Tooltip("The fastest the head is ever allowed to turn, in degrees per second.")]
        [Min(5f)] public float maxTurnSpeed = 110f;

        [Tooltip("How far each way it can turn its neck to the side, in degrees. Past about 110 the model's skin starts to stretch.")]
        [Range(10f, 170f)] public float yawLimit = 100f;

        [Tooltip("How far it can look up, in degrees.")]
        [Range(0f, 80f)] public float pitchUpLimit = 35f;

        [Tooltip("How far it can look down, in degrees.")]
        [Range(0f, 80f)] public float pitchDownLimit = 40f;

        [Tooltip("Shortest and longest pause in seconds between you coming into sight and it reacting. A random time in between each time, and it stands perfectly still meanwhile.")]
        public Vector2 reactionDelay = new Vector2(0.35f, 0.9f);

        [Header("Unsettling")]
        [Tooltip("Seconds of staring until it has fully locked on (steadier, tighter tracking and the first twitches).")]
        [Min(0.1f)] public float lockOnTime = 2.5f;

        [Tooltip("How much stiffer the tracking gets once locked on. 1 = no change.")]
        [Range(1f, 3f)] public float lockedStiffness = 1.6f;

        [Tooltip("How far, in degrees, the head ends up tilted sideways after a long stare. The side is random each time. 0 = never tilts.")]
        [Range(0f, 40f)] public float headTilt = 14f;

        [Tooltip("Seconds of staring until the tilt reaches its full angle. Slow is creepier.")]
        [Min(0.5f)] public float tiltBuildTime = 8f;

        [Tooltip("How strong the small sharp twitches are while it stares. 0 = none, 1 = a couple of degrees, 3 = jerky.")]
        [Range(0f, 3f)] public float twitchStrength = 1f;

        [Tooltip("Shortest and longest wait in seconds between twitches. Random each time, so there is no rhythm.")]
        public Vector2 twitchInterval = new Vector2(1.5f, 5f);

        [Header("Losing sight")]
        [Tooltip("Seconds it keeps staring at the place it last saw you after you vanish from view.")]
        [Min(0f)] public float holdTime = 2.5f;

        [Tooltip("How fast it creeps back to its resting pose afterwards, in degrees per second. Slow looks reluctant.")]
        [Min(1f)] public float returnSpeed = 22f;

        [Header("Idle")]
        [Tooltip("How much the head wanders and breathes while nobody is in sight. 0 = perfectly still, 1 = a slow drift of 10-15 degrees, 2 = restless.")]
        [Range(0f, 2f)] public float idleSway = 1f;

        [Header("General")]
        [Tooltip("1 = fully looks at the target, 0 = off (the neck keeps whatever pose the animation gives it). Fade this to hand the neck back to an animation.")]
        [Range(0f, 1f)] public float weight = 1f;

        [Tooltip("Draw the facing direction, field of view and gaze in the Scene view while this object is selected.")]
        public bool drawGizmos = true;

        // Spring settings that do not need a slider.
        const float IdleStiffness = 1.5f;       // resting / wandering: slow and loose
        const float NoticeStiffness = 3f;       // freezing before the reaction: brings the head gently to a stop
        const float TiltStiffness = 1.3f;       // the tilt creeps
        const float TiltMaxSpeed = 9f;          // degrees per second
        const float LeadIn = 0.14f;             // seconds the target is eased in, so a turn starts gently instead of at full push
        const float TwitchStiffness = 28f;
        const float TwitchDamping = 0.5f;
        const float SightHysteresis = 20f;      // extra half-angle kept once it is tracking
        const float SoftStart = 0.7f;        // the soft turn limits start easing in at this fraction of the limit
        const float MaxStep = 1f / 90f;         // spring integration step
        const int HitBufferSize = 24;

        static readonly RaycastHit[] hitBuffer = new RaycastHit[HitBufferSize];

        public GazeState State { get { return state; } }

        /// <summary>Where the eyes are, in the world. Other monster scripts (hunting vision) look from the same place.</summary>
        public Vector3 EyeWorldPosition
        {
            get
            {
                EnsureReady();
                return ready ? EyePosition() : transform.position + transform.up * 2f;
            }
        }

        /// <summary>The way the monster faces when standing still, as a flat direction in this object's own space (the Auto measurement included).</summary>
        public Vector3 FacingLocal
        {
            get
            {
                EnsureReady();
                return ready ? faceLocal : Vector3.forward;
            }
        }

        /// <summary>True while it is staring at the target (or at the place it last saw it).</summary>
        public bool IsStaring { get { return state == GazeState.Tracking || state == GazeState.Holding; } }

        /// <summary>True while the target is in its line of sight.</summary>
        public bool CanSeeTarget { get { return inSight; } }

        /// <summary>How locked on it is, 0 (just noticed you) to 1 (fully locked, tilting and twitching).</summary>
        public float LockOn { get { return lockOn; } }

        /// <summary>Current head turn in degrees, from the way it faces; positive = to its right.</summary>
        public float Yaw { get { return yaw + twitchYaw; } }

        /// <summary>Current head pitch in degrees; positive = looking up.</summary>
        public float Pitch { get { return pitch + twitchPitch; } }

        /// <summary>Current sideways head tilt in degrees.</summary>
        public float Tilt { get { return tilt + twitchTilt; } }

        GazeState state = GazeState.Idle;
        float stateTime;
        float noticeDelay;
        float stareTime;
        float lockOn;
        float tiltSide = 1f;
        float idleBlend = 1f;

        bool inSight;
        float lastSeenAt = -999f;
        float checkTimer;
        Vector3 lastKnownPoint;

        float yaw, yawVel, pitch, pitchVel, tilt, tiltVel;
        float leadYaw, leadPitch;
        float twitchYaw, twitchYawVel, twitchPitch, twitchPitchVel, twitchTilt, twitchTiltVel;
        float twitchTimer;
        float stiffnessNow = IdleStiffness;
        float speedNow = 20f;

        float clock;
        float noiseSeed;

        Vector3 faceLocal = Vector3.forward;
        bool ready;
        bool warned;

        Quaternion baseLocal;
        Quaternion appliedLocal;
        bool applied;

        Camera cachedCamera;
        float nextCameraLookup;

        void Reset()
        {
            FindBones();
        }

        void Awake()
        {
            EnsureReady();
        }

        void OnEnable()
        {
            checkTimer = 0f;
        }

        void OnDisable()
        {
            // Hand the neck back exactly as the animation left it.
            RestoreBase(true);
        }

        void OnValidate()
        {
            if (reactionDelay.y < reactionDelay.x) reactionDelay.y = reactionDelay.x;
            if (twitchInterval.x < 0.05f) twitchInterval.x = 0.05f;
            if (twitchInterval.y < twitchInterval.x) twitchInterval.y = twitchInterval.x;
            // A hand-picked direction can be changed live. Auto is only measured once, at start, while the neck is unturned.
            if (Application.isPlaying && ready && faceDirection != FaceDirection.Auto)
                faceLocal = ComputeFaceLocal();
        }

        /// <summary>Finds the neck, head and jaw bones below this object by name. Also in the component's context menu.</summary>
        [ContextMenu("Find Bones")]
        public void FindBones()
        {
            if (neck == null) neck = FindBone("Neck");
            if (head == null) head = FindBone("Head");
            if (jaw == null) jaw = FindBone("Jaw");
            if (head == null && neck != null && neck.childCount > 0) head = neck.GetChild(0);
        }

        Transform FindBone(string boneName)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            Transform partial = null;
            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i].name;
                if (string.Equals(n, boneName, System.StringComparison.OrdinalIgnoreCase))
                    return all[i];
                // "Head_end" and similar leaf markers are not bones to move
                if (partial == null && n.IndexOf(boneName, System.StringComparison.OrdinalIgnoreCase) >= 0
                    && n.IndexOf("_end", System.StringComparison.OrdinalIgnoreCase) < 0)
                    partial = all[i];
            }
            return partial;
        }

        void EnsureReady()
        {
            if (ready)
                return;
            if (neck == null)
                FindBones();
            if (neck == null)
            {
                if (!warned)
                {
                    warned = true;
                    Debug.LogWarning("MonsterGaze on '" + name + "' found no neck bone. Drag it into the Neck slot.", this);
                }
                return;
            }

            faceLocal = ComputeFaceLocal();
            noiseSeed = Random.value * 100f;
            twitchTimer = Random.Range(twitchInterval.x, twitchInterval.y);
            baseLocal = neck.localRotation;
            applied = false;
            ready = true;
        }

        /// <summary>The way the monster faces, as a flat direction in this object's own space.</summary>
        Vector3 ComputeFaceLocal()
        {
            switch (faceDirection)
            {
                case FaceDirection.Forward: return Vector3.forward;
                case FaceDirection.Back: return Vector3.back;
                case FaceDirection.Right: return Vector3.right;
                case FaceDirection.Left: return Vector3.left;
            }

            // Auto: a jaw hinges at the back of the skull and opens forward, so the line from the hinge to its tip points where it faces.
            if (jaw != null && jaw.childCount > 0)
            {
                Vector3 local = transform.InverseTransformDirection(jaw.GetChild(0).position - jaw.position);
                local.y = 0f;
                if (local.sqrMagnitude > 1e-4f)
                    return local.normalized;
            }
            return Vector3.forward;
        }

        Quaternion FaceFrame(Vector3 localFace)
        {
            Vector3 f = transform.TransformDirection(localFace);
            Vector3 up = transform.up;
            f = Vector3.ProjectOnPlane(f, up);
            if (f.sqrMagnitude < 1e-6f)
                f = transform.forward;
            return Quaternion.LookRotation(f, up);
        }

        Vector3 EyePosition()
        {
            if (eyes != null)
                return eyes.position;
            if (head != null)
                return head.childCount > 0 ? Vector3.Lerp(head.position, head.GetChild(0).position, 0.5f) : head.position;
            return neck.position + transform.up;
        }

        Transform ResolveTarget()
        {
            if (target != null)
                return target;
            if ((cachedCamera == null || !cachedCamera.isActiveAndEnabled) && Time.unscaledTime >= nextCameraLookup)
            {
                cachedCamera = Camera.main;
                nextCameraLookup = Time.unscaledTime + 1f;
            }
            return cachedCamera != null ? cachedCamera.transform : null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Per frame

        /// <summary>Called by Unity before the animation runs: puts the neck back to the pose it had before this script bent it.</summary>
        void Update()
        {
            RestoreBase(false);
        }

        void LateUpdate()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>Puts the neck back the way it was before this script turned it. Called every frame; call it yourself before <see cref="Tick"/> to pose the model in the editor.</summary>
        public void RestoreBase(bool force)
        {
            if (!applied || neck == null)
                return;
            // If something else (an animator) wrote the neck since we did, that is the new pose; leave it alone.
            if (force || Quaternion.Angle(neck.localRotation, appliedLocal) < 0.01f)
                neck.localRotation = baseLocal;
            applied = false;
        }

        /// <summary>Looks, thinks and turns the neck. Called every frame after all animation; call it yourself to pose the model in the editor.</summary>
        public void Tick(float deltaTime)
        {
            EnsureReady();
            if (!ready)
                return;

            float dt = Mathf.Clamp(deltaTime, 0f, 0.05f);
            clock += dt;

            // The pose to build on: whatever the animation (or the rest pose) gave the neck this frame.
            RestoreBase(false);
            baseLocal = neck.localRotation;
            Quaternion frame = FaceFrame(faceLocal);
            Vector3 eye = EyePosition();
            Transform t = ResolveTarget();

            Sense(t, eye, frame, dt);
            Think(t, dt);
            Move(frame, eye, t, dt);
            Apply(frame);
        }

        // --- seeing

        void Sense(Transform t, Vector3 eye, Quaternion frame, float dt)
        {
            checkTimer -= dt;
            if (checkTimer <= 0f)
            {
                checkTimer = checkInterval * Random.Range(0.8f, 1.2f);
                if (t != null && CanSee(t, eye, frame))
                {
                    lastSeenAt = clock;
                    lastKnownPoint = t.position;    // only a real sighting counts, not the grace period after one
                }
            }
            inSight = t != null && clock - lastSeenAt <= sightMemory;
        }

        bool CanSee(Transform t, Vector3 eye, Quaternion frame)
        {
            Vector3 to = t.position - eye;
            float dist = to.magnitude;
            bool staring = IsStaring;
            if (dist > (staring ? lookDistance * 1.1f : lookDistance))
                return false;
            if (dist < 0.05f)
                return true;

            float halfView = fieldOfView * 0.5f + (staring ? SightHysteresis : 0f);
            if (Vector3.Angle(frame * Vector3.forward, to) > halfView)
                return false;

            return !IsBlocked(t, eye, to / dist, dist);
        }

        bool IsBlocked(Transform t, Vector3 eye, Vector3 dir, float dist)
        {
            // What counts as "the player" for the ray: the Gorilla player if there is one, else everything the target hangs from.
            Transform playerBody = GorillaLocomotion.Player.Instance != null ? GorillaLocomotion.Player.Instance.transform : null;
            Transform targetRoot = t.root != transform.root ? t.root : t;

            int count = Physics.RaycastNonAlloc(eye, dir, hitBuffer, dist, blockedBy, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Transform hit = hitBuffer[i].collider.transform;
                if (hit.IsChildOf(transform))
                    continue;                       // the monster's own body
                if (hit.IsChildOf(targetRoot) || (playerBody != null && hit.IsChildOf(playerBody)))
                    continue;                       // the player
                return true;
            }
            return false;
        }

        // --- deciding

        void Think(Transform t, float dt)
        {
            stateTime += dt;
            switch (state)
            {
                case GazeState.Idle:
                    if (inSight)
                        Enter(GazeState.Noticing);
                    break;

                case GazeState.Noticing:
                    if (!inSight)
                        Enter(GazeState.Idle);
                    else if (stateTime >= noticeDelay)
                        Enter(GazeState.Tracking);
                    break;

                case GazeState.Tracking:
                    stareTime += dt;
                    if (!inSight)
                        Enter(GazeState.Holding);
                    break;

                case GazeState.Holding:
                    stareTime += dt;
                    if (inSight)
                        Enter(GazeState.Tracking);
                    else if (stateTime >= holdTime)
                        Enter(GazeState.Idle);
                    break;
            }

            lockOn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(stareTime / lockOnTime));
            // The wandering fades back in slowly after it stops staring, so it does not start fidgeting the moment you leave.
            idleBlend = state == GazeState.Idle ? Mathf.MoveTowards(idleBlend, 1f, dt / 4f) : 0f;
        }

        void Enter(GazeState next)
        {
            GazeState previous = state;
            state = next;
            stateTime = 0f;

            switch (next)
            {
                case GazeState.Noticing:
                    noticeDelay = Random.Range(reactionDelay.x, reactionDelay.y);
                    break;

                case GazeState.Tracking:
                    if (previous == GazeState.Noticing)
                    {
                        // A fresh stare: new tilt side, nothing built up yet.
                        stareTime = 0f;
                        tiltSide = Random.value < 0.5f ? -1f : 1f;
                        twitchTimer = Random.Range(twitchInterval.x, twitchInterval.y);
                        leadYaw = yaw;
                        leadPitch = pitch;
                    }
                    break;

                case GazeState.Idle:
                    stareTime = 0f;
                    break;
            }
        }

        // --- moving

        void Move(Quaternion frame, Vector3 eye, Transform t, float dt)
        {
            float wantYaw, wantPitch, wantTilt, wantStiffness, wantSpeed;

            switch (state)
            {
                case GazeState.Tracking:
                case GazeState.Holding:
                {
                    Vector3 point = state == GazeState.Tracking && t != null ? t.position : lastKnownPoint;
                    float rawYaw, rawPitch;
                    SolveAngles(point, frame, eye, out rawYaw, out rawPitch);
                    rawYaw = SoftLimit(rawYaw, -yawLimit, yawLimit);
                    rawPitch = SoftLimit(rawPitch, -pitchDownLimit, pitchUpLimit);

                    // Ease the target in so a turn starts gently, then follow it with the spring.
                    float follow = 1f - Mathf.Exp(-dt / LeadIn);
                    leadYaw += (rawYaw - leadYaw) * follow;
                    leadPitch += (rawPitch - leadPitch) * follow;

                    wantYaw = leadYaw;
                    wantPitch = leadPitch;
                    wantTilt = tiltSide * headTilt * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(stareTime / tiltBuildTime));
                    wantStiffness = stiffness * Mathf.Lerp(1f, lockedStiffness, lockOn);
                    wantSpeed = maxTurnSpeed * Mathf.Lerp(1f, 1.25f, lockOn);
                    break;
                }

                case GazeState.Noticing:
                    // Stop dead: hold exactly where it is.
                    wantYaw = yaw;
                    wantPitch = pitch;
                    wantTilt = tilt;
                    wantStiffness = NoticeStiffness;
                    wantSpeed = returnSpeed;
                    break;

                default:
                    IdleTarget(out wantYaw, out wantPitch, out wantTilt);
                    wantStiffness = IdleStiffness;
                    wantSpeed = returnSpeed;
                    break;
            }

            // Changes of mood are smoothed too, so the head never lurches when the state flips. It calms down quickly (a slow
            // return must not start with a rush left over from the fast turn) but gets keener gradually.
            stiffnessNow = Mathf.Lerp(stiffnessNow, wantStiffness, 1f - Mathf.Exp(-dt * (wantStiffness < stiffnessNow ? 12f : 3f)));
            speedNow = Mathf.Lerp(speedNow, wantSpeed, 1f - Mathf.Exp(-dt * (wantSpeed < speedNow ? 12f : 3f)));

            // Twitches: tiny sharp kicks that a stiff spring pulls straight back.
            if (state == GazeState.Tracking && lockOn > 0.2f && twitchStrength > 0f)
            {
                twitchTimer -= dt;
                if (twitchTimer <= 0f)
                {
                    twitchTimer = Random.Range(twitchInterval.x, twitchInterval.y);
                    float kick = 100f * twitchStrength;
                    twitchYawVel += Random.Range(-1f, 1f) * kick;
                    twitchPitchVel += Random.Range(-1f, 1f) * kick * 0.6f;
                    twitchTiltVel += Random.Range(-1f, 1f) * kick * 0.8f;
                }
            }

            float step = dt;
            while (step > 0f)
            {
                float h = Mathf.Min(step, MaxStep);
                step -= h;
                Spring(ref yaw, ref yawVel, wantYaw, stiffnessNow, damping, speedNow, h);
                Spring(ref pitch, ref pitchVel, wantPitch, stiffnessNow * 0.85f, damping, speedNow * 0.6f, h);
                Spring(ref tilt, ref tiltVel, wantTilt, TiltStiffness, 1f, TiltMaxSpeed, h);
                Spring(ref twitchYaw, ref twitchYawVel, 0f, TwitchStiffness, TwitchDamping, 0f, h);
                Spring(ref twitchPitch, ref twitchPitchVel, 0f, TwitchStiffness, TwitchDamping, 0f, h);
                Spring(ref twitchTilt, ref twitchTiltVel, 0f, TwitchStiffness, TwitchDamping, 0f, h);
            }
        }

        /// <summary>A resting pose with slow, never-repeating wander and a faint breath.</summary>
        void IdleTarget(out float wantYaw, out float wantPitch, out float wantTilt)
        {
            float s = idleSway * idleBlend;
            float n = clock * 0.2f;
            wantYaw = (Noise(n, 0f) * 1f + Noise(n * 2.7f, 10f) * 0.35f) * 11f * s;
            wantPitch = Noise(n * 0.8f, 20f) * 3.5f * s + Mathf.Sin(clock * 1.35f) * 0.6f * s;
            wantTilt = Noise(n * 0.6f, 30f) * 2.5f * s;
        }

        /// <summary>Smooth noise in -1..1.</summary>
        float Noise(float x, float channel)
        {
            return Mathf.PerlinNoise(x + noiseSeed, channel + noiseSeed * 0.37f) * 2f - 1f;
        }

        /// <summary>
        /// The yaw and pitch (degrees, from the way it faces) that point the eyes at a spot. The eyes sit well in front of the
        /// neck bone they swing around, so the first answer is refined a few times with the eyes where the turn would put them.
        /// </summary>
        void SolveAngles(Vector3 point, Quaternion frame, Vector3 eye, out float solvedYaw, out float solvedPitch)
        {
            Quaternion inverse = Quaternion.Inverse(frame);
            Vector3 pivot = neck.position;
            Vector3 arm = eye - pivot;
            solvedYaw = 0f;
            solvedPitch = 0f;
            for (int i = 0; i < 3; i++)
            {
                Quaternion r = frame * Quaternion.Euler(-solvedPitch, solvedYaw, 0f) * inverse;
                Vector3 d = inverse * (point - (pivot + r * arm));
                solvedYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                solvedPitch = Mathf.Atan2(d.y, Mathf.Sqrt(d.x * d.x + d.z * d.z)) * Mathf.Rad2Deg;
            }
        }

        /// <summary>Linear up to a fraction of the limit, then eases into it, so the head never hits a hard wall.</summary>
        static float SoftLimit(float value, float min, float max)
        {
            float limit = value >= 0f ? max : -min;
            if (limit <= 0.01f)
                return 0f;
            float a = Mathf.Abs(value);
            float start = limit * SoftStart;
            if (a <= start)
                return value;
            float room = limit - start;
            float eased = start + room * (float)System.Math.Tanh((a - start) / room);
            return value >= 0f ? eased : -eased;
        }

        static void Spring(ref float x, ref float v, float goal, float stiffness, float damping, float maxSpeed, float dt)
        {
            v += (stiffness * stiffness * (goal - x) - 2f * damping * stiffness * v) * dt;
            if (maxSpeed > 0f)
                v = Mathf.Clamp(v, -maxSpeed, maxSpeed);
            x += v * dt;
        }

        void Apply(Quaternion frame)
        {
            Quaternion turn = Quaternion.Euler(-(pitch + twitchPitch), yaw + twitchYaw, tilt + twitchTilt);
            Quaternion delta = frame * turn * Quaternion.Inverse(frame);
            if (weight < 1f)
                delta = Quaternion.Slerp(Quaternion.identity, delta, Mathf.Clamp01(weight));
            neck.rotation = delta * neck.rotation;
            appliedLocal = neck.localRotation;
            applied = true;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Scene view

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos || neck == null)
                return;

            Vector3 local = Application.isPlaying && ready ? faceLocal : ComputeFaceLocal();
            Quaternion frame = FaceFrame(local);
            Vector3 eye = EyePosition();
            float length = Mathf.Min(lookDistance, 15f);

            Gizmos.color = new Color(0.2f, 0.8f, 1f);
            Gizmos.DrawRay(eye, frame * Vector3.forward * length * 0.5f);

            Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.8f);
            float half = fieldOfView * 0.5f;
            Gizmos.DrawRay(eye, frame * Quaternion.Euler(0f, half, 0f) * Vector3.forward * length);
            Gizmos.DrawRay(eye, frame * Quaternion.Euler(0f, -half, 0f) * Vector3.forward * length);
            Gizmos.DrawRay(eye, frame * Quaternion.Euler(-half, 0f, 0f) * Vector3.forward * length);
            Gizmos.DrawRay(eye, frame * Quaternion.Euler(half, 0f, 0f) * Vector3.forward * length);

            if (Application.isPlaying && ready)
            {
                Gizmos.color = IsStaring ? Color.red : new Color(1f, 1f, 1f, 0.5f);
                Quaternion gaze = frame * Quaternion.Euler(-Pitch, Yaw, 0f);
                Gizmos.DrawRay(eye, gaze * Vector3.forward * length * 0.75f);
                Gizmos.DrawWireSphere(eye, 0.15f);
            }
        }
    }
}
