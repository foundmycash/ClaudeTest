using System.Collections;
using System.Collections.Generic;
using GorillaLocomotion;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// When the monster catches the local player they are "killed": the chase ends (HuntTarget.Kill), and while the
    /// screen is black (see <see cref="CatchBlackout"/>) the player is moved to a random <see cref="RespawnPoint"/>
    /// and brought back to life (HuntTarget.Revive) once the view has faded back in.
    ///
    /// Which point: random among the usable ones, never the one used last time, and never one closer than
    /// <see cref="minMonsterDistance"/> to the monster that caught you (if every point is closer, the farthest wins).
    /// With no RespawnPoint in the scene the player goes back to the forest's trailhead instead.
    ///
    /// Starts by itself, no setup. Put a PlayerRespawn on an object to change the numbers.
    /// </summary>
    [AddComponentMenu("Monster/Player Respawn")]
    public class PlayerRespawn : MonoBehaviour
    {
        static PlayerRespawn instance;

        [Tooltip("Seconds after the catch before the player is moved. The screen is black from the first instant, so this only lets the impact sound land first.")]
        [Min(0f)] public float moveDelay = 0.8f;

        [Tooltip("A respawn point closer than this to the monster (metres) is not used, so you do not come back next to it.")]
        [Min(0f)] public float minMonsterDistance = 60f;

        [Tooltip("Metres above the point the player is placed at (the rig drops to the ground).")]
        [Min(0f)] public float heightOffset = 0.1f;

        [Tooltip("Master switch.")]
        public bool enableRespawn = true;

        bool autoCreated;
        bool busy;
        RespawnPoint lastPoint;
        HuntTarget pending;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (instance != null || FindFirstObjectByType<PlayerRespawn>() != null)
                return;

            var host = new GameObject("Player Respawn");
            DontDestroyOnLoad(host);
            host.AddComponent<PlayerRespawn>().autoCreated = true;
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                if (instance.autoCreated)
                    Destroy(instance.gameObject);
                else
                {
                    Destroy(this);
                    return;
                }
            }
            instance = this;
        }

        void OnEnable()
        {
            MonsterHunter.AnyTargetCaught += OnCaught;
            CatchBlackout.BlackoutEnded += OnBlackoutEnded;
        }

        void OnDisable()
        {
            MonsterHunter.AnyTargetCaught -= OnCaught;
            CatchBlackout.BlackoutEnded -= OnBlackoutEnded;
            Revive();
            busy = false;
        }

        void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }

        void OnCaught(MonsterHunter monster, HuntTarget target)
        {
            if (!enableRespawn || busy || target == null || !target.isLocalPlayer)
                return;

            busy = true;
            pending = target;
            target.Kill();   // the monster stops hunting them

            bool hasMonster = monster != null;
            StartCoroutine(MoveAfterDelay(hasMonster ? monster.transform.position : Vector3.zero, hasMonster));
            StartCoroutine(SafetyRevive());
        }

        IEnumerator MoveAfterDelay(Vector3 monsterPosition, bool hasMonster)
        {
            for (float t = 0f; t < moveDelay; t += Time.unscaledDeltaTime)
                yield return null;

            Pose pose;
            if (TryPickPose(monsterPosition, hasMonster, out pose))
                Teleport(pose);
        }

        // If the blackout is off or failed it never reports its end; do not leave the player dead for ever.
        IEnumerator SafetyRevive()
        {
            CatchBlackout blackout = CatchBlackout.Instance;
            float wait = blackout != null && blackout.enableBlackout ? blackout.blackSeconds + blackout.fadeInSeconds + 2f : 1.5f;
            for (float t = 0f; t < wait; t += Time.unscaledDeltaTime)
                yield return null;

            OnBlackoutEnded();
        }

        void OnBlackoutEnded()
        {
            if (!busy)
                return;
            Revive();
            busy = false;
        }

        void Revive()
        {
            if (pending != null)
                pending.Revive();
            pending = null;
        }

        bool TryPickPose(Vector3 monsterPosition, bool hasMonster, out Pose pose)
        {
            var candidates = new List<RespawnPoint>();
            RespawnPoint farthest = null;
            float farthestDistance = -1f;

            foreach (RespawnPoint p in RespawnPoint.All)
            {
                if (p == null || !p.usable)
                    continue;

                float d = hasMonster ? Vector3.Distance(p.transform.position, monsterPosition) : float.MaxValue;
                if (d > farthestDistance)
                {
                    farthestDistance = d;
                    farthest = p;
                }
                if (d >= minMonsterDistance && p != lastPoint)
                    candidates.Add(p);
            }

            RespawnPoint chosen = null;
            if (candidates.Count > 0)
                chosen = candidates[Random.Range(0, candidates.Count)];
            else if (farthest != null)
                chosen = farthest;

            if (chosen != null)
            {
                lastPoint = chosen;
                pose = new Pose(chosen.transform.position + Vector3.up * heightOffset, chosen.transform.rotation);
                return true;
            }

            var forest = FindFirstObjectByType<HavocForest.ForestGenerator>();
            if (forest != null && forest.World != null && forest.World.data != null)
            {
                Debug.LogWarning("[PlayerRespawn] No RespawnPoint in the scene (Tools > Monster > Respawn Points); using the forest trailhead.", this);
                Pose spawn = forest.World.SpawnPose;
                pose = new Pose(spawn.position + Vector3.up * heightOffset, spawn.rotation);
                return true;
            }

            Debug.LogWarning("[PlayerRespawn] No RespawnPoint in the scene (Tools > Monster > Respawn Points); the player stays where they are.", this);
            pose = default(Pose);
            return false;
        }

        static void Teleport(Pose pose)
        {
            Player player = Player.Instance;
            if (player == null)
                return;

            Transform rig = player.transform;
            Vector3 forward = pose.rotation * Vector3.forward;
            forward.y = 0f;
            Quaternion yaw = forward.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : rig.rotation;

            rig.SetPositionAndRotation(pose.position, yaw);

            Rigidbody body = rig.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
            player.InitializeValues();   // forget the speed history so the jump is not mistaken for a huge throw
        }
    }
}
