using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// The size of the creature the forest has to make room for. Trails, clearings, obstacle spacing, streetlight arms and the
    /// monster NavMesh are all derived from these numbers, so changing them only needs a regenerate (and a NavMesh re-bake),
    /// never a code change.
    /// </summary>
    [System.Serializable]
    public class ForestMonsterProfile
    {
        [Tooltip("Height of the player in metres. Only used as a reference (the inspector shows the monster as a multiple of it).")]
        [Min(0.5f)] public float playerHeight = 1.6f;

        [Tooltip("Full height of the monster in metres, from the ground to the top of its head (or back).")]
        [Min(0.5f)] public float height = 4.8f;

        [Tooltip("Full width of the monster's body in metres (shoulder to shoulder). The monster NavMesh agent radius is half of this.")]
        [Min(0.2f)] public float width = 2f;

        [Tooltip("Radius in metres of the tightest circle the monster can turn around in. Clearings are made at least this big (plus half its width and the margin), so it can turn around in every clearing.")]
        [Min(0f)] public float turningRadius = 3.5f;

        [Tooltip("Ground clearance: obstacles lower than this (roots, branches, small rocks) are stepped over and do not count as blocking. Also the NavMesh step height.")]
        [Min(0f)] public float stepHeight = 0.6f;

        [Tooltip("Steepest ground in degrees the monster can walk up. Used for the NavMesh.")]
        [Range(10f, 60f)] public float maxSlope = 35f;

        [Tooltip("Free space in metres kept on each side of the monster's body along its routes.")]
        [Min(0f)] public float sideMargin = 0.6f;

        [Tooltip("Free space in metres kept above the monster's head along its routes (streetlight arms, low branches).")]
        [Min(0f)] public float overheadMargin = 0.5f;

        /// <summary>Half the width of the strip that must stay free of blocking objects along a monster route.</summary>
        public float CorridorHalfWidth { get { return width * 0.5f + sideMargin; } }

        /// <summary>The smallest clearing radius the monster can turn around in.</summary>
        public float MinClearingRadius { get { return turningRadius + width * 0.5f + sideMargin; } }

        /// <summary>Height that must stay free above a monster route.</summary>
        public float OverheadClearance { get { return height + overheadMargin; } }

        /// <summary>How many times the player's height the monster is.</summary>
        public float SizeRatio { get { return height / Mathf.Max(0.1f, playerHeight); } }
    }
}
