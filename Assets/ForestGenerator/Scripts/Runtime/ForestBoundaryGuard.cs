using UnityEngine;
using UnityEngine.Events;

namespace HavocForest
{
    /// <summary>
    /// The last line of containment. The boundary slope, rock formations, hidden walls and ceiling already keep players in;
    /// this catches whatever slips through anyway (falling through the ground, tunnelling at high speed, a physics glitch,
    /// standing on the ceiling) and puts the player back on the nearest trail. It checks a few times a second; nothing
    /// happens while the player is inside.
    ///
    /// Works with the Gorilla rig (it resets the Gorilla Player after the move so it does not fling the player), and with
    /// any other rig (Character Controller or a plain transform).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Havoc/Forest Boundary Guard")]
    public class ForestBoundaryGuard : MonoBehaviour
    {
        [Tooltip("The player's rig root (moved when the player is put back). Empty = the Gorilla Player's rig, else the main camera's root.")]
        public Transform playerRoot;

        [Tooltip("The player's head (what is checked). Empty = the main camera.")]
        public Transform head;

        [Tooltip("How far past the hidden wall the head may get before the player is put back (metres). Small values may trigger on players pressed against the wall.")]
        [Min(0f)] public float tolerance = 2f;

        [Tooltip("How far below the ground the head may get before the player is put back (fell through the terrain).")]
        [Min(0.5f)] public float belowGround = 4f;

        [Tooltip("Checks per second.")]
        [Range(1f, 30f)] public float checksPerSecond = 5f;

        [Tooltip("Put the player back onto a trail the monster can use (wider), rather than any trail.")]
        public bool monsterTrailsOnly = false;

        [Tooltip("Raised after the player was put back: (where they were, where they are now). Hook a fade or a sound here.")]
        public UnityEvent<Vector3, Vector3> onReturned = new UnityEvent<Vector3, Vector3>();

        /// <summary>How many times the player has been put back this session.</summary>
        public int Returns { get; private set; }

        float nextCheck;

        void Update()
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + 1f / checksPerSecond;

            ForestWorld world = ForestWorld.Active;
            if (world == null || world.data == null) return;
            Transform h = head != null ? head : (Camera.main != null ? Camera.main.transform : null);
            if (h == null) return;
            Vector3 p = h.position;

            bool escaped = false;
            ForestData d = world.data;
            if (d.wallLine != null && d.wallLine.Length > 2)
            {
                float edge = world.EdgeDistance(p);     // positive inside the playable area
                if (edge < -(d.wallOffset + d.wallThickness + tolerance)) escaped = true;
            }
            if (p.y < world.GroundHeight(p) - belowGround) escaped = true;
            if (d.ceilingHeight > 0f && world.transform.InverseTransformPoint(p).y > d.ceilingHeight + 1f) escaped = true;
            if (escaped) ReturnPlayer(world, p);
        }

        /// <summary>Puts the player back on the nearest trail now (also usable by events).</summary>
        public void ReturnPlayer(ForestWorld world, Vector3 from)
        {
            Vector3 target;
            if (!world.TryGetNearestTrailPoint(from, monsterTrailsOnly, out target)) target = world.SpawnPose.position;

            GorillaLocomotion.Player gorilla = GorillaLocomotion.Player.Instance;
            Transform root = playerRoot;
            if (root == null && gorilla != null) root = gorilla.transform;
            if (root == null && Camera.main != null) root = Camera.main.transform.root;
            if (root == null) return;

            // move the rig so its head ends up above the trail point at its usual height above the rig
            Transform h = head != null ? head : (Camera.main != null ? Camera.main.transform : root);
            Vector3 headOffset = h.position - root.position;
            Vector3 newRoot = target + Vector3.up * 0.1f - new Vector3(headOffset.x, 0f, headOffset.z);

            var cc = root.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            root.position = newRoot;
            if (cc != null) cc.enabled = true;

            Rigidbody body = root.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
            if (gorilla != null && gorilla.transform == root) gorilla.InitializeValues();    // forget the old hand and head positions

            Returns++;
            onReturned.Invoke(from, target);
        }
    }
}
