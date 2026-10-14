using System;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace MonsterAI
{
    /// <summary>
    /// The monster's hunting brain: what it does once a sense has noticed somebody. A small state machine:
    ///
    ///   Idle      stands where it is, <see cref="MonsterSenses"/> is looking and listening.
    ///   Chasing   locked on to ONE player (the first one a sense reported) until that player is safe or dead.
    ///   Returning walks back to where it started, senses back on (a new detection starts a new chase).
    ///
    /// Locking on is the whole point of keeping senses and chase apart. Senses are switched off for the length of
    /// the chase, so losing sight, going quiet, hiding or getting far away changes nothing: it keeps heading for the
    /// player's real position. A chase ends only when:
    ///   - the target is in a <see cref="SafeZone"/> (or <see cref="HuntTarget.forceSafe"/>) for Safe Confirm Time,
    ///   - the target is dead (<see cref="HuntTarget.Kill"/>), or
    ///   - the target left the game (destroyed, or unavailable longer than Unavailable Grace; a player who is merely
    ///     switched off for a moment is NOT treated as gone).
    /// It never switches to another player in the middle of a chase.
    ///
    /// Speed: starts at Base Chase Speed, after Acceleration Delay seconds it gains Speed Gain Per Second (easing in
    /// over Acceleration Ease In seconds, so there is no jolt) up to Max Chase Speed. Based on elapsed time, so it is the
    /// same at 72 or 120 fps. When the chase ends it eases down to Return Speed and the next chase starts from the base again.
    ///
    /// Movement is a NavMeshAgent on the NavMesh baked for the "Forest Monster" agent type; this script steers its
    /// speed and the turning itself (the model faces world -Z, so the agent's own rotation would walk it backwards),
    /// and tells the Legs Animator when it moves. Nothing here allocates per frame.
    ///
    /// When the monster is shared in a room only one player's game runs this (see <see cref="MonsterNetworkLink"/>);
    /// <see cref="SetAuthority"/> switches the others to just following the replicated position.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent), typeof(MonsterBody), typeof(MonsterSenses))]
    [DefaultExecutionOrder(-50)]
    [AddComponentMenu("Monster/Monster Hunter")]
    public class MonsterHunter : MonoBehaviour
    {
        public enum State { Idle, Chasing, Returning }

        public enum EndReason
        {
            /// <summary>The target has been in a safe zone for Safe Confirm Time.</summary>
            TargetSafe,
            /// <summary>The target was killed.</summary>
            TargetDead,
            /// <summary>The target left the game or stayed unavailable longer than Unavailable Grace.</summary>
            TargetLeft,
            /// <summary>Ended by code (<see cref="EndChase"/>).</summary>
            Manual,
        }

        [Header("Chase speed")]
        [Tooltip("Speed in m/s when a chase starts. (A Gorilla Tag player moves about 3-6 m/s while swinging.)")]
        [Min(0.1f)] public float baseChaseSpeed = 3.5f;

        [Tooltip("The speed never goes above this, however long the chase lasts.")]
        [Min(0.1f)] public float maxChaseSpeed = 8f;

        [Tooltip("How much faster it gets, in m/s per second of chase, once it is fully into its stride. 0.12 = reaches 8 m/s from 3.5 after about 40 s.")]
        [Min(0f)] public float speedGainPerSecond = 0.12f;

        [Tooltip("Seconds of chase before it starts to speed up. The first moments stay fair.")]
        [Min(0f)] public float accelerationDelay = 3f;

        [Tooltip("Seconds over which the speed gain eases in from nothing to full, so it never lurches. 0 = no easing.")]
        [Min(0f)] public float accelerationEaseIn = 4f;

        [Tooltip("Speed in m/s while walking back home after a chase.")]
        [Min(0.1f)] public float returnSpeed = 2.5f;

        [Tooltip("How fast the speed eases down to Return Speed when a chase ends, in m/s per second.")]
        [Min(0.1f)] public float slowDownPerSecond = 3f;

        [Header("Chasing")]
        [Tooltip("The goal is updated at most this often (seconds) while it chases, and only if the target has moved.")]
        [Range(0.05f, 1f)] public float repathInterval = 0.2f;

        [Tooltip("The goal is not updated unless the target moved this far (metres) since the last time.")]
        [Min(0f)] public float repathMinMove = 0.4f;

        [Tooltip("How far (metres) from a player in a tree or on a rock the monster looks for the closest ground it can walk to.")]
        [Min(0.5f)] public float maxSampleDistance = 8f;

        [Tooltip("The monster has caught the target when it is this close sideways (metres).")]
        [Min(0.1f)] public float catchDistance = 2.5f;

        [Tooltip("...and the target's chest is not higher than this above the monster's feet (metres).")]
        [Min(0f)] public float catchHeight = 4f;

        [Tooltip("On: reaching the target kills it (HuntTarget.Kill), which ends the chase. Off (the project has no death system yet): the catch only raises the 'On Target Caught' event, and the monster stays beside the player until they are safe or dead. Connect your death system to HuntTarget.Kill / On Target Caught.")]
        public bool killOnCatch = false;

        [Tooltip("The target must stay in a safe zone this long (seconds) before the chase ends, so brushing the edge of a zone does not end it.")]
        [Min(0f)] public float safeConfirmTime = 0.4f;

        [Tooltip("If the target object is switched off or gone for this long (seconds) the chase ends (the player left). Shorter gaps (a scene change, a respawn) are waited out and the monster heads for where the player was last seen.")]
        [Min(0f)] public float unavailableGrace = 8f;

        [Tooltip("Seconds without making progress before it re-plans its route.")]
        [Min(0.5f)] public float stuckSeconds = 2f;

        [Header("Movement")]
        [Tooltip("The NavMesh agent type the monster walks on. The forest NavMesh is baked for \"Forest Monster\"; if the monster's agent is on another type (it was on Humanoid) and there is no NavMesh for that one, this one is used instead. Empty = never change.")]
        public string agentTypeName = "Forest Monster";

        [Tooltip("The NavMesh agent's own acceleration (m/s per second) while hunting, so it can follow the speed changes closely. 0 = keep the agent's setting.")]
        [Min(0f)] public float agentAcceleration = 12f;

        [Tooltip("How fast it turns to face where it is going, in degrees per second.")]
        [Range(20f, 720f)] public float turnSpeed = 140f;

        [Tooltip("Share of its speed it keeps while it is turned far away from where it is going (a sharp turn). 1 = no slowing. Stops it from sliding sideways before it has turned round.")]
        [Range(0.1f, 1f)] public float minSpeedWhenTurning = 0.35f;

        [Tooltip("After a chase: walk back to where it started. Off = stay where the chase ended.")]
        public bool returnHome = true;

        [Tooltip("It counts as home when it is this close (metres). Small, so it walks all the way back to the exact spot it started on.")]
        [Min(0.3f)] public float arriveDistance = 0.6f;

        [Header("Idle wandering")]
        [Tooltip("While not hunting, walk slowly between random spots around where it started, instead of standing still. A chase interrupts it; afterwards it walks home first.")]
        public bool wanderWhenIdle = true;

        [Tooltip("How far from its start spot it may wander (metres).")]
        [Min(1f)] public float wanderRadius = 15f;

        [Tooltip("Walking speed while wandering (m/s).")]
        [Min(0.2f)] public float wanderSpeed = 1.5f;

        [Tooltip("Seconds it stands still between walks (random between the two).")]
        [Min(0f)] public float wanderPauseMin = 3f;
        [Min(0f)] public float wanderPauseMax = 8f;

        [Header("Events")]
        public UnityEvent onChaseStarted;
        public UnityEvent onChaseEnded;
        [Tooltip("Raised once when the monster reaches its target. The place to hook your death / tag / game-over logic to.")]
        public UnityEvent onTargetCaught;

        [Header("Debug")]
        [Tooltip("One Console line when a chase starts and ends.")]
        public bool logEvents = true;
        public bool drawGizmos = true;

        /// <summary>A chase started: the target and which sense noticed it.</summary>
        public event Action<HuntTarget, DetectionSource> ChaseStarted;
        /// <summary>A chase ended: the target (null if it was destroyed) and why.</summary>
        public event Action<HuntTarget, EndReason> ChaseEnded;
        /// <summary>The monster reached its target.</summary>
        public event Action<HuntTarget> TargetCaught;
        /// <summary>Any monster reached any target. Static so scene-wide effects (see CatchBlackout) need no reference to the monster.</summary>
        public static event Action<MonsterHunter, HuntTarget> AnyTargetCaught;

        // --- state, readable from outside

        public State CurrentState => state;
        /// <summary>The player it is locked on to; null unless chasing.</summary>
        public HuntTarget Target => target;
        public DetectionSource LastDetection => lastSource;
        /// <summary>Seconds since the lock-on.</summary>
        public float ChaseTime => chaseTime;
        /// <summary>The speed it is trying to reach, m/s.</summary>
        public float CurrentSpeed => currentSpeed;
        /// <summary>The speed it really moves at, m/s (measured, smoothed).</summary>
        public float ActualSpeed => actualSpeed;
        public Vector3 Velocity => velocity;
        /// <summary>True while it is really walking (with a little hysteresis, so it does not flicker at a standstill).</summary>
        public bool IsMoving => isMoving;
        /// <summary>False when another player's game runs the monster and this one only follows.</summary>
        public bool HasAuthority => hasAuthority;
        public MonsterSenses Senses => senses;
        public MonsterBody Body => body;
        public NavMeshAgent Agent => agent;
        public int StuckCount => stuckCount;

        NavMeshAgent agent;
        MonsterBody body;
        MonsterSenses senses;

        State state = State.Idle;
        bool wandering;
        float wanderTimer = 2f;
        float wanderStuckTimer;
        HuntTarget target;
        float repelUntil;                 // Time.time until which no chase may start (see Repel)
        int targetPlayerId = -1;
        DetectionSource lastSource;
        float chaseTime;
        float currentSpeed;
        float turnFactor = 1f;
        bool caught;
        float safeTimer;
        float checkTimer;
        const float CheckInterval = 0.1f;
        float unavailableTimer;
        float nextRebind;
        Vector3 lastKnownPosition;
        bool hasKnownPosition;

        float nextRepath;
        Vector3 lastRequestedGoal;
        bool hasGoal;
        bool goalReachable = true;

        float stuckTimer;
        Vector3 stuckRef;
        int stuckCount;
        float nextStuckWarning;

        Vector3 home;
        Quaternion homeRotation;
        bool settleFacing;
        float returnStuckTimer;

        bool hasAuthority = true;
        Vector3 lastPosition;
        Vector3 velocity;
        float actualSpeed;
        bool isMoving;
        bool warnedOffNavMesh;

        void Reset()
        {
            // Defaults that suit a tall crawler on a forest floor.
            NavMeshAgent a = GetComponent<NavMeshAgent>();
            if (a != null)
            {
                a.autoBraking = false;
                a.updateRotation = false;
            }
        }

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            body = GetComponent<MonsterBody>();
            senses = GetComponent<MonsterSenses>();

            home = transform.position;
            homeRotation = transform.rotation;
            lastPosition = transform.position;
            currentSpeed = baseChaseSpeed;

            SelectAgentType();

            // This script turns the monster itself (the agent would walk it tail first) and controls the braking.
            agent.updateRotation = false;
            agent.autoBraking = false;
            if (agentAcceleration > 0f)
                agent.acceleration = agentAcceleration;
        }

        void OnEnable()
        {
            if (senses != null)
                senses.Detected += OnDetected;
            HuntTarget.Died += OnTargetDied;
        }

        void OnDisable()
        {
            if (senses != null)
                senses.Detected -= OnDetected;
            HuntTarget.Died -= OnTargetDied;
        }

        void Start()
        {
            if (hasAuthority)
                EnsureOnNavMesh();
            senses.Scanning = hasAuthority && state != State.Chasing;
        }

        void OnValidate()
        {
            if (maxChaseSpeed < baseChaseSpeed)
                maxChaseSpeed = baseChaseSpeed;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Setup

        /// <summary>
        /// The agent was on the Humanoid type while the forest NavMesh is for "Forest Monster": nothing could be found
        /// to walk on. If the agent has no NavMesh at its feet but the named type does, use that one.
        /// </summary>
        void SelectAgentType()
        {
            if (string.IsNullOrEmpty(agentTypeName))
                return;
            if (SampleNavMesh(transform.position, agent.agentTypeID, 10f, out _))
                return;

            int count = NavMesh.GetSettingsCount();
            for (int i = 0; i < count; i++)
            {
                NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(i);
                if (settings.agentTypeID == agent.agentTypeID)
                    continue;
                if (NavMesh.GetSettingsNameFromID(settings.agentTypeID) != agentTypeName)
                    continue;
                if (!SampleNavMesh(transform.position, settings.agentTypeID, 10f, out _))
                    continue;

                Debug.Log("MonsterHunter: '" + name + "' had no NavMesh for its agent type, switching it to \"" + agentTypeName + "\". Set the agent type on the NavMeshAgent to make this permanent.", this);
                agent.agentTypeID = settings.agentTypeID;
                return;
            }
        }

        static bool SampleNavMesh(Vector3 position, int agentType, float distance, out NavMeshHit hit)
        {
            var filter = new NavMeshQueryFilter { agentTypeID = agentType, areaMask = NavMesh.AllAreas };
            return NavMesh.SamplePosition(position, out hit, distance, filter);
        }

        bool EnsureOnNavMesh()
        {
            if (!agent.enabled)
                return false;
            if (agent.isOnNavMesh)
                return true;

            NavMeshHit hit;
            if (SampleNavMesh(transform.position, agent.agentTypeID, 10f, out hit))
            {
                agent.Warp(hit.position);
                return true;
            }

            if (!warnedOffNavMesh)
            {
                warnedOffNavMesh = true;
                Debug.LogWarning("MonsterHunter: '" + name + "' is not on a NavMesh for its agent type (" + NavMesh.GetSettingsNameFromID(agent.agentTypeID) + "). Bake the forest's monster NavMesh (Havoc Forest > Bake Monster NavMesh) and make sure the monster stands on it.", this);
            }
            return false;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Public control

        /// <summary>
        /// Another player's game is the one that runs this monster (shared rooms): false stops all thinking and
        /// moving here, the replicated position takes over. True takes the monster back, starting from Idle.
        /// </summary>
        public void SetAuthority(bool value)
        {
            if (value == hasAuthority)
                return;
            hasAuthority = value;

            if (!value)
            {
                AbortChase();
                state = State.Idle;
                senses.Scanning = false;
                if (agent.enabled)
                    agent.enabled = false;
            }
            else
            {
                agent.enabled = true;
                EnsureOnNavMesh();
                state = State.Idle;
                senses.Scanning = true;
                currentSpeed = baseChaseSpeed;
            }
        }

        /// <summary>Starts a chase on this target, as if a sense had noticed it. Ignored while already chasing.</summary>
        public bool StartChase(HuntTarget who)
        {
            return BeginChase(who, DetectionSource.Manual, true);
        }

        /// <summary>
        /// Drives the monster off (the flashlight's high beam): ends a chase, and no sense can start a new one for this
        /// many seconds. Calling it again while the beam stays on just extends the time.
        /// </summary>
        public void Repel(float seconds)
        {
            repelUntil = Mathf.Max(repelUntil, Time.time + seconds);
            if (hasAuthority && state == State.Chasing)
                FinishChase(EndReason.Manual);
        }

        /// <summary>Ends the current chase now (the monster goes home).</summary>
        public void EndChase()
        {
            if (state == State.Chasing)
                FinishChase(EndReason.Manual);
        }

        [ContextMenu("Test: Start chase on the local player")]
        void TestStartChase()
        {
            if (!Application.isPlaying)
                return;
            HuntTarget t = HuntTarget.EnsureLocalPlayer();
            if (t != null)
                StartChase(t);
            else
                Debug.LogWarning("MonsterHunter: no local player target yet.", this);
        }

        [ContextMenu("Test: End chase")]
        void TestEndChase()
        {
            EndChase();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Detection -> lock-on

        void OnDetected(HuntTarget who, DetectionSource source)
        {
            BeginChase(who, source, false);
        }

        void OnTargetDied(HuntTarget who)
        {
            if (state == State.Chasing && who == target)
                FinishChase(EndReason.TargetDead);
        }

        bool BeginChase(HuntTarget who, DetectionSource source, bool manual)
        {
            if (Time.time < repelUntil)
                return false;
            if (!hasAuthority || state == State.Chasing || who == null || !who.isActiveAndEnabled || who.IsDead)
                return false;
            if (!manual && who.IsSafe)
                return false;
            if (!EnsureOnNavMesh())
                return false;

            state = State.Chasing;
            settleFacing = false;
            target = who;
            targetPlayerId = who.playerId;
            lastSource = source;
            chaseTime = 0f;
            currentSpeed = baseChaseSpeed;
            caught = false;
            safeTimer = 0f;
            checkTimer = CheckInterval;       // check right away on the first frame
            unavailableTimer = 0f;
            hasGoal = false;
            goalReachable = true;
            stuckTimer = 0f;
            stuckRef = transform.position;
            lastKnownPosition = who.ChestPosition;
            hasKnownPosition = true;
            nextRepath = 0f;

            senses.Scanning = false;          // the lock does not depend on seeing or hearing anybody again

            agent.isStopped = false;
            agent.autoBraking = false;
            agent.stoppingDistance = Mathf.Max(0.5f, catchDistance * 0.75f);
            agent.speed = currentSpeed;
            RequestGoal(who.ChestPosition, true);

            if (logEvents)
                Debug.Log("MonsterHunter: chase started on player " + who.playerId + (who.isLocalPlayer ? " (local)" : "") + " - noticed by " + source + ".", this);

            ChaseStarted?.Invoke(who, source);
            onChaseStarted?.Invoke();
            return true;
        }

        void FinishChase(EndReason reason)
        {
            HuntTarget who = target;
            float lasted = chaseTime;
            AbortChase();

            state = returnHome ? State.Returning : State.Idle;
            returnStuckTimer = 0f;
            hasGoal = false;
            senses.Scanning = hasAuthority;

            if (state == State.Returning)
            {
                agent.autoBraking = true;
                agent.stoppingDistance = 0.3f;
                RequestHome();
            }
            else
            {
                StopMoving();
            }

            if (logEvents)
                Debug.Log("MonsterHunter: chase ended after " + lasted.ToString("0.0") + " s - " + reason + ".", this);

            ChaseEnded?.Invoke(who, reason);
            onChaseEnded?.Invoke();
        }

        /// <summary>Clears the lock without raising anything.</summary>
        void AbortChase()
        {
            target = null;
            targetPlayerId = -1;
            chaseTime = 0f;
            caught = false;
            safeTimer = 0f;
            unavailableTimer = 0f;
            hasKnownPosition = false;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Per frame

        void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (dt <= 0f)
                return;

            MeasureMotion(dt);

            if (!hasAuthority)
                return;

            switch (state)
            {
                case State.Chasing:
                    UpdateChase(dt);
                    break;
                case State.Returning:
                    UpdateReturn(dt);
                    break;
                default:
                    UpdateIdle(dt);
                    break;
            }

            ApplyRotation(dt);
        }

        void MeasureMotion(float dt)
        {
            Vector3 delta = transform.position - lastPosition;
            lastPosition = transform.position;
            Vector3 instant = delta / dt;
            if (instant.sqrMagnitude > 60f * 60f)
                instant = velocity;                 // a teleport, not movement

            float k = 1f - Mathf.Exp(-dt / 0.12f);
            velocity = Vector3.Lerp(velocity, instant, k);
            actualSpeed = new Vector2(velocity.x, velocity.z).magnitude;

            if (!isMoving && actualSpeed > 0.25f)
                isMoving = true;
            else if (isMoving && actualSpeed < 0.12f)
                isMoving = false;
        }

        void UpdateIdle(float dt)
        {
            if (wanderWhenIdle && !settleFacing && agent.enabled && agent.isOnNavMesh)
            {
                UpdateWander(dt);
                return;
            }

            // Settle: no speed left over from the last chase.
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, slowDownPerSecond * dt);
        }

        void UpdateWander(float dt)
        {
            if (wandering)
            {
                currentSpeed = Mathf.MoveTowards(currentSpeed, wanderSpeed, slowDownPerSecond * dt);
                ApplySpeed(dt, currentSpeed);

                bool done = !agent.pathPending && (!agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.3f);
                if (!done && !agent.pathPending && actualSpeed < 0.1f)
                {
                    wanderStuckTimer += dt;
                    done = wanderStuckTimer > 3f;
                }
                else if (actualSpeed >= 0.1f)
                {
                    wanderStuckTimer = 0f;
                }

                if (done)
                {
                    wandering = false;
                    wanderStuckTimer = 0f;
                    agent.ResetPath();
                    wanderTimer = UnityEngine.Random.Range(wanderPauseMin, Mathf.Max(wanderPauseMin, wanderPauseMax));
                }
                return;
            }

            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, slowDownPerSecond * dt);
            wanderTimer -= dt;
            if (wanderTimer > 0f)
                return;

            Vector2 offset = UnityEngine.Random.insideUnitCircle * wanderRadius;
            NavMeshHit hit;
            if (SampleNavMesh(home + new Vector3(offset.x, 0f, offset.y), agent.agentTypeID, maxSampleDistance, out hit))
            {
                agent.isStopped = false;
                if (agent.SetDestination(hit.position))
                {
                    wandering = true;
                    wanderStuckTimer = 0f;
                    return;
                }
            }
            wanderTimer = 2f;                       // no spot found: try again soon
        }

        // --- chasing

        void UpdateChase(float dt)
        {
            chaseTime += dt;

            // Is the target still there? A target that is only switched off is waited for; one that is gone ends the chase.
            bool exists = target != null;
            bool available = exists && target.isActiveAndEnabled;

            if (!exists && targetPlayerId >= 0 && Time.time >= nextRebind)
            {
                nextRebind = Time.time + 0.5f;
                HuntTarget again = HuntTarget.FindByPlayerId(targetPlayerId);   // the same player, respawned as a new object
                if (again != null)
                {
                    target = again;
                    exists = true;
                    available = again.isActiveAndEnabled;
                }
            }

            if (available)
            {
                unavailableTimer = 0f;
                lastKnownPosition = target.ChestPosition;
                hasKnownPosition = true;
            }
            else
            {
                unavailableTimer += dt;
                if (unavailableTimer >= unavailableGrace)
                {
                    FinishChase(EndReason.TargetLeft);
                    return;
                }
            }

            // Ending conditions, ten times a second.
            checkTimer += dt;
            if (available && checkTimer >= CheckInterval)
            {
                float elapsed = checkTimer;
                checkTimer = 0f;

                if (target.IsDead)
                {
                    FinishChase(EndReason.TargetDead);
                    return;
                }
                if (target.IsSafe)
                {
                    safeTimer += elapsed;
                    if (safeTimer >= safeConfirmTime)
                    {
                        FinishChase(EndReason.TargetSafe);
                        return;
                    }
                }
                else
                {
                    safeTimer = 0f;
                }
            }

            currentSpeed = StepSpeed(currentSpeed, chaseTime, dt, accelerationDelay, accelerationEaseIn, speedGainPerSecond, maxChaseSpeed);
            ApplySpeed(dt, currentSpeed);

            if (!hasKnownPosition)
                return;

            // Navigation: re-plan towards the target's real position, only when it has moved.
            Vector3 goal = lastKnownPosition;
            if (Time.time >= nextRepath && (!hasGoal || (goal - lastRequestedGoal).sqrMagnitude >= repathMinMove * repathMinMove || !goalReachable))
            {
                nextRepath = Time.time + repathInterval;
                RequestGoal(goal, false);
            }

            CheckCatch(available);
            CheckStuck(dt);
        }

        /// <summary>
        /// One step of the chase speed: nothing until <paramref name="delay"/> seconds have passed, then the gain eases
        /// in over <paramref name="easeIn"/> seconds. Depends only on elapsed time, never on the frame rate.
        /// </summary>
        public static float StepSpeed(float speed, float chaseTime, float dt, float delay, float easeIn, float gain, float max)
        {
            if (chaseTime > delay && gain > 0f)
            {
                float ease = easeIn > 0f ? Mathf.SmoothStep(0f, 1f, (chaseTime - delay) / easeIn) : 1f;
                speed += gain * ease * dt;
            }
            return Mathf.Min(speed, max);
        }

        void RequestGoal(Vector3 position, bool force)
        {
            if (!agent.isOnNavMesh)
                return;

            // The closest walkable ground to the player: right below them, or for a player up a tree or on a rock, as near as can be reached.
            NavMeshHit hit;
            if (SampleNavMesh(position, agent.agentTypeID, maxSampleDistance, out hit))
            {
                goalReachable = true;
                lastRequestedGoal = position;
                hasGoal = true;
                agent.SetDestination(hit.position);
            }
            else
            {
                // Over water or far up a cliff: keep the last route and try again soon. Not a reason to give up the chase.
                goalReachable = false;
                lastRequestedGoal = position;
                hasGoal = true;
            }
        }

        void CheckCatch(bool available)
        {
            if (!available || !hasKnownPosition)
                return;

            Vector3 chest = lastKnownPosition;
            Vector3 to = chest - transform.position;
            float flat = new Vector2(to.x, to.z).magnitude;
            bool inReach = flat <= catchDistance && to.y <= catchHeight;

            if (inReach && !caught)
            {
                caught = true;
                if (logEvents)
                    Debug.Log("MonsterHunter: caught player " + target.playerId + ".", this);
                TargetCaught?.Invoke(target);
                AnyTargetCaught?.Invoke(this, target);
                onTargetCaught?.Invoke();
                if (killOnCatch && target != null)
                    target.Kill();                      // ends the chase through HuntTarget.Died
            }
            else if (caught && flat > catchDistance * 1.6f)
            {
                caught = false;                         // got away; it can catch them again
            }
        }

        /// <summary>
        /// Raises the catch events on this peer for a player this peer's own game does not run the monster for: another
        /// player's game caught the local player (see <see cref="MonsterNetworkLink"/>). The same events as a local catch, so
        /// the blackout, the respawn and your own death logic react exactly as they do when this game's monster catches you.
        /// </summary>
        public void RaiseCaughtRemotely(HuntTarget who)
        {
            if (who == null)
                return;
            if (logEvents)
                Debug.Log("MonsterHunter: caught by the monster of another player (player " + who.playerId + ").", this);
            TargetCaught?.Invoke(who);
            AnyTargetCaught?.Invoke(this, who);
            onTargetCaught?.Invoke();
        }

        void CheckStuck(float dt)
        {
            if (!agent.isOnNavMesh || agent.pathPending)
            {
                stuckTimer = 0f;
                stuckRef = transform.position;
                return;
            }

            // Only counts when it still wants to go somewhere.
            bool wantsToMove = goalReachable && agent.remainingDistance > agent.stoppingDistance + 0.75f && !caught;
            if (!wantsToMove)
            {
                stuckTimer = 0f;
                stuckRef = transform.position;
                return;
            }

            stuckTimer += dt;
            if (stuckTimer < stuckSeconds)
                return;

            float moved = (transform.position - stuckRef).magnitude;
            stuckTimer = 0f;
            stuckRef = transform.position;
            if (moved >= 0.3f)
                return;

            // No progress: throw the route away and plan a new one.
            stuckCount++;
            agent.ResetPath();
            if (hasKnownPosition)
                RequestGoal(lastKnownPosition, true);
            if (Time.time >= nextStuckWarning)
            {
                nextStuckWarning = Time.time + 10f;
                Debug.LogWarning("MonsterHunter: '" + name + "' made no progress for " + stuckSeconds + " s at " + transform.position + "; re-planning its route (" + stuckCount + " times so far).", this);
            }
        }

        // --- returning

        void RequestHome()
        {
            if (!agent.isOnNavMesh)
                return;
            NavMeshHit hit;
            Vector3 goal = SampleNavMesh(home, agent.agentTypeID, maxSampleDistance, out hit) ? hit.position : home;
            agent.isStopped = false;
            agent.SetDestination(goal);
        }

        void UpdateReturn(float dt)
        {
            currentSpeed = Mathf.MoveTowards(currentSpeed, returnSpeed, slowDownPerSecond * dt);
            ApplySpeed(dt, currentSpeed);

            Vector3 flat = home - transform.position;
            flat.y = 0f;
            bool arrived = flat.magnitude <= arriveDistance;

            // The agent reached the end of its route (the spot on the NavMesh closest to home): that is home, even if it is not exactly the start position.
            if (!arrived && !agent.pathPending && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance <= agent.stoppingDistance + 0.1f && actualSpeed < 0.6f)
                arrived = true;

            // Stopped short of home for a long time (the route is blocked): good enough, stand here.
            if (!arrived && !agent.pathPending && agent.isOnNavMesh && actualSpeed < 0.1f)
            {
                returnStuckTimer += dt;
                if (returnStuckTimer > 4f)
                    arrived = true;
            }
            else
            {
                returnStuckTimer = 0f;
            }

            if (arrived)
            {
                state = State.Idle;
                settleFacing = true;
                StopMoving();
            }
        }

        void StopMoving()
        {
            if (agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();                  // the agent eases to a stop by itself
            }
        }

        // --- speed and turning

        void ApplySpeed(float dt, float desiredSpeed)
        {
            if (!agent.enabled)
                return;

            // Slow down while turned away from where it is going, so a sharp turn is a turn and not a sideways slide.
            float error = HeadingError();
            float wanted = Mathf.Lerp(1f, minSpeedWhenTurning, Mathf.InverseLerp(25f, 120f, error));
            turnFactor = Mathf.MoveTowards(turnFactor, wanted, 3f * dt);
            agent.speed = desiredSpeed * turnFactor;
        }

        bool TryGetHeading(out Vector3 heading)
        {
            heading = default;
            if (!agent.enabled || !agent.isOnNavMesh)
                return false;

            Vector3 v = agent.desiredVelocity;
            v.y = 0f;
            if (v.sqrMagnitude > 0.04f)
            {
                heading = v.normalized;
                return true;
            }

            // Arrived or waiting for a path: face the target (or home).
            if (state == State.Chasing && hasKnownPosition)
            {
                Vector3 to = lastKnownPosition - transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.25f)
                {
                    heading = to.normalized;
                    return true;
                }
            }
            return false;
        }

        float HeadingError()
        {
            Vector3 heading;
            if (!TryGetHeading(out heading))
                return 0f;
            return Vector3.Angle(body.FacingWorld, heading);
        }

        void ApplyRotation(float dt)
        {
            Quaternion wanted;
            Vector3 heading;
            bool settling = false;
            if (TryGetHeading(out heading))
            {
                wanted = body.RotationToFace(heading);
            }
            else if (state == State.Idle && settleFacing)
            {
                wanted = homeRotation;              // back home: turn to face the way it did before the chase
                settling = true;
            }
            else
            {
                return;
            }

            // Eased, with a turn-rate limit: smooth starts and stops, never a snap.
            float angle = Quaternion.Angle(transform.rotation, wanted);
            if (angle < 0.01f)
            {
                // (Only the turn home counts: on the frame it arrives the agent still reports last frame's velocity.)
                if (settling)
                    settleFacing = false;
                return;
            }
            float easeStep = angle * (1f - Mathf.Exp(-6f * dt));
            float step = Mathf.Min(turnSpeed * dt, Mathf.Max(easeStep, Mathf.Min(angle, 8f * dt)));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, wanted, step);
        }

        // ---------------------------------------------------------------------------------------------------------

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;

            Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.8f);
            Vector3 h = Application.isPlaying ? home : transform.position;
            Gizmos.DrawWireSphere(h, arriveDistance);
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, catchDistance);

#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                string label = state + (target != null ? " -> player " + target.playerId : "") + "\n" +
                               "speed " + actualSpeed.ToString("0.0") + " / " + currentSpeed.ToString("0.0") + " m/s";
                if (state == State.Chasing)
                    label += "  t=" + chaseTime.ToString("0") + " s";
                UnityEditor.Handles.Label(transform.position + Vector3.up * 6f, label);

                if (target != null)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawLine(transform.position + Vector3.up * 2f, target.ChestPosition);
                }
            }
#endif
        }
    }
}
