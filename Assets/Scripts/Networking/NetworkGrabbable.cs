using Fusion;

using UnityEngine;

namespace GorillaNet
{
    /// <summary>
    /// An object any player can pick up with <see cref="NetworkHandGrabber"/> and throw, synced with Photon Fusion
    /// in Shared mode. Needs a Rigidbody, a NetworkObject (with "Allow State Authority Override" on) and a NetworkTransform.
    ///
    /// How it works: the player who holds (or last threw) the object has state authority, so they run its physics and
    /// NetworkTransform sends the result to everybody else. Grabbing asks for that authority first; the hold starts
    /// once Fusion hands it over. IsHeld / Holder are networked so everyone knows who has it.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkGrabbable : NetworkBehaviour, IStateAuthorityChanged
    {
        /// <summary>True while somebody has the object in their hand.</summary>
        [Networked] public NetworkBool IsHeld { get; set; }

        /// <summary>The player holding it (only meaningful while <see cref="IsHeld"/>).</summary>
        [Networked] public PlayerRef Holder { get; set; }

        [Header("Behaviour")]
        [Tooltip("If the object falls below this height it is sent back to where it started.")]
        public float resetBelowHeight = -50f;

        [Tooltip("Stop the thrown object from colliding with the local player's own head/body.")]
        public bool ignoreLocalPlayerBody = true;

        [Tooltip("How long to wait for Fusion to hand over state authority before giving up on a grab (seconds).")]
        public float requestTimeout = 1.5f;

        [Header("Debug tint")]
        [Tooltip("Colors the object by state so it is obvious in a test: free / held by you / held by someone else. Leave empty to use every renderer.")]
        public Renderer[] tintRenderers;
        public Color freeColor = new Color(1f, 0.55f, 0.1f);
        public Color heldByMeColor = new Color(0.2f, 0.9f, 0.3f);
        public Color heldByOtherColor = new Color(0.9f, 0.2f, 0.2f);

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        Rigidbody body;
        Collider[] colliders;
        NetworkTransform netTransform;
        MaterialPropertyBlock block;

        Vector3 homePosition;
        Quaternion homeRotation;

        // This peer's side of a grab.
        NetworkHandGrabber grabber;          // the hand holding it (only set on the peer that holds it)
        NetworkHandGrabber requester;        // the hand waiting for state authority
        float requestDeadline;
        Vector3 localOffset;                 // where the object sits relative to the hand
        Quaternion localRotation;

        bool collidersOn = true;
        int appliedTint = -1;
        float nextClaimTime;

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            colliders = GetComponentsInChildren<Collider>(true);
            block = new MaterialPropertyBlock();
            if (tintRenderers == null || tintRenderers.Length == 0)
                tintRenderers = GetComponentsInChildren<Renderer>(true);
            homePosition = transform.position;
            homeRotation = transform.rotation;
        }

        public override void Spawned()
        {
            netTransform = GetComponent<NetworkTransform>();

            var player = GorillaLocomotion.Player.Instance;
            if (ignoreLocalPlayerBody && player != null)
            {
                foreach (var c in colliders)
                {
                    if (player.headCollider != null) Physics.IgnoreCollision(c, player.headCollider);
                    if (player.bodyCollider != null) Physics.IgnoreCollision(c, player.bodyCollider);
                }
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (grabber != null)
                grabber.OnReleased(this);
            grabber = null;
            requester = null;
        }

        // ---- Grabbing (called by NetworkHandGrabber) ----

        /// <summary>False if the object isn't ready, is already in this player's other hand, or somebody else has it.</summary>
        public bool CanBeGrabbedBy(NetworkHandGrabber hand)
        {
            if (Object == null || !Object.IsValid)
                return false;
            if (grabber != null)
                return false;
            if (IsHeld && Holder != Runner.LocalPlayer && IsHolderPresent())
                return false;
            return true;
        }

        /// <summary>Starts a grab. Returns true if it started (the hold may begin a moment later, once authority arrives).</summary>
        public bool TryGrab(NetworkHandGrabber hand)
        {
            if (!CanBeGrabbedBy(hand))
                return false;

            if (Object.HasStateAuthority)
            {
                BeginHold(hand);
                return true;
            }

            if (!Object.RequestStateAuthority())
            {
                Debug.LogWarning($"{name}: state authority request was refused. Is 'Allow State Authority Override' on in its NetworkObject?", this);
                return false;
            }

            requester = hand;
            requestDeadline = Time.unscaledTime + requestTimeout;
            return true;
        }

        public void CancelPendingGrab(NetworkHandGrabber hand)
        {
            if (requester == hand)
                requester = null;
        }

        /// <summary>Lets go of the object and throws it with the given velocities (world space).</summary>
        public void Release(NetworkHandGrabber hand, Vector3 velocity, Vector3 angularVelocity)
        {
            if (grabber != hand)
                return;
            grabber = null;
            hand.OnReleased(this);

            if (!Object.HasStateAuthority)
                return;

            IsHeld = false;
            Holder = default;
            body.isKinematic = false;
            body.linearVelocity = velocity;
            body.angularVelocity = angularVelocity;
        }

        void BeginHold(NetworkHandGrabber hand)
        {
            if (!hand.isActiveAndEnabled)
                return;

            grabber = hand;
            Quaternion inverseHand = Quaternion.Inverse(hand.HandRotation);
            localOffset = inverseHand * (transform.position - hand.HandPosition);
            localRotation = inverseHand * transform.rotation;

            IsHeld = true;
            Holder = Runner.LocalPlayer;

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;

            hand.OnGrabbed(this);
        }

        // Fusion calls this when state authority moves to or away from this peer.
        public void StateAuthorityChanged()
        {
            if (Object.HasStateAuthority)
            {
                if (requester != null)
                {
                    var hand = requester;
                    requester = null;
                    BeginHold(hand);
                }
            }
            else if (grabber != null)
            {
                // Somebody else grabbed it out of our hand.
                grabber.OnReleased(this);
                grabber = null;
            }
        }

        // ---- Per-frame ----

        void LateUpdate()
        {
            if (grabber != null && Object.IsValid && Object.HasStateAuthority)
            {
                // Fusion can reset the rigidbody when authority arrives; keep it kinematic while held.
                if (!body.isKinematic)
                    body.isKinematic = true;

                Quaternion handRotation = grabber.HandRotation;
                transform.SetPositionAndRotation(grabber.HandPosition + handRotation * localOffset, handRotation * localRotation);
            }

            if (requester != null && Time.unscaledTime > requestDeadline)
                requester = null;
        }

        // Runs on the state authority only (Shared mode).
        public override void FixedUpdateNetwork()
        {
            if (IsHeld && !IsHolderPresent())
            {
                // The holder left the room with it in their hand.
                IsHeld = false;
                Holder = default;
                body.isKinematic = false;
            }

            if (!IsHeld && transform.position.y < resetBelowHeight)
                ResetToHome();
        }

        // Runs on every peer, every frame.
        public override void Render()
        {
            // When the player who owns the object leaves the room, nobody owns it any more and it would hang frozen
            // in mid-air. The room's master client picks it up; FixedUpdateNetwork then lets go of a dead hold.
            if (Object.StateAuthority == PlayerRef.None && Runner.IsSharedModeMasterClient && Time.unscaledTime >= nextClaimTime)
            {
                nextClaimTime = Time.unscaledTime + 1f;
                Object.RequestStateAuthority();
            }

            bool held = IsHeld;

            // A held object has no collider, so it can't bump anyone or be pushed against (or grabbed twice).
            if (collidersOn == held)
            {
                collidersOn = !held;
                foreach (var c in colliders)
                    c.enabled = collidersOn;
            }

            int tint = !held ? 0 : (Holder == Runner.LocalPlayer ? 1 : 2);
            if (tint != appliedTint)
                ApplyTint(tint);
        }

        void ResetToHome()
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            if (netTransform != null)
                netTransform.Teleport(homePosition, homeRotation);
            else
                transform.SetPositionAndRotation(homePosition, homeRotation);
        }

        bool IsHolderPresent()
        {
            foreach (var player in Runner.ActivePlayers)
            {
                if (player == Holder)
                    return true;
            }
            return false;
        }

        void ApplyTint(int state)
        {
            appliedTint = state;
            Color color = state == 0 ? freeColor : (state == 1 ? heldByMeColor : heldByOtherColor);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            foreach (var r in tintRenderers)
            {
                if (r != null)
                    r.SetPropertyBlock(block);
            }
        }
    }
}
