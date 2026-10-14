using System.Collections.Generic;
using UnityEngine;

namespace MonsterAI
{
    /// <summary>
    /// A place the player can come back to after the monster catches them (see <see cref="PlayerRespawn"/>). The
    /// player is put on this object's position, facing the way its blue arrow points. Make them with
    /// Tools > Monster > Respawn Points > Create 5 From Forest Markers, then drag them wherever you like.
    /// </summary>
    [AddComponentMenu("Monster/Respawn Point")]
    public class RespawnPoint : MonoBehaviour
    {
        static readonly List<RespawnPoint> all = new List<RespawnPoint>();

        /// <summary>Every enabled respawn point.</summary>
        public static IReadOnlyList<RespawnPoint> All => all;

        [Tooltip("Off = never used (keep the point but take it out of the draw).")]
        public bool usable = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            all.Clear();   // Enter Play Mode without a domain reload keeps statics from the last run
        }

        void OnEnable()
        {
            if (!all.Contains(this))
                all.Add(this);
        }

        void OnDisable()
        {
            all.Remove(this);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = usable ? new Color(0.2f, 1f, 0.4f, 0.9f) : new Color(0.6f, 0.6f, 0.6f, 0.6f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.9f, 0.9f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(transform.position + Vector3.up * 0.9f, Flat(transform.forward) * 1.8f);
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
        }
    }
}
