using UnityEngine;

namespace HavocCave
{
    /// <summary>Ready-made starting points for the Cave Settings.</summary>
    public enum CavePreset
    {
        /// <summary>One winding tunnel linking a few round rooms. Calm and simple.</summary>
        Smooth,
        /// <summary>Several levels, loops, shafts, halls, cathedrals, pillars and stalactites.</summary>
        Experimental,
        /// <summary>Experimental with everything turned up.</summary>
        Wild
    }

    /// <summary>
    /// Everything that shapes the carved cave. Same seed + same settings = same cave.
    /// The defaults are the Experimental preset: tunnels on several levels with ramps and loops, vertical shafts,
    /// rooms of different kinds, and rock formations.
    /// </summary>
    [System.Serializable]
    public class CaveSettings
    {
        [Header("Tunnels")]
        [Tooltip("Length in metres of the main tunnel that starts at the spawn point.")]
        [Min(20f)] public float mainTunnelLength = 140f;

        [Tooltip("Side tunnels that split off the tunnels. Each ends in a room. 0 = one single winding path.")]
        [Range(0, 8)] public int branchCount = 3;

        [Tooltip("Average length in metres of each side tunnel.")]
        [Min(10f)] public float branchLength = 45f;

        [Tooltip("Tunnels that run from one tunnel round to another, so the cave has loops instead of only dead ends.")]
        [Range(0, 4)] public int loops = 1;

        [Tooltip("Full tunnel width (min, max) in metres. It drifts between the two along the tunnel.")]
        public Vector2 tunnelWidth = new Vector2(3.2f, 5.5f);

        [Tooltip("Full tunnel height (min, max) in metres.")]
        public Vector2 tunnelHeight = new Vector2(2.8f, 4.6f);

        [Tooltip("Maximum change of heading in degrees every step. Higher = windier.")]
        [Range(0f, 90f)] public float turnAmount = 30f;

        [Tooltip("Steepest ramp: rise over run. 0.2 is about 11 degrees. 0 = perfectly flat tunnels.")]
        [Range(0f, 0.4f)] public float slope = 0.2f;

        [Tooltip("How far up or down (metres) tunnels may climb away from the spawn level. 0 = everything on one level. Side tunnels pick a level of their own, so they cross over and under each other.")]
        [Min(0f)] public float levelRange = 14f;

        [Tooltip("Minimum distance in metres between separate tunnels, so they don't run into each other.")]
        [Min(3f)] public float tunnelSpacing = 12f;

        [Tooltip("Distance in metres between layout points. Smaller = smoother bends.")]
        [Min(2f)] public float stepLength = 4f;

        [Header("Experimental")]
        [Tooltip("0 = every tunnel has the same cross section. Higher = narrow tall fissures, wide low galleries and squeezes along the way.")]
        [Range(0f, 1f)] public float shapeVariety = 0.6f;

        [Tooltip("0 = every room is a plain round dome. Higher = long halls, tall cathedrals and lumpy caverns.")]
        [Range(0f, 1f)] public float roomVariety = 0.7f;

        [Tooltip("Pillars, stalagmites and stalactites. 0 = none.")]
        [Range(0f, 1f)] public float formations = 0.6f;

        [Tooltip("Vertical shafts. Each one is either a pit in a room floor that drops into a lower chamber, or a chimney that rises out of a room ceiling.")]
        [Range(0, 4)] public int shafts = 1;

        [Tooltip("Horizontal layering in the rock walls, like sediment. 0 = none.")]
        [Range(0f, 0.6f)] public float strata = 0.12f;

        [Header("Rooms")]
        [Tooltip("A room every this many metres along the main tunnel. There is always one at the spawn and one at the end of every tunnel.")]
        [Min(10f)] public float roomSpacing = 32f;

        [Tooltip("Room radius (min, max) in metres. 5 to 9 gives rooms about 10 to 18 metres across. Halls, cathedrals and caverns are scaled from this.")]
        public Vector2 roomRadius = new Vector2(5f, 9f);

        [Tooltip("Room height (min, max) in metres, floor to the top of the dome.")]
        public Vector2 roomHeight = new Vector2(4.5f, 7f);

        [Tooltip("0 = round rooms, 1 = rounded squares.")]
        [Range(0f, 1f)] public float roomSquareness = 0.25f;

        [Header("Rock Shape")]
        [Tooltip("Size of one voxel in metres. Smaller = more detail but many more triangles. Only used when Use Poly Count is off; otherwise it is picked from Poly Count.")]
        [Range(0.3f, 1f)] public float voxelSize = 0.5f;

        [Tooltip("0 = round tunnels, 1 = boxy mine tunnels with a flat floor and ceiling.")]
        [Range(0f, 1f)] public float squareness = 0.5f;

        [Tooltip("How far (metres) the walls bump in and out. Low = smooth.")]
        [Range(0f, 1.2f)] public float roughness = 0.3f;

        [Tooltip("Size in metres of the lumps. Bigger = broader, smoother lumps.")]
        [Range(1f, 10f)] public float roughnessScale = 5f;

        [Tooltip("How much of the roughness is kept on the floor. 0 = flat floor, 1 = same as the walls.")]
        [Range(0f, 1f)] public float floorRoughness = 0.2f;

        [Tooltip("Boulders sticking out of the walls and floor, per 10 m of tunnel. 0 = none.")]
        [Range(0f, 3f)] public float rocksPer10m = 0.4f;

        [Tooltip("Boulder radius range in metres.")]
        public Vector2 rockRadius = new Vector2(0.6f, 1.4f);

        [Header("Dirt Path")]
        [Tooltip("Width in metres of the dirt strip down the middle of the floor.")]
        [Min(0f)] public float pathWidth = 2.6f;

        [Tooltip("How many metres the dirt takes to fade into rock at its edges.")]
        [Min(0.1f)] public float pathFade = 1.8f;

        [Tooltip("How much the edge of the dirt path wanders, in metres.")]
        [Range(0f, 2f)] public float pathWobble = 0.6f;

        public CaveSettings Clone()
        {
            return (CaveSettings)MemberwiseClone();
        }

        /// <summary>Settings for one of the presets.</summary>
        public static CaveSettings FromPreset(CavePreset preset)
        {
            var s = new CaveSettings();
            switch (preset)
            {
                case CavePreset.Smooth:
                    s.mainTunnelLength = 120f;
                    s.branchCount = 1;
                    s.branchLength = 40f;
                    s.loops = 0;
                    s.tunnelWidth = new Vector2(3.4f, 4.2f);
                    s.tunnelHeight = new Vector2(2.8f, 3.4f);
                    s.turnAmount = 22f;
                    s.slope = 0.04f;
                    s.levelRange = 0f;
                    s.tunnelSpacing = 14f;
                    s.shapeVariety = 0f;
                    s.roomVariety = 0f;
                    s.formations = 0f;
                    s.shafts = 0;
                    s.strata = 0f;
                    s.roomSpacing = 35f;
                    s.roomRadius = new Vector2(5.5f, 9f);
                    s.roomHeight = new Vector2(4.2f, 6f);
                    s.roomSquareness = 0.3f;
                    s.squareness = 0.7f;
                    s.roughness = 0.2f;
                    s.roughnessScale = 6f;
                    s.rocksPer10m = 0f;
                    s.rockRadius = new Vector2(0.5f, 1.2f);
                    break;

                case CavePreset.Wild:
                    s.mainTunnelLength = 180f;
                    s.branchCount = 5;
                    s.branchLength = 55f;
                    s.loops = 2;
                    s.tunnelWidth = new Vector2(3f, 6.5f);
                    s.tunnelHeight = new Vector2(2.6f, 5.5f);
                    s.turnAmount = 42f;
                    s.slope = 0.32f;
                    s.levelRange = 26f;
                    s.tunnelSpacing = 11f;
                    s.shapeVariety = 1f;
                    s.roomVariety = 1f;
                    s.formations = 1f;
                    s.shafts = 2;
                    s.strata = 0.2f;
                    s.roomSpacing = 28f;
                    s.roomRadius = new Vector2(5f, 10.5f);
                    s.roomHeight = new Vector2(4.5f, 8f);
                    s.roomSquareness = 0.15f;
                    s.squareness = 0.35f;
                    s.roughness = 0.5f;
                    s.roughnessScale = 4.5f;
                    s.rocksPer10m = 0.7f;
                    break;
            }
            return s;
        }

        /// <summary>The Poly Count that goes with a preset.</summary>
        public static float PolyCountFor(CavePreset preset)
        {
            switch (preset)
            {
                case CavePreset.Smooth: return 40000f;
                case CavePreset.Wild: return 80000f;
                default: return 60000f;
            }
        }
    }
}
