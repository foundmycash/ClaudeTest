using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// Kinds of places the generator marks for gameplay and future horror systems (audio, events, creature appearances).
    /// Query them through <see cref="ForestWorld"/>.
    /// </summary>
    public enum ForestMarkerType
    {
        /// <summary>Where the player starts (the trailhead), facing down the first trail.</summary>
        Spawn,
        /// <summary>An open clearing. Radius = clearing radius.</summary>
        Clearing,
        /// <summary>The clearing furthest in along the trails: the heart of the forest.</summary>
        Heart,
        /// <summary>Where three or more trails meet.</summary>
        Junction,
        /// <summary>The end of an intentional dead end.</summary>
        DeadEnd,
        /// <summary>A landmark site (a landmark asset is placed here).</summary>
        Landmark,
        /// <summary>The start of a long straight stretch of trail. Faces along it; Value = how far you can see, in metres. Good for distant sightings.</summary>
        RevealPoint,
        /// <summary>A sharp bend where the trees hide what is round the corner. Faces along the trail; Value = bend angle (+ left, - right).</summary>
        BlindCorner,
        /// <summary>A spot deep in the trees, away from the trails, for distant sounds.</summary>
        AmbientSound,
        /// <summary>A monster-wide gap from the trail into the woods, where something can step out or vanish. Faces from the woods to the trail.</summary>
        MonsterGap,
        /// <summary>A hidden pocket at the far end of a monster gap.</summary>
        MonsterLair,
        /// <summary>A streetlight. Value: 0 = working, 1 = flickering, 2 = dead; Radius = light range.</summary>
        Streetlight
    }

    /// <summary>One marked place, in the forest's local space (use ForestWorld to convert to world space).</summary>
    [System.Serializable]
    public struct ForestMarker
    {
        public ForestMarkerType type;
        public Vector3 position;

        [Tooltip("Compass direction in degrees (0 = local +Z).")]
        public float yaw;

        public float radius;

        [Tooltip("0..1: how deep in the forest this is.")]
        public float dread;

        [Tooltip("Extra number, meaning depends on the type (see ForestMarkerType).")]
        public float value;

        public Vector3 Forward
        {
            get { return Quaternion.Euler(0f, yaw, 0f) * Vector3.forward; }
        }
    }
}
