using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// A player the monster is allowed to hunt. This is the one place the hunting AI learns about players, so the AI
    /// never depends on how a player happens to be built.
    ///
    /// - The local player is the Gorilla rig. A HuntTarget is put on it by itself (see <see cref="EnsureLocalPlayer"/>).
    /// - Other players in the room are their networked avatars (<c>GorillaNet.NetworkAvatar</c>); <see cref="MonsterNetworkLink"/>
    ///   puts a HuntTarget on each one while the monster is networked.
    ///
    /// It also carries the two conditions that end a chase:
    /// - Safe: standing in a <see cref="SafeZone"/>, or <see cref="forceSafe"/> set by your own game code.
    /// - Dead: <see cref="Kill"/> was called. There is no death or health system in the project yet, so connect yours
    ///   to <see cref="Kill"/> / <see cref="Revive"/> (or to the monster's "On Target Caught" event, see MonsterHunter).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Monster/Hunt Target")]
    public class HuntTarget : MonoBehaviour
    {
        static readonly List<HuntTarget> active = new List<HuntTarget>();

        /// <summary>Every enabled target right now. Read it by index, it is not copied.</summary>
        public static IReadOnlyList<HuntTarget> Active => active;

        /// <summary>The target of the player holding this headset (null before the Gorilla rig exists).</summary>
        public static HuntTarget Local { get; private set; }

        /// <summary>Raised when any target is killed, so a chase on it can end the same frame.</summary>
        public static event Action<HuntTarget> Died;

        [Tooltip("The head: where the monster looks at and listens to. Empty = 1.6 m above this object.")]
        public Transform head;

        [Tooltip("The player's own body: colliders under it never block the monster's view of the player. Empty = this object. For the Gorilla rig this is the rig (hands and head included).")]
        public Transform bodyRoot;

        [Tooltip("How far below the head the chest is, in metres. The monster looks at the head first and the chest second, so a player peeking out from behind a tree is still seen.")]
        [Min(0f)] public float chestBelowHead = 0.45f;

        [Tooltip("The player number in the room (Photon player id). -1 = the local player / unknown.")]
        public int playerId = -1;

        [Tooltip("True for the player holding this headset.")]
        public bool isLocalPlayer;

        [Tooltip("Treats this player as safe no matter where they are. For your own game code (a respawn lobby, a tagged-out state). A SafeZone volume does not need it.")]
        public bool forceSafe;

        /// <summary>True after <see cref="Kill"/>, until <see cref="Revive"/>.</summary>
        public bool IsDead { get; private set; }

        /// <summary>Safe = in a SafeZone volume (chest height) or <see cref="forceSafe"/>. Looked up on demand; keep it to a few times a second.</summary>
        public bool IsSafe => forceSafe || SafeZone.IsInsideAny(ChestPosition);

        /// <summary>Alive, safe nowhere, and enabled: something the monster may start hunting.</summary>
        public bool IsHuntable => isActiveAndEnabled && !IsDead && !IsSafe;

        public Vector3 HeadPosition => head != null ? head.position : transform.position + Vector3.up * 1.6f;
        public Vector3 ChestPosition => HeadPosition + Vector3.down * chestBelowHead;

        /// <summary>The transform whose colliders belong to the player (never block the monster's sight).</summary>
        public Transform BodyRoot => bodyRoot != null ? bodyRoot : transform;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode without a domain reload keeps statics from the last run.
            active.Clear();
            Local = null;
            Died = null;
        }

        void OnEnable()
        {
            if (!active.Contains(this))
                active.Add(this);
            if (isLocalPlayer)
                Local = this;
        }

        void OnDisable()
        {
            active.Remove(this);
            if (Local == this)
                Local = null;
        }

        /// <summary>The player is dead. Every monster hunting this target stops the chase.</summary>
        [ContextMenu("Test: Kill")]
        public void Kill()
        {
            if (IsDead)
                return;
            IsDead = true;
            Died?.Invoke(this);
        }

        /// <summary>The player is alive again (a respawn). They can be detected and hunted again.</summary>
        [ContextMenu("Test: Revive")]
        public void Revive()
        {
            IsDead = false;
        }

        /// <summary>Makes this target the local player's. Done for you by <see cref="EnsureLocalPlayer"/>.</summary>
        public void MarkLocalPlayer(Transform headTransform, Transform body)
        {
            isLocalPlayer = true;
            head = headTransform;
            bodyRoot = body;
            if (isActiveAndEnabled)
                Local = this;
        }

        /// <summary>The target with this player id, or null. A linear search over a handful of players.</summary>
        public static HuntTarget FindByPlayerId(int id)
        {
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].playerId == id && !active[i].isLocalPlayer)
                    return active[i];
            }
            return Local != null && Local.playerId == id ? Local : null;
        }

        /// <summary>
        /// Returns the local player's target, putting one on the Gorilla rig if it has none yet. Null while there is
        /// no <c>GorillaLocomotion.Player</c> (the rig is not loaded yet); ask again later.
        /// </summary>
        public static HuntTarget EnsureLocalPlayer()
        {
            if (Local != null)
                return Local;

            GorillaLocomotion.Player player = GorillaLocomotion.Player.Instance;
            if (player == null)
                return null;

            HuntTarget target = player.GetComponent<HuntTarget>();
            if (target == null)
                target = player.gameObject.AddComponent<HuntTarget>();

            Transform headTransform = player.headCollider != null ? player.headCollider.transform : null;
            // The hand followers may sit next to the player object under the rig, so the whole rig counts as the body.
            Transform rig = player.transform.parent != null ? player.transform.parent : player.transform;
            target.MarkLocalPlayer(headTransform, rig);
            return target;
        }
    }
}
