using System.Collections.Generic;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// A place where the monster cannot hunt you: a shelter, a lobby, a finish area. Put it on an object with one or
    /// more colliders (box, sphere, capsule or a convex mesh) that mark the area; a player whose chest is inside is
    /// safe. A chase on a player ends once they have been safe for a moment (MonsterHunter "Safe Confirm Time"), and
    /// the monster cannot start a new one on them while they stay inside.
    ///
    /// Nothing physical happens: no trigger events, no rigidbody needed, the monster just asks "is this point inside
    /// any zone?" a few times a second. That also works for remote players, whose avatars have no colliders. The
    /// zone's colliders are switched to triggers (the player is not blocked by them).
    ///
    /// Create one with the menu GameObject > Monster > Safe Zone. A non-convex mesh collider cannot be asked this
    /// question; use primitives or a convex mesh.
    /// </summary>
    [AddComponentMenu("Monster/Safe Zone")]
    public class SafeZone : MonoBehaviour
    {
        static readonly List<SafeZone> zones = new List<SafeZone>();

        [Tooltip("The colliders that make up the zone. Empty = every collider on this object and its children.")]
        public Collider[] volumes;

        bool warned;

        /// <summary>True if the point is inside any enabled safe zone.</summary>
        public static bool IsInsideAny(Vector3 point)
        {
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i].Contains(point))
                    return true;
            }
            return false;
        }

        /// <summary>True if the point is inside this zone.</summary>
        public bool Contains(Vector3 point)
        {
            if (volumes == null || volumes.Length == 0)
                volumes = GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < volumes.Length; i++)
            {
                Collider c = volumes[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                    continue;

                MeshCollider mesh = c as MeshCollider;
                if (mesh != null && !mesh.convex)
                {
                    if (!warned)
                    {
                        warned = true;
                        Debug.LogWarning("SafeZone '" + name + "' has a non-convex MeshCollider, which cannot tell what is inside it. Use a box/sphere/capsule or tick Convex.", this);
                    }
                    continue;
                }

                // For a point inside the collider ClosestPoint returns the point itself.
                if ((c.ClosestPoint(point) - point).sqrMagnitude < 1e-6f)
                    return true;
            }
            return false;
        }

        void OnEnable()
        {
            if (!zones.Contains(this))
                zones.Add(this);
        }

        void OnDisable()
        {
            zones.Remove(this);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            zones.Clear();
        }

        void Reset()
        {
            // A new zone starts as a 6 x 3 x 6 m box.
            Collider existing = GetComponent<Collider>();
            if (existing == null)
            {
                BoxCollider box = gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(6f, 3f, 6f);
                box.center = new Vector3(0f, 1.5f, 0f);
                existing = box;
            }
            existing.isTrigger = true;
        }

        void OnValidate()
        {
            // The zone is only a volume, never an obstacle for the player.
            if (volumes != null)
            {
                for (int i = 0; i < volumes.Length; i++)
                {
                    MeshCollider mesh = volumes[i] as MeshCollider;
                    if (volumes[i] != null && (mesh == null || mesh.convex))
                        volumes[i].isTrigger = true;
                }
            }
        }

        void OnDrawGizmos()
        {
            Collider[] cols = volumes != null && volumes.Length > 0 ? volumes : GetComponentsInChildren<Collider>(true);
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.18f);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null)
                    continue;
                Bounds b = cols[i].bounds;
                Gizmos.DrawCube(b.center, b.size);
                Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
                Gizmos.DrawWireCube(b.center, b.size);
                Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.18f);
            }
        }
    }
}
