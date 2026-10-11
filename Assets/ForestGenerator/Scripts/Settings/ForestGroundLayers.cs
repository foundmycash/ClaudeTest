using UnityEngine;

namespace HavocForest
{
    /// <summary>The textures and look of one ground surface.</summary>
    [System.Serializable]
    public class ForestGroundLayer
    {
        public string name = "Layer";

        [Tooltip("Colour texture. If Height is empty, the alpha channel is used as height when it has one, otherwise the brightness.")]
        public Texture2D albedo;

        [Tooltip("Optional normal map (a standard tangent-space normal map, OpenGL style: green up).")]
        public Texture2D normal;

        [Tooltip("Optional height map (white = high). Decides which surface wins where two blend: stones poke through dirt, moss fills the gaps.")]
        public Texture2D height;

        [Tooltip("Size in metres of one repeat of the texture on the ground.")]
        [Min(0.1f)] public float tileSize = 3f;

        [Tooltip("Colour multiplier.")]
        public Color tint = Color.white;

        [Range(0f, 1f)] public float smoothness = 0.1f;

        [Range(0f, 2f)] public float normalStrength = 1f;
    }

    /// <summary>
    /// The 8 surfaces of the forest floor and the shared ground material built from them. The textures of all 8 are packed
    /// into two texture arrays (colour + height, and normals), so the single ground material can blend any of them in one
    /// pass. Press Build Texture Arrays (in the inspector) after changing textures; tiling, tint, smoothness and normal
    /// strength apply straight away.
    /// </summary>
    [CreateAssetMenu(menuName = "Havoc/Forest/Ground Layers", fileName = "ForestGroundLayers", order = 2)]
    public class ForestGroundLayers : ScriptableObject
    {
        public const int LayerCount = 8;

        [Tooltip("Dirt, Gravel, Moss, Rock, Grass, Leaf Litter, Roots, Needles. The order matters: it matches the ground painter's surfaces.")]
        public ForestGroundLayer[] layers = CreateDefaults();

        [Tooltip("Size of every texture in the arrays. Textures of other sizes are resized when the arrays are built. 512 is a good fit for Quest.")]
        public ForestMapResolution textureSize = ForestMapResolution._512;

        [Header("Built (do not edit)")]
        [Tooltip("Colour (rgb) and height (a) of all 8 surfaces. Built from the layers above.")]
        public Texture2DArray albedoArray;

        [Tooltip("Normals of all 8 surfaces. Built from the layers above.")]
        public Texture2DArray normalArray;

        [Tooltip("The shared ground material. Build Texture Arrays and Apply write the arrays and per-layer settings into it.")]
        public Material material;

        public static ForestGroundLayer[] CreateDefaults()
        {
            return new[]
            {
                new ForestGroundLayer { name = "Dirt", tileSize = 3f, smoothness = 0.08f },
                new ForestGroundLayer { name = "Gravel", tileSize = 2.2f, smoothness = 0.15f },
                new ForestGroundLayer { name = "Moss", tileSize = 2.6f, smoothness = 0.05f },
                new ForestGroundLayer { name = "Rock", tileSize = 4.5f, smoothness = 0.2f },
                new ForestGroundLayer { name = "Grass", tileSize = 2.2f, smoothness = 0.05f },
                new ForestGroundLayer { name = "Leaf Litter", tileSize = 2.4f, smoothness = 0.1f },
                new ForestGroundLayer { name = "Roots", tileSize = 3.2f, smoothness = 0.1f },
                new ForestGroundLayer { name = "Needles", tileSize = 2.4f, smoothness = 0.06f }
            };
        }

        static readonly int TileId0 = Shader.PropertyToID("_LayerTiling0");
        static readonly int TileId1 = Shader.PropertyToID("_LayerTiling1");
        static readonly int SmoothId0 = Shader.PropertyToID("_LayerSmoothness0");
        static readonly int SmoothId1 = Shader.PropertyToID("_LayerSmoothness1");
        static readonly int NormalId0 = Shader.PropertyToID("_LayerNormalStrength0");
        static readonly int NormalId1 = Shader.PropertyToID("_LayerNormalStrength1");
        static readonly int AlbedoArrayId = Shader.PropertyToID("_AlbedoArray");
        static readonly int NormalArrayId = Shader.PropertyToID("_NormalArray");

        /// <summary>Writes the arrays and the per-layer settings into the material.</summary>
        public void ApplyTo(Material mat)
        {
            if (mat == null || layers == null) return;
            var tiling = new float[LayerCount];
            var smooth = new float[LayerCount];
            var normal = new float[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                ForestGroundLayer l = i < layers.Length ? layers[i] : null;
                tiling[i] = l != null ? 1f / Mathf.Max(0.1f, l.tileSize) : 0.3f;
                smooth[i] = l != null ? l.smoothness : 0.1f;
                normal[i] = l != null ? l.normalStrength : 1f;
                if (mat.HasProperty("_Tint" + i)) mat.SetColor("_Tint" + i, l != null ? l.tint : Color.white);
            }
            mat.SetVector(TileId0, new Vector4(tiling[0], tiling[1], tiling[2], tiling[3]));
            mat.SetVector(TileId1, new Vector4(tiling[4], tiling[5], tiling[6], tiling[7]));
            mat.SetVector(SmoothId0, new Vector4(smooth[0], smooth[1], smooth[2], smooth[3]));
            mat.SetVector(SmoothId1, new Vector4(smooth[4], smooth[5], smooth[6], smooth[7]));
            mat.SetVector(NormalId0, new Vector4(normal[0], normal[1], normal[2], normal[3]));
            mat.SetVector(NormalId1, new Vector4(normal[4], normal[5], normal[6], normal[7]));
            if (albedoArray != null) mat.SetTexture(AlbedoArrayId, albedoArray);
            if (normalArray != null) mat.SetTexture(NormalArrayId, normalArray);
        }

        void OnValidate()
        {
            if (material != null) ApplyTo(material);
        }
    }
}
