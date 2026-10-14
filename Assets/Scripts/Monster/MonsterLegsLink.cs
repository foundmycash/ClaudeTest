using System;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// The only script that knows about the Legs Animator. It does three things:
    ///
    /// - Tells the Legs Animator whether the monster is walking (it has no animator or rigidbody to find out by
    ///   itself, so without this its legs never step while the NavMesh agent glides the body along).
    /// - Crawl gait (<see cref="crawlGait"/>): makes the arms take real, lifted steps instead of sliding. See below.
    /// - Raises <see cref="Stepped"/> each time a hand is planted, with its position, which
    ///   <see cref="MonsterFootsteps"/> turns into a footstep sound that lands exactly when the hand does.
    ///
    /// Why the crawl gait: the monster has no walk animation, and the Legs Animator's default "Automatic" glue mode
    /// switches to "Moving" glue while the monster walks. Moving glue assumes a walk clip is lifting the feet and only
    /// fixes them to the ground, so with no clip the hands just slid along the ground in tiny, flat re-plants (about
    /// 2 cm of lift): the monster scooted. "Idle" glue does the real thing: the hand lifts off behind the body, swings
    /// forward in an arc and lands ahead, and the other arm waits until it has landed (so the arms alternate).
    /// On top of that, what a fixed setting cannot do for a monster that runs 3.5 to 8 m/s: the swing time shrinks with
    /// speed (otherwise the stride keeps growing until the arms are stretched out straight behind the body), and the
    /// hands land a little ahead of the body (capped, so the arms never reach their limit). The hands' ground check also
    /// starts higher than the plugin default, so a hand does not hang in the air where a hillside beside the body is
    /// higher than the hips.
    ///
    /// Everything it changes on the Legs Animator is applied at start and undone if you switch the option off (also
    /// while playing, to compare); the Legs Animator component in the scene is never edited. It does nothing if there
    /// is no Legs Animator. Delete this one file if you ever remove the Legs Animator package.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-40)]
    [AddComponentMenu("Monster/Monster Legs Link")]
    public class MonsterLegsLink : MonoBehaviour, LegsAnimator.ILegStepReceiver
    {
        [Header("Crawl gait")]
        [Tooltip("ON: the arms take lifted, alternating steps (settings below). OFF: the Legs Animator is left exactly as set up in the scene, which makes the monster scoot because it has no walk animation. Can be toggled while playing.")]
        public bool crawlGait = true;

        [Tooltip("How high a hand rises when it swings forward, for a short step. (Legs Animator: Min Foot Raise, default 0.1. Scaled with the monster's size.)")]
        [Range(0f, 1f)] public float minHandRaise = 0.2f;

        [Tooltip("How high a hand rises for a long step (Legs Animator: Max Foot Raise, default 0.4). Higher = bigger, slower looking arcs. The peak of the arc is roughly half of this in metres (0.7 lifts a hand about 0.35 m).")]
        [Range(0f, 1.5f)] public float maxHandRaise = 0.7f;

        [Tooltip("Swing time of a hand = this / speed (seconds x m/s), limited by the range below. 1.4 gives 0.4 s at 3.5 m/s and 0.175 s at 8 m/s. Bigger = slower, more deliberate arm movement, but the arms stretch further behind the body at speed; smaller = faster flapping with shorter strides.")]
        [Min(0.1f)] public float swingTimeTimesSpeed = 1.4f;

        [Tooltip("Shortest and longest swing time in seconds (Legs Animator: Step Move Duration, default 0.375). Also used while the monster stands or turns on the spot.")]
        public Vector2 swingTimeRange = new Vector2(0.15f, 0.4f);

        [Tooltip("How far ahead of its body a hand lands: metres per m/s of speed. 0 = the Legs Animator default (hands land under the body and trail far behind it).")]
        [Min(0f)] public float reachPerSpeed = 0.12f;

        [Tooltip("The reach never exceeds this many metres, so the arms stay bent at full speed.")]
        [Min(0f)] public float maxReach = 0.45f;

        [Tooltip("Where each hand's ground check starts, as a multiple of the monster's hip height (Legs Animator: Raycast Start Height Mul, default 1). On a hillside the ground beside the body can be higher than the hips; the check then finds nothing and the hand hangs in the air for a moment. 1.6 fixed that on the forest slopes; raise it if a hand still hangs somewhere, 1 = the Legs Animator default.")]
        [Range(1f, 2.5f)] public float groundCheckHeight = 1.6f;

        /// <summary>A hand was planted: where, how hard (0..1+), and whether it was the right hand.</summary>
        public event Action<Vector3, float, bool> Stepped;

        /// <summary>How many steps have been reported so far.</summary>
        public int StepCount { get; private set; }

        /// <summary>Time.time of the latest step.</summary>
        public float LastStepTime { get; private set; } = -999f;

        public bool HasLegs => legs != null;

        LegsAnimator legs;
        MonsterHunter hunter;

        // what the crawl gait changed, so it can be undone
        bool crawlApplied;
        LegsAnimator.EGlueMode originalGlueMode;
        float originalMinRaise, originalMaxRaise, originalStepDuration, originalGroundCheckHeight;
        LegsAnimator.LegsAnimatorCustomModuleHelper reachModule;      // the module this script added (null if the scene had its own)
        LegsAnimator.LegsAnimatorCustomModuleHelper reachHelper;      // the one in use
        bool[] wasPlanted;

        void Awake()
        {
            legs = GetComponent<LegsAnimator>();
            hunter = GetComponent<MonsterHunter>();
            if (legs == null)
                return;

            // Must be set before the Legs Animator starts (it decides at start whether to send step events).
            if (legs.StepInfoReceiver == null)
                legs.StepInfoReceiver = transform;
            legs.InitializeGetStepInfoReceiver();

            if (crawlGait)
                ApplyCrawlGait();
        }

        void OnDestroy()
        {
            if (crawlApplied)
                RestoreLegs();
        }

        void Update()
        {
            if (legs == null || hunter == null)
                return;

            if (crawlGait != crawlApplied)
            {
                if (crawlGait) ApplyCrawlGait();
                else RestoreLegs();
            }

            legs.User_SetIsMoving(hunter.IsMoving);
            float speed = hunter.ActualSpeed;

            if (crawlApplied)
            {
                // Faster monster, quicker swings: the stride stays the same size instead of growing with speed.
                float lo = Mathf.Min(swingTimeRange.x, swingTimeRange.y), hi = Mathf.Max(swingTimeRange.x, swingTimeRange.y);
                legs.BaseLegAnimating.StepMoveDuration = Mathf.Clamp(swingTimeTimesSpeed / Mathf.Max(0.5f, speed), lo, hi);
            }

            if (hunter.IsMoving)
            {
                // The Step Further module reads this as a velocity: its length is how far ahead (in metres) the hands land.
                Vector3 dir = hunter.Velocity.normalized;
                float reach = crawlApplied ? Mathf.Min(reachPerSpeed * speed, maxReach) : 1f;
                legs.User_SetDesiredMovementDirection(dir * reach);
            }

            if (crawlApplied)
                DetectPlants();
        }

        void ApplyCrawlGait()
        {
            if (legs == null || crawlApplied)
                return;

            originalGlueMode = legs.GlueMode;
            originalMinRaise = legs.BaseLegAnimating.MinFootRaise;
            originalMaxRaise = legs.BaseLegAnimating.MaxFootRaise;
            originalStepDuration = legs.BaseLegAnimating.StepMoveDuration;
            originalGroundCheckHeight = legs.RaycastStartHeightMul;

            legs.GlueMode = LegsAnimator.EGlueMode.Idle;
            legs.BaseLegAnimating.MinFootRaise = minHandRaise;
            legs.BaseLegAnimating.MaxFootRaise = Mathf.Max(minHandRaise, maxHandRaise);
            legs.RaycastStartHeightMul = groundCheckHeight;

            // The "Step Further" module moves where a hand lands by the movement direction it is given. The scene's own one is used if
            // there is one; otherwise one is added here (the plugin prepares it when it starts, or right now if it already has).
            reachHelper = legs.GetModuleHelper<LAM_StepFurther>();
            if (reachHelper == null)
            {
                var module = ScriptableObject.CreateInstance<LAM_StepFurther>();
                module.name = "Monster Reach (added by Monster Legs Link)";
                reachModule = new LegsAnimator.LegsAnimatorCustomModuleHelper(legs) { ModuleReference = module, Enabled = true };
                legs.CustomModules.Add(reachModule);
                if (legs.LegsInitialized)
                    reachModule.PreparePlaymodeModule(legs);
                reachHelper = reachModule;
            }
            // 1 = the length of the direction we hand over is the reach in metres
            reachHelper.RequestVariable("Predict Forward Offset", 0.1f).SetValue(1f);

            wasPlanted = null;
            crawlApplied = true;
        }

        void RestoreLegs()
        {
            if (legs == null || !crawlApplied)
                return;

            legs.GlueMode = originalGlueMode;
            legs.BaseLegAnimating.MinFootRaise = originalMinRaise;
            legs.BaseLegAnimating.MaxFootRaise = originalMaxRaise;
            legs.BaseLegAnimating.StepMoveDuration = originalStepDuration;
            legs.RaycastStartHeightMul = originalGroundCheckHeight;

            if (reachModule != null)
            {
                legs.CustomModules.Remove(reachModule);
                reachModule.DisposeModule();
                if (reachModule.ModuleReference != null)
                    Destroy(reachModule.ModuleReference);
                reachModule = null;
            }
            reachHelper = null;
            crawlApplied = false;
        }

        // A hand counts as planted while the Legs Animator has it attached to the ground. Reading it here (rather than waiting for the
        // plugin's own step events, which only fire occasionally for a creature with no walk clip) makes each sound land on a hand.
        void DetectPlants()
        {
            if (!legs.LegsInitialized)
                return;

            int n = legs.Legs.Count;
            if (wasPlanted == null || wasPlanted.Length != n)
            {
                wasPlanted = new bool[n];
                for (int i = 0; i < n; i++)
                    wasPlanted[i] = legs.Legs[i].G_Attached;
                return;
            }

            for (int i = 0; i < n; i++)
            {
                LegsAnimator.Leg leg = legs.Legs[i];
                bool planted = leg.G_Attached;
                if (planted && !wasPlanted[i] && hunter.IsMoving)
                    RaiseStep(leg.BoneEnd.position, 1f, leg.Side == LegsAnimator.ELegSide.Right);
                wasPlanted[i] = planted;
            }
        }

        void RaiseStep(Vector3 position, float power, bool isRight)
        {
            StepCount++;
            LastStepTime = Time.time;
            Stepped?.Invoke(position, power, isRight);
        }

        // ILegStepReceiver: the plugin's own step events (used when the crawl gait is off, e.g. if you give the monster a walk animation)
        public void LegAnimatorStepEvent(LegsAnimator.Leg leg, float power, bool isRight, Vector3 position, Quaternion rotation, LegsAnimator.EStepType type)
        {
            if (crawlApplied)
                return;
            RaiseStep(position, power, isRight);
        }
    }
}
