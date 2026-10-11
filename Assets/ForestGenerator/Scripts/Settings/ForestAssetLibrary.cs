using System.Collections.Generic;
using UnityEngine;

namespace HavocForest
{
    /// <summary>What a category is, for density: zones scale trees and undergrowth separately, and ground cover has its own global multiplier.</summary>
    public enum ForestAssetKind
    {
        Tree,
        Undergrowth,
        GroundCover,
        [Tooltip("Rocks, boulders, logs, stumps: solid things on the ground.")] Obstacle,
        [Tooltip("Placed by a special rule (landmarks, streetlights, boundary rocks), not scattered by density.")] Special
    }

    /// <summary>How a category is placed.</summary>
    public enum ForestPlacement
    {
        [Tooltip("Scattered by density over the forest.")] Scatter,
        [Tooltip("One per landmark site (dead ends, clearings, beside trails).")] Landmark,
        [Tooltip("Along the trails and in odd places, by the Streetlights settings.")] Streetlight,
        [Tooltip("Packed along the raised boundary so it reads as a rock wall.")] BoundaryRock,
        [Tooltip("Packed into the woods on the inside of sharp bends, so you cannot see round the corner.")] BendThicket
    }

    /// <summary>A ground surface a placed object paints around itself.</summary>
    public enum ForestGroundMark
    {
        None,
        [Tooltip("Within about 3 trunk widths.")] Roots,
        [Tooltip("Under the whole crown.")] Needles,
        [Tooltip("Under the whole crown.")] LeafLitter,
        [Tooltip("Around the footprint.")] Moss,
        [Tooltip("Around the footprint.")] Dirt
    }

    public enum ForestRenderMode
    {
        [Tooltip("Instanced when the prefab is only meshes (+ colliders, LOD Group); a real GameObject when it has lights, scripts, particles or audio.")] Auto,
        [Tooltip("Drawn by the Forest Instanced Renderer: thousands of copies in a few draw calls. Colliders are rebuilt from the prefab.")] Instanced,
        [Tooltip("A real prefab instance in the scene for every copy. Use for things with behaviour; costs one draw call each.")] GameObjects
    }

    /// <summary>One model a category can place.</summary>
    [System.Serializable]
    public class ForestAssetVariant
    {
        [Tooltip("The prefab. Pivot at the base (where it meets the ground), +Y up, +Z forward, real-world metres. May have an LOD Group and colliders.")]
        public GameObject prefab;

        [Tooltip("How often this one is picked compared with the category's other variants.")]
        [Min(0f)] public float weight = 1f;

        [Tooltip("Random uniform size multiplier (min, max).")]
        public Vector2 scale = new Vector2(0.85f, 1.15f);

        [Tooltip("Extra random multiplier on the height only (min, max). 1, 1 = none.")]
        public Vector2 heightStretch = new Vector2(1f, 1f);

        [Tooltip("Radius in metres (at scale 1) of the ground it takes up, used to keep things from overlapping. 0 = worked out from the prefab (trunk collider for trees, bounds for the rest).")]
        [Min(0f)] public float footprint = 0f;
    }

    /// <summary>
    /// One kind of thing the forest places (conifers, bushes, boulders, streetlights...). Swap its variants for your own
    /// prefabs and tune where and how densely it goes; the generator does not care what the models look like.
    /// </summary>
    [System.Serializable]
    public class ForestAssetCategory
    {
        public string name = "Category";
        public bool enabled = true;

        [Tooltip("Zones scale categories by tag (Forest Settings > Zones > Tag Weights), e.g. Conifer, Deciduous, Birch, DeadTree, YoungConifer, Bush, Rock, Boulder, FallenLog.")]
        public string tag = "";

        public ForestAssetKind kind = ForestAssetKind.Tree;
        public ForestPlacement placement = ForestPlacement.Scatter;
        public List<ForestAssetVariant> variants = new List<ForestAssetVariant>();

        [Header("Density")]
        [Tooltip("Average number per 100 square metres where conditions suit it best.")]
        [Min(0f)] public float density = 1f;

        [Tooltip("Two of this category never stand closer than this many metres (centre to centre, at scale 1).")]
        [Min(0f)] public float spacing = 2f;

        [Tooltip("0 = spread evenly, 1 = in tight groups with open gaps between.")]
        [Range(0f, 1f)] public float clustering = 0.4f;

        [Tooltip("Size in metres of the groups.")]
        [Min(1f)] public float clusterSize = 14f;

        [Tooltip("Density multiplier on the raised boundary band (0 = never there).")]
        [Min(0f)] public float boundaryDensity = 0f;

        [Tooltip("Hard limit on how many are placed. 0 = no limit.")]
        [Min(0)] public int maxCount = 0;

        [Header("Where")]
        [Tooltip("Only on ground between these steepnesses (degrees).")]
        public Vector2 slope = new Vector2(0f, 35f);

        [Tooltip("Keep at least this many metres from the edge of every trail.")]
        public float trailDistance = 1f;

        [Tooltip("Only within this many metres of a trail edge, for things that line the trails. 0 = anywhere.")]
        [Min(0f)] public float maxTrailDistance = 0f;

        [Tooltip("Allowed inside clearings.")]
        public bool inClearings = false;

        [Tooltip("Grows more where this ground surface is strong (grass tufts on grass, for example).")]
        public bool followGround = false;
        public ForestGroundRole groundSurface = ForestGroundRole.Grass;
        [Range(0f, 1f)] public float groundInfluence = 0.8f;

        [Header("Placement")]
        [Tooltip("0 = stands upright, 1 = tilts with the ground.")]
        [Range(0f, 1f)] public float alignToSlope = 0f;

        [Tooltip("Random lean in degrees.")]
        [Range(0f, 45f)] public float randomTilt = 0f;

        public bool randomYaw = true;

        [Tooltip("Metres pushed into the ground, so the base never floats on slopes.")]
        public float sink = 0.05f;

        [Header("Gameplay")]
        [Tooltip("The monster cannot pass through it: kept out of monster routes, clearings and the monster gaps.")]
        public bool blocksMonster = false;

        [Tooltip("Rebuild the prefab's colliders for every copy. Off = decoration only: no physics cost.")]
        public bool colliders = true;

        [Header("Rendering")]
        public ForestRenderMode render = ForestRenderMode.Auto;

        [Tooltip("Not drawn beyond this many metres. 0 = until the fog hides it.")]
        [Min(0f)] public float drawDistance = 0f;

        public bool castShadows = false;

        [Tooltip("Multiplier on the distances where the level of detail switches.")]
        [Range(0.25f, 4f)] public float lodScale = 1f;

        [Header("Effect On The Ground")]
        [Tooltip("How much shade its crown throws on the ground (baked into the ground light map: darker under dense trees, dappled sunlight between). 0 = none.")]
        [Range(0f, 1f)] public float canopyShade = 0f;

        [Tooltip("Paints a ground surface around it: roots around big trunks, needles under conifers, leaves under broadleaf trees, moss around rocks.")]
        public ForestGroundMark groundMark = ForestGroundMark.None;

        public bool HasUsableVariant
        {
            get
            {
                if (variants == null) return false;
                foreach (ForestAssetVariant v in variants) if (v != null && v.prefab != null && v.weight > 0f) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// The models the forest is built from, by category. Generation logic never refers to a specific model: replace the
    /// placeholder prefabs with your own here (several variants per category, weighted), add categories, or switch them
    /// off. See the README in Assets/ForestGenerator for what a prefab needs (pivot, scale, colliders, LODs, materials).
    /// </summary>
    [CreateAssetMenu(menuName = "Havoc/Forest/Asset Library", fileName = "ForestAssetLibrary", order = 1)]
    public class ForestAssetLibrary : ScriptableObject
    {
        public List<ForestAssetCategory> categories = new List<ForestAssetCategory>();
    }
}
