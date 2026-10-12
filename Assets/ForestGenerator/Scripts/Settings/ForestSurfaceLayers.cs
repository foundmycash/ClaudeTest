using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// The surfaces the generated models are made of. Every vertex of a placeholder model says which one it is, so a trunk
    /// gets bark and the crown gets needles while the whole forest still shares one material and one draw per model.
    /// The order is fixed: it is the slice number stored in the models' texture coordinates.
    /// </summary>
    public enum ForestSurface
    {
        Bark = 0,
        BirchBark = 1,
        Needles = 2,
        Leaves = 3,
        Blades = 4,
        Stone = 5,
        Iron = 6,
        Wood = 7
    }

    /// <summary>One surface: the texture that gives it its pattern.</summary>
    [System.Serializable]
    public class ForestSurfaceLayer
    {
        public string name = "Surface";

        [Tooltip("A seamlessly tiling colour texture. Only its pattern is used (light and dark, and a little colour variation): the average colour comes from the model's vertex colours, so a photo of bark on a brown trunk does not make it browner. Empty = the surface stays plain.")]
        public Texture2D albedo;
    }

    /// <summary>
    /// Surface textures for the generated (placeholder) models: bark, needles, leaves, stone and so on, packed into one
    /// texture array that the shared Havoc/Forest Vegetation material samples. Press Build Texture Array after changing a
    /// texture. Untick Use Surface Textures to go back to the plain vertex-colour look (nothing else needs to change).
    /// </summary>
    [CreateAssetMenu(menuName = "Havoc/Forest/Surface Layers", fileName = "ForestSurfaceLayers", order = 3)]
    public class ForestSurfaceLayers : ScriptableObject
    {
        public const int LayerCount = 8;

        /// <summary>How many metres one repeat of each surface covers on the models (the models' texture coordinates are built with these, so changing them needs Rebuild Placeholder Models).</summary>
        public static readonly float[] TileMetres = { 1.0f, 1.0f, 0.7f, 1.0f, 0.3f, 1.2f, 0.8f, 0.8f };

        [Tooltip("Turns the surface textures on or off for every model that uses the shared vegetation material.")]
        public bool useSurfaceTextures = true;

        [Tooltip("0 = plain vertex colours, 1 = the full texture pattern.")]
        [Range(0f, 1f)] public float strength = 0.85f;

        [Tooltip("Bark, Birch Bark, Needles, Leaves, Blades (grass and ferns), Stone, Iron, Wood. The order matters: it is the slice number stored in the models.")]
        public ForestSurfaceLayer[] layers = CreateDefaults();

        [Tooltip("Size of every texture in the array. Other sizes are resized when the array is built. 512 is a good fit for Quest.")]
        public ForestMapResolution textureSize = ForestMapResolution._512;

        [Header("Built (do not edit)")]
        [Tooltip("The 8 textures, reduced to their pattern. Built from the layers above.")]
        public Texture2DArray array;

        [Tooltip("The shared vegetation material the array is written into.")]
        public Material material;

        public static ForestSurfaceLayer[] CreateDefaults()
        {
            return new[]
            {
                new ForestSurfaceLayer { name = "Bark" },
                new ForestSurfaceLayer { name = "Birch Bark" },
                new ForestSurfaceLayer { name = "Needles" },
                new ForestSurfaceLayer { name = "Leaves" },
                new ForestSurfaceLayer { name = "Blades (grass, ferns)" },
                new ForestSurfaceLayer { name = "Stone" },
                new ForestSurfaceLayer { name = "Iron" },
                new ForestSurfaceLayer { name = "Wood" }
            };
        }

        static readonly int ArrayId = Shader.PropertyToID("_SurfaceArray");
        static readonly int StrengthId = Shader.PropertyToID("_SurfaceStrength");
        static readonly int UseId = Shader.PropertyToID("_UseSurfaces");

        /// <summary>Writes the array, strength and on/off switch into a vegetation material.</summary>
        public void ApplyTo(Material mat)
        {
            if (mat == null || !mat.HasProperty(UseId)) return;
            bool on = useSurfaceTextures && array != null;
            mat.SetTexture(ArrayId, array);
            mat.SetFloat(StrengthId, strength);
            mat.SetFloat(UseId, on ? 1f : 0f);
            if (on) mat.EnableKeyword("_FOREST_SURFACES");
            else mat.DisableKeyword("_FOREST_SURFACES");
        }

        void OnValidate()
        {
            if (material != null) ApplyTo(material);
        }
    }
}
