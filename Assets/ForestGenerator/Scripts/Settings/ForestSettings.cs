using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>The 8 ground surfaces the forest floor blends between. The order is the order of the texture array slices.</summary>
    public enum ForestGroundRole
    {
        Dirt = 0,
        Gravel = 1,
        Moss = 2,
        Rock = 3,
        Grass = 4,
        LeafLitter = 5,
        Roots = 6,
        Needles = 7
    }

    /// <summary>How much of each ground surface an area gets. The painter normalises the mix, so only the ratios matter.</summary>
    [System.Serializable]
    public struct ForestGroundMix
    {
        [Min(0f)] public float dirt;
        [Min(0f)] public float gravel;
        [Min(0f)] public float moss;
        [Min(0f)] public float rock;
        [Min(0f)] public float grass;
        [Min(0f)] public float leafLitter;
        [Min(0f)] public float roots;
        [Tooltip("Conifer needles and forest duff.")]
        [Min(0f)] public float needles;

        public const int Count = 8;

        public float this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0: return dirt;
                    case 1: return gravel;
                    case 2: return moss;
                    case 3: return rock;
                    case 4: return grass;
                    case 5: return leafLitter;
                    case 6: return roots;
                    default: return needles;
                }
            }
        }

        public static ForestGroundMix Make(float dirt, float gravel, float moss, float rock, float grass, float leafLitter, float roots, float needles)
        {
            return new ForestGroundMix { dirt = dirt, gravel = gravel, moss = moss, rock = rock, grass = grass, leafLitter = leafLitter, roots = roots, needles = needles };
        }
    }

    /// <summary>A multiplier for every asset category that carries this tag (see the Forest Asset Library).</summary>
    [System.Serializable]
    public struct ForestTagWeight
    {
        public string tag;
        [Min(0f)] public float multiplier;

        public ForestTagWeight(string tag, float multiplier)
        {
            this.tag = tag;
            this.multiplier = multiplier;
        }
    }

    /// <summary>
    /// One kind of woodland. The forest is split into irregular regions, each given one of these, with soft borders between.
    /// </summary>
    [System.Serializable]
    public class ForestZone
    {
        public string name = "Zone";
        public bool enabled = true;

        [Tooltip("How common this zone is compared with the others.")]
        [Min(0f)] public float weight = 1f;

        [Tooltip("Where this zone prefers to appear. -1 = near the trailhead (the start feels familiar), 0 = anywhere, 1 = deep in the forest (the far end feels wrong).")]
        [Range(-1f, 1f)] public float dreadPreference = 0f;

        [Tooltip("Multiplier on the density of every tree category.")]
        [Min(0f)] public float treeDensity = 1f;

        [Tooltip("Multiplier on the density of bushes, ferns, grass and other undergrowth.")]
        [Min(0f)] public float undergrowthDensity = 1f;

        [Tooltip("Multipliers for asset categories by tag, e.g. Conifer 1.6 makes conifers more common here. Tags not listed count as 1.")]
        public List<ForestTagWeight> tagWeights = new List<ForestTagWeight>();

        [Tooltip("The ground surfaces of this zone.")]
        public ForestGroundMix ground = ForestGroundMix.Make(0.3f, 0f, 0.3f, 0f, 0.1f, 0.2f, 0.1f, 0.4f);

        [Tooltip("Only used by the debug gizmos.")]
        public Color debugColor = Color.green;

        public float TagMultiplier(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tagWeights == null) return 1f;
            for (int i = 0; i < tagWeights.Count; i++)
                if (tagWeights[i].tag == tag) return tagWeights[i].multiplier;
            return 1f;
        }
    }

    [System.Serializable]
    public class ForestWorldSettings
    {
        [Tooltip("Size in metres of the playable area (inside the raised boundary).")]
        public Vector2 playableSize = new Vector2(300f, 300f);

        [Tooltip("How rounded the corners of the playable area are, in metres.")]
        [Min(0f)] public float cornerRadius = 45f;

        [Tooltip("How far in metres the edge of the playable area wanders in and out, so it is not a straight line.")]
        [Min(0f)] public float edgeWobble = 14f;

        [Tooltip("Length in metres of one bulge of the edge wobble.")]
        [Min(10f)] public float edgeWobbleScale = 80f;
    }

    [System.Serializable]
    public class ForestTerrainSettings
    {
        [Tooltip("Distance in metres between terrain vertices. Smaller = smoother ground and trail edges, more triangles. 1.5 is a good fit for Quest.")]
        [Range(0.75f, 4f)] public float vertexSpacing = 1.5f;

        [Tooltip("Terrain chunk size in vertices per side. Chunks are culled one by one, so smaller chunks cull tighter but cost more draw calls.")]
        [Range(8, 64)] public int chunkCells = 32;

        [Tooltip("Height in metres of the big rolling hills.")]
        [Min(0f)] public float hillHeight = 9f;

        [Tooltip("Size in metres of one hill.")]
        [Min(10f)] public float hillScale = 130f;

        [Tooltip("0 = round hills, 1 = sharp ridges and gullies.")]
        [Range(0f, 1f)] public float ridges = 0.35f;

        [Tooltip("Height in metres of the small bumps that make the forest floor uneven.")]
        [Min(0f)] public float bumpHeight = 0.7f;

        [Tooltip("Size in metres of the small bumps.")]
        [Min(1f)] public float bumpScale = 9f;

        [Tooltip("Layer for the generated terrain, boundary and collider objects. It must be in the Gorilla Player's Locomotion Enabled Layers.")]
        public int layer = 0;

        [Tooltip("Should the terrain cast shadows? Off is cheaper; the ground is lit by the sun without shadows on Quest anyway.")]
        public bool castShadows = false;
    }

    [System.Serializable]
    public class ForestTrailSettings
    {
        [Tooltip("Full width in metres of the main trails (the backbone that links the trailhead and every clearing). Never narrower than the monster corridor.")]
        [Min(1f)] public float mainWidth = 3.8f;

        [Tooltip("Full width in metres of the connecting paths (loops and side trips). Never narrower than the monster corridor.")]
        [Min(1f)] public float pathWidth = 3.2f;

        [Tooltip("Extra links between clearings, so the trails form loops instead of only dead ends. 0 = a tree of trails, 1 = many loops.")]
        [Range(0f, 1f)] public float loops = 0.5f;

        [Tooltip("Intentional dead ends: short trails that end at something worth finding (a landmark).")]
        [Range(0, 8)] public int deadEnds = 3;

        [Tooltip("Turn some of the connecting paths into narrow player-only escape routes the monster cannot follow.")]
        public bool playerOnlyPaths = false;

        [Tooltip("Share of the connecting paths that become player-only, when Player Only Paths is on.")]
        [Range(0f, 1f)] public float playerOnlyShare = 0.35f;

        [Tooltip("Full width in metres of the player-only paths.")]
        [Min(0.6f)] public float playerOnlyWidth = 1.4f;

        [Tooltip("How much the trails wander. 0 = as direct as the slopes allow.")]
        [Range(0f, 1f)] public float wander = 0.6f;

        [Tooltip("Size in metres of the wiggles in the trails.")]
        [Min(4f)] public float wanderScale = 26f;

        [Tooltip("How often a trail runs dead straight for a long stretch (a long, open sightline), instead of winding (it hides what is ahead).")]
        [Range(0f, 1f)] public float straightRuns = 0.5f;

        [Tooltip("How strongly the trails avoid steep ground.")]
        [Range(0f, 4f)] public float slopeAvoidance = 1.5f;

        [Tooltip("Metres of ground on each side of a trail that is levelled with it.")]
        [Min(0f)] public float shoulder = 2.5f;

        [Tooltip("How far in metres the edge of the gravel and dirt wanders in and out.")]
        [Range(0f, 2f)] public float edgeRaggedness = 0.7f;

        [Tooltip("Share of the trail width (from the middle) that is gravel. The rest is packed dirt. Narrow player-only paths are dirt only.")]
        [Range(0f, 1f)] public float gravelShare = 0.72f;

        [Tooltip("Separate trails keep at least this many metres apart, unless they join.")]
        [Min(4f)] public float separation = 16f;

        [Tooltip("Size in metres of the grid the trails are routed on. Smaller is more precise and slower.")]
        [Range(1f, 4f)] public float routingCell = 2f;
    }

    [System.Serializable]
    public class ForestClearingSettings
    {
        [Tooltip("Number of open clearings. The trails link them.")]
        [Range(1, 16)] public int count = 7;

        [Tooltip("Clearing radius (min, max) in metres. Never smaller than the monster's turning space.")]
        public Vector2 radius = new Vector2(9f, 15f);

        [Tooltip("Clearings keep at least this many metres apart.")]
        [Min(10f)] public float spacing = 55f;

        [Tooltip("0 = round clearings, 1 = very irregular.")]
        [Range(0f, 0.6f)] public float irregularity = 0.3f;

        [Tooltip("How flat the ground in the clearings is made.")]
        [Range(0f, 1f)] public float flatten = 0.8f;

        [Tooltip("Radius in metres of the small clearing at the trailhead, where the player starts.")]
        [Min(3f)] public float trailheadRadius = 8f;

        [Tooltip("Places reserved for landmarks (big boulders, a giant dead tree, an old sign...). They help players find their way without making it easy.")]
        [Range(0, 16)] public int landmarks = 6;
    }

    [System.Serializable]
    public class ForestZoneSettings
    {
        [Tooltip("Average size in metres of one zone region.")]
        [Min(20f)] public float zoneSize = 75f;

        [Tooltip("Width in metres of the soft border between two zones.")]
        [Min(1f)] public float borderBlend = 16f;

        [Tooltip("How much the zone borders wiggle, in metres.")]
        [Min(0f)] public float borderWarp = 22f;

        public List<ForestZone> zones = CreateDefaults();

        public static List<ForestZone> CreateDefaults()
        {
            return new List<ForestZone>
            {
                new ForestZone
                {
                    name = "Old Conifer Stand", weight = 3f, dreadPreference = 0f, treeDensity = 1f, undergrowthDensity = 0.6f,
                    tagWeights = new List<ForestTagWeight> { new ForestTagWeight("Conifer", 1.6f), new ForestTagWeight("Deciduous", 0.3f), new ForestTagWeight("Birch", 0.35f), new ForestTagWeight("DeadTree", 0.4f) },
                    ground = ForestGroundMix.Make(0.2f, 0f, 0.35f, 0f, 0.05f, 0.1f, 0.12f, 0.6f),
                    debugColor = new Color(0.2f, 0.45f, 0.25f)
                },
                new ForestZone
                {
                    name = "Autumn Mixed Wood", weight = 2f, dreadPreference = -0.6f, treeDensity = 0.85f, undergrowthDensity = 1.3f,
                    tagWeights = new List<ForestTagWeight> { new ForestTagWeight("Conifer", 0.7f), new ForestTagWeight("Deciduous", 1.7f), new ForestTagWeight("Birch", 0.8f), new ForestTagWeight("DeadTree", 0.2f) },
                    ground = ForestGroundMix.Make(0.2f, 0f, 0.15f, 0f, 0.15f, 0.75f, 0.05f, 0.1f),
                    debugColor = new Color(0.85f, 0.45f, 0.15f)
                },
                new ForestZone
                {
                    name = "Birch Grove", weight = 1f, dreadPreference = -0.3f, treeDensity = 0.8f, undergrowthDensity = 0.9f,
                    tagWeights = new List<ForestTagWeight> { new ForestTagWeight("Birch", 2.6f), new ForestTagWeight("Deciduous", 0.6f), new ForestTagWeight("Conifer", 0.35f) },
                    ground = ForestGroundMix.Make(0.1f, 0f, 0.2f, 0f, 0.5f, 0.35f, 0.02f, 0.05f),
                    debugColor = new Color(0.9f, 0.9f, 0.8f)
                },
                new ForestZone
                {
                    name = "Thicket", weight = 1f, dreadPreference = 0.3f, treeDensity = 1.5f, undergrowthDensity = 2.2f,
                    tagWeights = new List<ForestTagWeight> { new ForestTagWeight("YoungConifer", 2.6f), new ForestTagWeight("Conifer", 1f), new ForestTagWeight("Bush", 2f) },
                    ground = ForestGroundMix.Make(0.2f, 0f, 0.3f, 0f, 0.05f, 0.1f, 0.1f, 0.55f),
                    debugColor = new Color(0.1f, 0.25f, 0.12f)
                },
                new ForestZone
                {
                    name = "Dead Stand", weight = 1f, dreadPreference = 1f, treeDensity = 0.75f, undergrowthDensity = 0.5f,
                    tagWeights = new List<ForestTagWeight> { new ForestTagWeight("DeadTree", 3.5f), new ForestTagWeight("Conifer", 0.6f), new ForestTagWeight("Deciduous", 0.15f), new ForestTagWeight("Birch", 0.2f), new ForestTagWeight("FallenLog", 2.5f) },
                    ground = ForestGroundMix.Make(0.45f, 0f, 0.1f, 0.12f, 0.02f, 0.05f, 0.35f, 0.25f),
                    debugColor = new Color(0.35f, 0.3f, 0.3f)
                },
                new ForestZone
                {
                    name = "Rocky Outcrop", weight = 1f, dreadPreference = 0.2f, treeDensity = 0.55f, undergrowthDensity = 0.6f,
                    tagWeights = new List<ForestTagWeight> { new ForestTagWeight("Boulder", 3f), new ForestTagWeight("Rock", 2f), new ForestTagWeight("Conifer", 1.2f) },
                    ground = ForestGroundMix.Make(0.15f, 0.05f, 0.4f, 0.45f, 0.05f, 0.05f, 0.05f, 0.25f),
                    debugColor = new Color(0.55f, 0.55f, 0.6f)
                }
            };
        }
    }

    [System.Serializable]
    public class ForestGroundSettings
    {
        [Tooltip("Resolution of the two ground blend maps (8 surfaces) covering the whole terrain. 1024 = about 0.4 m per texel on the default forest. Uncompressed: 1024 uses about 11 MB of memory for both, 512 about 3 MB.")]
        public ForestMapResolution blendMapResolution = ForestMapResolution._1024;

        [Tooltip("Resolution of the ground light map (canopy shade, dappled sun, ambient occlusion).")]
        public ForestMapResolution lightMapResolution = ForestMapResolution._1024;

        [Tooltip("Size in metres of the patches the surfaces are scattered in.")]
        [Min(1f)] public float patchSize = 7f;

        [Tooltip("How strongly the patches vary. 0 = an even mix everywhere.")]
        [Range(0f, 1f)] public float patchContrast = 0.7f;

        [Tooltip("Ground steeper than this many degrees starts to show rock.")]
        [Range(0f, 90f)] public float rockSlopeStart = 30f;

        [Tooltip("Ground steeper than this many degrees is all rock.")]
        [Range(0f, 90f)] public float rockSlopeEnd = 48f;

        [Tooltip("How much grass grows in the clearings.")]
        [Range(0f, 1f)] public float clearingGrass = 0.6f;

        [Tooltip("How much fallen leaves drift onto the trail edges.")]
        [Range(0f, 1f)] public float leavesOnTrails = 0.5f;

        [Tooltip("How dark valleys and hollows get (baked ambient occlusion from the terrain shape).")]
        [Range(0f, 1f)] public float terrainOcclusion = 0.5f;
    }

    public enum ForestMapResolution
    {
        _256 = 256,
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048
    }

    public enum ForestEdgeType
    {
        [Tooltip("Rises steeply into a rock wall.")] Cliff = 0,
        [Tooltip("A steep slope covered in boulders.")] RockyIncline = 1,
        [Tooltip("A gentler slope blocked by dense trees and deadfall.")] DenseWoodland = 2
    }

    [System.Serializable]
    public class ForestBoundarySettings
    {
        [Tooltip("Width in metres of the raised band around the playable area.")]
        [Min(10f)] public float bandWidth = 45f;

        [Tooltip("How high the ground rises at the edge (min, max) in metres.")]
        public Vector2 riseHeight = new Vector2(14f, 26f);

        [Tooltip("How much the rise varies around the edge, in metres of edge per change.")]
        [Min(20f)] public float variationScale = 110f;

        [Tooltip("How common each kind of edge is: cliffs, rocky inclines, dense woodland.")]
        public Vector3 edgeTypeWeights = new Vector3(1f, 1f, 1f);

        [Tooltip("How suddenly the edge type changes: 0 = long soft blends, 1 = fairly sharp.")]
        [Range(0f, 1f)] public float edgeTypeSharpness = 0.6f;

        [Tooltip("Height in metres of the gentle rise just inside the playable edge, at the foot of the slope.")]
        [Min(0f)] public float footRise = 1.5f;

        [Header("Containment")]
        [Tooltip("Build the hidden containment: thick wall colliders a few metres into the slope plus an invisible ceiling high above the trees. With Gorilla locomotion players can climb any collider, so slopes and trees alone cannot keep them in.")]
        public bool buildContainment = true;

        [Tooltip("How far in metres outside the playable edge the hidden wall stands (inside the slope, behind the rocks and trees).")]
        [Min(1f)] public float wallInset = 9f;

        [Tooltip("Thickness of the hidden wall in metres. Thick walls cannot be tunnelled through at high speed.")]
        [Min(0.5f)] public float wallThickness = 4f;

        [Tooltip("The invisible ceiling sits this many metres above the highest treetop (more than a jump), so climbing trees or walls never gets a player over the edge.")]
        [Min(2f)] public float ceilingClearance = 10f;
    }

    [System.Serializable]
    public class ForestStreetlightSettings
    {
        [Tooltip("Average number of streetlights per 100 metres of trail.")]
        [Range(0f, 3f)] public float perHundredMetres = 0.55f;

        [Tooltip("Share of the streetlights that stand somewhere unexpected: off the trail, deep in the trees.")]
        [Range(0f, 1f)] public float offTrailShare = 0.3f;

        [Tooltip("Streetlights keep at least this many metres apart.")]
        [Min(5f)] public float spacing = 38f;

        [Tooltip("Share of the lamps with a sickly green tint.")]
        [Range(0f, 1f)] public float greenShare = 0.3f;

        [Tooltip("Share of the lamps that are dead (no light).")]
        [Range(0f, 1f)] public float brokenShare = 0.2f;

        [Tooltip("Share of the working lamps that flicker.")]
        [Range(0f, 1f)] public float flickerShare = 0.25f;

        [Tooltip("Deeper in the forest more lamps are dead and flickering: this much more at the far end.")]
        [Range(0f, 1f)] public float decayWithDread = 0.4f;

        [Tooltip("Normal lamp colour (an old sodium/incandescent warm white).")]
        public Color warmColor = new Color(1f, 0.82f, 0.6f);

        [Tooltip("The green tint.")]
        public Color greenColor = new Color(0.55f, 1f, 0.55f);

        [Tooltip("Light radius in metres.")]
        [Min(1f)] public float range = 11f;

        [Tooltip("Light brightness.")]
        [Min(0f)] public float intensity = 1.6f;
    }

    [System.Serializable]
    public class ForestScatterSettings
    {
        [Tooltip("Multiplier on the density of every asset category. A quick way to thin the whole forest.")]
        [Range(0f, 3f)] public float globalDensity = 1f;

        [Tooltip("Multiplier on the density of small ground cover (grass, debris, pebbles).")]
        [Range(0f, 3f)] public float groundCoverDensity = 1f;

        [Tooltip("Multiplier on every category's minimum spacing.")]
        [Range(0.25f, 3f)] public float spacingScale = 1f;

        [Tooltip("Dense thickets on the inside of sharp trail bends, so you cannot see round the corner.")]
        [Range(0f, 1f)] public float bendThickets = 0.7f;

        [Tooltip("Gaps (as wide as the monster) from the trails into the woods, where something can step out of the trees or vanish into them.")]
        [Range(0, 40)] public int monsterGaps = 14;

        [Tooltip("How far tree crowns may reach over the monster's routes, as a share of the crown radius. Trunks never stand in them. 0 = crowns stay clear too (wide open trails), 1 = crowns may close over the trail.")]
        [Range(0f, 1f)] public float canopyOverhang = 0.45f;
    }

    [System.Serializable]
    public class ForestAtmosphereSettings
    {
        [Tooltip("The look at the trailhead.")]
        public ForestAtmosphereProfile calm;

        [Tooltip("The look deep in the forest. The atmosphere blends from Calm to Dread as the player goes further in.")]
        public ForestAtmosphereProfile dread;

        [Tooltip("How much more dread being off the trail adds, at 50 m from the nearest trail.")]
        [Range(0f, 1f)] public float offTrailDread = 0.25f;
    }

    /// <summary>
    /// Everything that shapes the generated forest, in one asset. Same seed + same settings = same forest.
    /// The models that get placed are in a separate asset (Forest Asset Library), so the layout and the art can be changed
    /// independently.
    /// </summary>
    [CreateAssetMenu(menuName = "Havoc/Forest/Forest Settings", fileName = "ForestSettings", order = 0)]
    public class ForestSettings : ScriptableObject
    {
        public ForestWorldSettings world = new ForestWorldSettings();
        public ForestTerrainSettings terrain = new ForestTerrainSettings();
        public ForestTrailSettings trails = new ForestTrailSettings();
        public ForestClearingSettings clearings = new ForestClearingSettings();
        public ForestZoneSettings zones = new ForestZoneSettings();
        public ForestGroundSettings ground = new ForestGroundSettings();
        public ForestBoundarySettings boundary = new ForestBoundarySettings();
        public ForestMonsterProfile monster = new ForestMonsterProfile();
        public ForestScatterSettings scatter = new ForestScatterSettings();
        public ForestStreetlightSettings streetlights = new ForestStreetlightSettings();
        public ForestAtmosphereSettings atmosphere = new ForestAtmosphereSettings();

        /// <summary>The full width of a trail of this kind, widened to fit the monster where it has to.</summary>
        public float TrailWidth(ForestTrailKind kind)
        {
            float monsterWidth = monster.CorridorHalfWidth * 2f;
            switch (kind)
            {
                case ForestTrailKind.Main: return Mathf.Max(trails.mainWidth, monsterWidth);
                case ForestTrailKind.PlayerOnly: return trails.playerOnlyWidth;
                default: return Mathf.Max(trails.pathWidth, monsterWidth);
            }
        }

        /// <summary>The clearing radius range, raised to the monster's turning space where needed.</summary>
        public Vector2 ClearingRadius
        {
            get
            {
                float min = Mathf.Max(clearings.radius.x, monster.MinClearingRadius);
                return new Vector2(min, Mathf.Max(min, clearings.radius.y));
            }
        }
    }
}
