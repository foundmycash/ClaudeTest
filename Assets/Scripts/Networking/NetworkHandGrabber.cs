using UnityEngine;
using UnityEngine.XR;

namespace GorillaNet
{
    /// <summary>
    /// One per hand. Squeeze the grip near a <see cref="NetworkGrabbable"/> to pick it up, let go to throw it.
    /// Local only (not networked): it just asks the grabbable to take over, and the grabbable does the syncing.
    /// Reads the grip through UnityEngine.XR (same source as the rig's XRController, works with OpenXR and Oculus).
    /// </summary>
    public class NetworkHandGrabber : MonoBehaviour
    {
        [Header("Hand")]
        public XRNode hand = XRNode.RightHand;

        [Tooltip("Where the hand is in the world (the rig's hand follower, so held objects sit where the visible hand is). Defaults to this transform.")]
        public Transform positionSource;

        [Tooltip("Tracked controller; its rotation is used for held objects. Defaults to this transform.")]
        public Transform rotationSource;

        [Header("Grabbing")]
        [Tooltip("How close an object has to be to the hand to grab it (meters).")]
        public float grabRadius = 0.15f;
        public LayerMask grabLayers = ~0;
        [Range(0f, 1f)] public float gripPress = 0.6f;
        [Range(0f, 1f)] public float gripRelease = 0.35f;

        [Header("Throwing")]
        public float throwVelocityMultiplier = 1f;
        public float throwAngularMultiplier = 1f;
        [Tooltip("Hand motion over this many seconds is averaged to get the throw velocity.")]
        public float throwSampleWindow = 0.1f;

        public Vector3 HandPosition => positionSource != null ? positionSource.position : transform.position;
        public Quaternion HandRotation => rotationSource != null ? rotationSource.rotation : transform.rotation;

        /// <summary>The object this hand is holding right now (null if none).</summary>
        public NetworkGrabbable Held { get; private set; }

        const int SampleCount = 16;
        struct Sample { public float time; public Vector3 pos; public Quaternion rot; }
        readonly Sample[] samples = new Sample[SampleCount];
        int sampleHead;
        int sampleFilled;

        readonly Collider[] overlap = new Collider[16];
        InputDevice device;
        bool gripDown;
        NetworkGrabbable pending;

        void Update()
        {
            RecordSample();

            bool down = ReadGrip();
            if (down && !gripDown)
                BeginGrab();
            else if (!down && gripDown)
                EndGrab();
            gripDown = down;
        }

        bool ReadGrip()
        {
            if (!device.isValid)
                device = InputDevices.GetDeviceAtXRNode(hand);
            if (!device.isValid)
                return false;

            if (device.TryGetFeatureValue(CommonUsages.grip, out float grip))
                return grip > (gripDown ? gripRelease : gripPress);
            if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool button))
                return button;
            return false;
        }

        void BeginGrab()
        {
            if (Held != null || pending != null)
                return;

            int count = Physics.OverlapSphereNonAlloc(HandPosition, grabRadius, overlap, grabLayers, QueryTriggerInteraction.Ignore);
            NetworkGrabbable best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var grabbable = overlap[i].GetComponentInParent<NetworkGrabbable>();
                if (grabbable == null || !grabbable.CanBeGrabbedBy(this))
                    continue;
                float distance = (overlap[i].ClosestPoint(HandPosition) - HandPosition).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = grabbable;
                }
            }

            if (best != null && best.TryGrab(this))
                pending = best;
        }

        void EndGrab()
        {
            if (Held != null)
                Held.Release(this, ThrowVelocity() * throwVelocityMultiplier, ThrowAngularVelocity() * throwAngularMultiplier);
            else if (pending != null)
                pending.CancelPendingGrab(this);
            pending = null;
        }

        // Called by the grabbable.
        internal void OnGrabbed(NetworkGrabbable grabbable)
        {
            Held = grabbable;
            pending = null;
        }

        internal void OnReleased(NetworkGrabbable grabbable)
        {
            if (Held == grabbable)
                Held = null;
            if (pending == grabbable)
                pending = null;
        }

        void RecordSample()
        {
            samples[sampleHead] = new Sample { time = Time.unscaledTime, pos = HandPosition, rot = HandRotation };
            sampleHead = (sampleHead + 1) % SampleCount;
            if (sampleFilled < SampleCount)
                sampleFilled++;
        }

        // Oldest sample that is still inside the throw window, and the newest one.
        bool GetWindow(out Sample oldest, out Sample newest)
        {
            newest = samples[(sampleHead - 1 + SampleCount) % SampleCount];
            oldest = newest;
            for (int i = 2; i <= sampleFilled; i++)
            {
                var s = samples[(sampleHead - i + SampleCount) % SampleCount];
                if (newest.time - s.time > throwSampleWindow)
                    break;
                oldest = s;
            }
            return newest.time - oldest.time > 0.0001f;
        }

        Vector3 ThrowVelocity()
        {
            if (!GetWindow(out var oldest, out var newest))
                return Vector3.zero;
            return (newest.pos - oldest.pos) / (newest.time - oldest.time);
        }

        Vector3 ThrowAngularVelocity()
        {
            if (!GetWindow(out var oldest, out var newest))
                return Vector3.zero;
            Quaternion delta = newest.rot * Quaternion.Inverse(oldest.rot);
            delta.ToAngleAxis(out float degrees, out Vector3 axis);
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x))
                return Vector3.zero;
            if (degrees > 180f)
                degrees -= 360f;
            return axis * (degrees * Mathf.Deg2Rad / (newest.time - oldest.time));
        }

        void OnDisable()
        {
            if (Held != null)
                Held.Release(this, Vector3.zero, Vector3.zero);
            pending = null;
            gripDown = false;
        }
    }
}
