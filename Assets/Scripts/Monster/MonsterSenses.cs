using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>What made the monster notice a player.</summary>
    public enum DetectionSource
    {
        /// <summary>It saw the player: inside its view, in range, with a clear line.</summary>
        Sight,
        /// <summary>The player came within the proximity radius (walls do not matter).</summary>
        Proximity,
        /// <summary>The player made a loud noise into the microphone.</summary>
        Sound,
        /// <summary>Not a sense: the chase was started by code (<see cref="MonsterHunter.StartChase"/>).</summary>
        Manual,
    }

    /// <summary>
    /// Decides WHEN the monster starts hunting, and nothing else. Three independent senses; any one of them is enough:
    ///
    /// - Sight: the player is inside the view distance and field of view, and a raycast from the eyes to their head (or
    ///   chest) is not blocked by the world. Trees, rocks, ground and walls block it; the monster's own body and the
    ///   player's own colliders never do. Checked a few times a second, only for players already in range and in view.
    /// - Proximity: the player is inside the proximity radius, seen or not. Just a distance check, no physics.
    /// - Sound: the local player's microphone picked up a loud noise (see <see cref="MicrophoneNoiseDetector"/>), or a
    ///   remote player's noise was reported with <see cref="ReportNoise"/>. If there is no microphone this sense is
    ///   simply silent.
    ///
    /// When one of them fires, <see cref="Detected"/> is raised with the player and the sense. What happens next
    /// (locking on, chasing, giving up) is the <see cref="MonsterHunter"/>'s business: it switches this script off
    /// with <see cref="Scanning"/> while a chase lasts, so a chase never depends on seeing or hearing anyone again.
    ///
    /// Players that are dead or in a safe zone are invisible to all three senses. Players are found through
    /// <see cref="HuntTarget"/> (no scene searches, no layers to set up); the player's own colliders are skipped by
    /// hierarchy, not by layer, because in this project the player and the world share the Default layer.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MonsterBody))]
    [DefaultExecutionOrder(-60)]
    [AddComponentMenu("Monster/Monster Senses")]
    public class MonsterSenses : MonoBehaviour
    {
        [Header("Sight (raycast)")]
        public bool useSight = true;

        [Tooltip("Furthest distance in metres at which it can see a player (default 25: the forest is foggy, 60 felt like it saw through the whole map). Called View Distance before; scenes that still hold the old 60 simply use this value.")]
        [Min(0f)] public float sightDistance = 25f;

        [Tooltip("Width of its view in degrees, centred on the way the model faces.")]
        [Range(20f, 360f)] public float fieldOfView = 140f;

        [Tooltip("Height of its view in degrees (half up, half down).")]
        [Range(20f, 180f)] public float verticalFieldOfView = 120f;

        [Tooltip("Layers that block its view: trees, rocks, ground, walls. Trigger colliders never block. The monster's own colliders and the player's never block either, whatever layer they are on.")]
        public LayerMask obstacleLayers = Physics.DefaultRaycastLayers & ~(1 << 5);

        [Tooltip("Seconds between sight checks. They only cost anything for players that are in range and in view, so 0.15 is cheap.")]
        [Range(0.05f, 1f)] public float sightInterval = 0.15f;

        [Header("Proximity")]
        public bool useProximity = true;

        [Tooltip("A player closer than this many metres is noticed even behind a wall or behind its back. Measured from the middle of its body.")]
        [Min(0f)] public float proximityRadius = 10f;

        [Tooltip("Height above the monster's feet the proximity sphere is centred on.")]
        [Min(0f)] public float proximityCenterHeight = 1.5f;

        [Tooltip("Seconds between proximity checks (just distances).")]
        [Range(0.02f, 1f)] public float proximityInterval = 0.1f;

        [Header("Sound (microphone)")]
        public bool useSound = true;

        /// <summary>The hearing range used when the field below is 0 (older scenes saved 0, which used to mean "anywhere").</summary>
        public const float DefaultHearingRange = 75f;

        [Tooltip("How far the monster can hear, in metres. A noise only counts if the player is within this many metres, and it is also the radius in which the monster's screech and footsteps can be heard (see MonsterScreech, MonsterFootsteps). 0 = the default of 75 m.")]
        [Min(0f)] public float hearingRange = DefaultHearingRange;

        /// <summary>How far it can hear, and how far its screech carries, in metres.</summary>
        public float HearingRange => hearingRange > 0f ? hearingRange : DefaultHearingRange;

        [Header("Start-up")]
        [Tooltip("Seconds after the monster wakes up (the scene loads) in which it notices nobody. The player is still being placed, the loading transition is still playing and the microphone is still settling; none of that is the player being found.")]
        [Min(0f)] public float startupGraceSeconds = 6f;

        [Header("Debug")]
        [Tooltip("Draw the view, proximity radius and hearing range in the Scene view while the monster is selected.")]
        public bool drawGizmos = true;

        /// <summary>Raised when a sense notices a huntable player while <see cref="Scanning"/>.</summary>
        public event Action<HuntTarget, DetectionSource> Detected;

        /// <summary>The Hunter turns this off for the whole length of a chase, so no sight or proximity work is done meanwhile.</summary>
        public bool Scanning { get; set; } = true;

        public MonsterBody Body => body;

        MonsterBody body;
        MicrophoneNoiseDetector microphone;
        float nextSight;
        float nextProximity;
        float nextLocalLookup;
        float enabledAt;
        bool warnedNoPlayer;

        void Awake()
        {
            body = GetComponent<MonsterBody>();
            // Spread the checks of several monsters over different frames.
            nextSight = Time.time + UnityEngine.Random.value * sightInterval;
            nextProximity = Time.time + UnityEngine.Random.value * proximityInterval;

            // The screech needs no setup: it is added next to the senses (it reads the hearing range from here).
            if (GetComponent<MonsterScreech>() == null)
                gameObject.AddComponent<MonsterScreech>();
        }

        void OnEnable()
        {
            enabledAt = Time.time;
            if (useSound)
            {
                microphone = MicrophoneNoiseDetector.EnsureExists();
                microphone.NoiseDetected += OnNoise;
                microphone.StartListening();
            }
        }

        void OnDisable()
        {
            if (microphone != null)
                microphone.NoiseDetected -= OnNoise;
            microphone = null;
        }

        void Update()
        {
            if (!Scanning)
                return;

            float now = Time.time;

            // The Gorilla rig may load after the monster; keep asking, rarely, until it is there.
            if (HuntTarget.Local == null && now >= nextLocalLookup)
            {
                nextLocalLookup = now + 1f;
                HuntTarget.EnsureLocalPlayer();

                if (!warnedNoPlayer && HuntTarget.Active.Count == 0 && now - enabledAt > 20f)
                {
                    warnedNoPlayer = true;
                    Debug.LogWarning("MonsterSenses on '" + name + "' has found nobody to hunt after 20 s: there is no GorillaLocomotion.Player in the scene (it gets a HuntTarget by itself) and no other HuntTarget. Nothing can be detected until one exists.", this);
                }
            }

            if (useProximity && now >= nextProximity)
            {
                nextProximity = now + proximityInterval;
                ScanProximity();
                if (!Scanning)
                    return;
            }

            if (useSight && now >= nextSight)
            {
                nextSight = now + sightInterval * UnityEngine.Random.Range(0.8f, 1.2f);
                ScanSight();
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Sound

        void OnNoise(float levelDb)
        {
            if (!Scanning || !useSound)
                return;
            ReportNoise(HuntTarget.Local);
        }

        /// <summary>A player made a loud noise. Used for the local player's microphone, and by the network link for remote players.</summary>
        public void ReportNoise(HuntTarget player)
        {
            if (!Scanning || !useSound || player == null || !player.IsHuntable)
                return;
            float range = HearingRange;
            if ((player.HeadPosition - transform.position).sqrMagnitude > range * range)
                return;
            Raise(player, DetectionSource.Sound);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Proximity

        void ScanProximity()
        {
            Vector3 center = transform.position + Vector3.up * proximityCenterHeight;
            float limit = proximityRadius * proximityRadius;
            HuntTarget best = null;
            float bestSq = float.MaxValue;

            IReadOnlyList<HuntTarget> all = HuntTarget.Active;
            for (int i = 0; i < all.Count; i++)
            {
                HuntTarget t = all[i];
                float sq = (t.ChestPosition - center).sqrMagnitude;
                if (sq <= limit && sq < bestSq && t.IsHuntable)
                {
                    best = t;
                    bestSq = sq;
                }
            }

            if (best != null)
                Raise(best, DetectionSource.Proximity);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Sight

        void ScanSight()
        {
            Vector3 eye = body.EyePosition;
            Vector3 forward = body.FacingWorld;
            HuntTarget best = null;
            float bestSq = float.MaxValue;

            IReadOnlyList<HuntTarget> all = HuntTarget.Active;
            for (int i = 0; i < all.Count; i++)
            {
                HuntTarget t = all[i];
                float sq = (t.HeadPosition - eye).sqrMagnitude;
                if (sq >= bestSq || sq > sightDistance * sightDistance)
                    continue;
                if (!t.IsHuntable || !IsInView(t.HeadPosition - eye, forward))
                    continue;
                if (!HasClearLine(eye, t.HeadPosition, t) && !HasClearLine(eye, t.ChestPosition, t))
                    continue;
                best = t;
                bestSq = sq;
            }

            if (best != null)
                Raise(best, DetectionSource.Sight);
        }

        /// <summary>True if the player is in range, inside the view and has a clear line from the eyes. For debugging and tests; the monster does the same on its own.</summary>
        public bool CanSee(HuntTarget t)
        {
            if (t == null || body == null)
                return false;
            Vector3 eye = body.EyePosition;
            Vector3 to = t.HeadPosition - eye;
            if (to.sqrMagnitude > sightDistance * sightDistance || !IsInView(to, body.FacingWorld))
                return false;
            return HasClearLine(eye, t.HeadPosition, t) || HasClearLine(eye, t.ChestPosition, t);
        }

        bool IsInView(Vector3 to, Vector3 forward)
        {
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            float flatLength = flat.magnitude;
            if (flatLength < 0.1f)
                return true;                                    // right above or below: inside the cone, the vertical limit does not matter that close
            if (Vector3.Angle(forward, flat) > fieldOfView * 0.5f)
                return false;
            float pitch = Mathf.Atan2(to.y, flatLength) * Mathf.Rad2Deg;
            return Mathf.Abs(pitch) <= verticalFieldOfView * 0.5f;
        }

        const int MaxSkips = 6;

        /// <summary>
        /// A ray from the eyes to the point, skipping the monster's own colliders and the player's. Casting again from
        /// past each skipped collider (instead of collecting every hit in a buffer) means a dense forest cannot
        /// overflow anything and the first real obstacle is always the one found.
        /// </summary>
        bool HasClearLine(Vector3 from, Vector3 to, HuntTarget target)
        {
            Vector3 dir = to - from;
            float remaining = dir.magnitude;
            if (remaining < 0.05f)
                return true;
            dir /= remaining;

            Vector3 origin = from;
            for (int i = 0; i < MaxSkips; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, dir, out hit, remaining, obstacleLayers, QueryTriggerInteraction.Ignore))
                    return true;
                if (!IsOwnCollider(hit.collider.transform, target))
                    return false;

                float advance = hit.distance + 0.02f;
                origin += dir * advance;
                remaining -= advance;
                if (remaining <= 0.02f)
                    return true;
            }
            return false;
        }

        bool IsOwnCollider(Transform hit, HuntTarget target)
        {
            if (hit.IsChildOf(transform))
                return true;                                    // the monster's own body
            if (hit.IsChildOf(target.BodyRoot))
                return true;                                    // the player being looked at
            HuntTarget local = HuntTarget.Local;
            return local != null && hit.IsChildOf(local.BodyRoot);   // this headset's player never hides anybody
        }

        void Raise(HuntTarget target, DetectionSource source)
        {
            if (Time.time - enabledAt < startupGraceSeconds)
                return;
            Detected?.Invoke(target, source);
        }

        // ---------------------------------------------------------------------------------------------------------

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;

            MonsterBody b = body != null ? body : GetComponent<MonsterBody>();
            Vector3 eye = b != null ? b.EyePosition : transform.position + Vector3.up * 2.3f;
            Vector3 forward = b != null ? b.FacingWorld : transform.forward;

            if (useProximity)
            {
                Gizmos.color = new Color(1f, 0.35f, 0.2f, 0.9f);
                Gizmos.DrawWireSphere(transform.position + Vector3.up * proximityCenterHeight, proximityRadius);
            }

            if (useSight)
            {
                Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.9f);
                float half = fieldOfView * 0.5f;
                float length = sightDistance;
                Vector3 left = Quaternion.AngleAxis(-half, Vector3.up) * forward;
                Vector3 right = Quaternion.AngleAxis(half, Vector3.up) * forward;
                Gizmos.DrawRay(eye, left * length);
                Gizmos.DrawRay(eye, right * length);
                Gizmos.DrawRay(eye, forward * length);
                const int Steps = 24;
                Vector3 previous = eye + left * length;
                for (int i = 1; i <= Steps; i++)
                {
                    Vector3 dir = Quaternion.AngleAxis(-half + fieldOfView * i / Steps, Vector3.up) * forward;
                    Vector3 point = eye + dir * length;
                    Gizmos.DrawLine(previous, point);
                    previous = point;
                }
            }

            if (useSound)
            {
                Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.6f);
                Gizmos.DrawWireSphere(transform.position, HearingRange);
            }
        }
    }
}
