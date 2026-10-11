using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Menu: Tools > Havoc Forest.
    ///   Create Default Assets   the settings, ground layers (with placeholder textures), ground material and atmosphere
    ///                           profiles in Assets/ForestGenerator. Existing assets are kept; only missing ones are made.
    ///   Create Forest Scene     a new scene (Assets/Scenes/Forest.unity) with a sun, the Forest object, a generated forest,
    ///                           the player rig and multiplayer (see ForestGameSetup), added to the Build Settings.
    /// </summary>
    public static class ForestSetup
    {
        public const string Root = "Assets/ForestGenerator";
        public const string SettingsDir = Root + "/Settings";
        public const string MaterialsDir = Root + "/Materials";
        public const string TexturesDir = Root + "/Textures";
        public const string PlaceholderDir = TexturesDir + "/Ground";
        public const string ScenePath = "Assets/Scenes/Forest.unity";
        const string RigSourceScene = "Assets/Scenes/ClaudeTest.unity";
        const string RigFallbackPrefab = "Assets/Resources/GorillaPrefabs/Gorilla Rig.prefab";

        public struct Defaults
        {
            public ForestSettings settings;
            public ForestGroundLayers layers;
            public Material groundMaterial;
            public Material vegetationMaterial;
            public ForestAssetLibrary library;
            public ForestAtmosphereProfile calm;
            public ForestAtmosphereProfile dread;
        }

        static readonly string[] LayerNames = { "Dirt", "Gravel", "Moss", "Rock", "Grass", "LeafLitter", "Roots", "Needles" };

        [MenuItem("Tools/Havoc Forest/Create Default Assets", priority = 10)]
        static void CreateDefaultAssetsMenu()
        {
            Defaults d = CreateDefaultAssets(false);
            Selection.activeObject = d.settings;
            EditorGUIUtility.PingObject(d.settings);
        }

        [MenuItem("Tools/Havoc Forest/Create Forest Scene", priority = 11)]
        static void CreateForestSceneMenu()
        {
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Havoc Forest", ScenePath + " already exists. Replace it with a new forest scene?", "Replace", "Cancel")) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CreateForestScene();
        }

        [MenuItem("Tools/Havoc Forest/Rebuild Placeholder Ground Textures", priority = 30)]
        static void RebuildPlaceholdersMenu()
        {
            Defaults d = CreateDefaultAssets(true);
            string msg;
            ForestTextureArrays.Build(d.layers, out msg);
            Debug.Log("Havoc Forest: " + msg);
        }

        // ---------------------------------------------------------------- default assets

        public static Defaults CreateDefaultAssets(bool repaintPlaceholders)
        {
            EnsureFolder(SettingsDir);
            EnsureFolder(MaterialsDir);
            EnsureFolder(PlaceholderDir);

            var d = new Defaults();
            bool wrotePlaceholders = false;

            // placeholder ground textures
            for (int i = 0; i < LayerNames.Length; i++)
            {
                string albedoPath = PlaceholderDir + "/Placeholder_" + LayerNames[i] + "_Albedo.png";
                string normalPath = PlaceholderDir + "/Placeholder_" + LayerNames[i] + "_Normal.png";
                if (repaintPlaceholders || !File.Exists(albedoPath) || !File.Exists(normalPath))
                {
                    WritePlaceholder((ForestGroundRole)i, albedoPath, normalPath);
                    wrotePlaceholders = true;
                }
            }
            string macroPath = TexturesDir + "/ForestMacroNoise.png";
            if (repaintPlaceholders || !File.Exists(macroPath)) WriteMacroNoise(macroPath);

            // the ground material
            string matPath = MaterialsDir + "/ForestGround.mat";
            d.groundMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (d.groundMaterial == null)
            {
                d.groundMaterial = new Material(Shader.Find("Havoc/Forest Ground")) { name = "ForestGround" };
                d.groundMaterial.SetTexture("_MacroNoise", AssetDatabase.LoadAssetAtPath<Texture2D>(macroPath));
                d.groundMaterial.EnableKeyword("_FOREST_NORMALMAPS");
                AssetDatabase.CreateAsset(d.groundMaterial, matPath);
            }

            // the ground layers
            string layersPath = SettingsDir + "/ForestGroundLayers.asset";
            d.layers = AssetDatabase.LoadAssetAtPath<ForestGroundLayers>(layersPath);
            bool newLayers = d.layers == null;
            if (newLayers)
            {
                d.layers = ScriptableObject.CreateInstance<ForestGroundLayers>();
                AssetDatabase.CreateAsset(d.layers, layersPath);
            }
            for (int i = 0; i < LayerNames.Length && i < d.layers.layers.Length; i++)
            {
                ForestGroundLayer l = d.layers.layers[i];
                if (l.albedo == null) l.albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(PlaceholderDir + "/Placeholder_" + LayerNames[i] + "_Albedo.png");
                if (l.normal == null) l.normal = AssetDatabase.LoadAssetAtPath<Texture2D>(PlaceholderDir + "/Placeholder_" + LayerNames[i] + "_Normal.png");
            }
            if (d.layers.material == null) d.layers.material = d.groundMaterial;
            EditorUtility.SetDirty(d.layers);
            if (newLayers || wrotePlaceholders || d.layers.albedoArray == null || d.layers.normalArray == null)
            {
                string msg;
                ForestTextureArrays.Build(d.layers, out msg);
            }

            // atmosphere: a foggy night (Calm / Dread), and the daytime look kept as two presets to swap in
            d.calm = LoadOrCreate<ForestAtmosphereProfile>(SettingsDir + "/ForestAtmosphere_Calm.asset", NightCalm);
            d.dread = LoadOrCreate<ForestAtmosphereProfile>(SettingsDir + "/ForestAtmosphere_Dread.asset", NightDread);
            LoadOrCreate<ForestAtmosphereProfile>(SettingsDir + "/ForestAtmosphere_DayCalm.asset", DayCalm);
            LoadOrCreate<ForestAtmosphereProfile>(SettingsDir + "/ForestAtmosphere_DayDread.asset", DayDread);

            // the flashlight's beam pattern
            ConfigureCookie(FlashlightCookiePath);

            // the settings
            d.settings = LoadOrCreate<ForestSettings>(SettingsDir + "/ForestSettings.asset", s => { });
            if (d.settings.atmosphere.calm == null) d.settings.atmosphere.calm = d.calm;
            if (d.settings.atmosphere.dread == null) d.settings.atmosphere.dread = d.dread;
            EditorUtility.SetDirty(d.settings);

            // the placeholder models and the asset library that uses them
            string vegPath = MaterialsDir + "/ForestVegetation.mat";
            d.vegetationMaterial = AssetDatabase.LoadAssetAtPath<Material>(vegPath);
            if (d.vegetationMaterial == null)
            {
                d.vegetationMaterial = new Material(Shader.Find("Havoc/Forest Vegetation")) { name = "ForestVegetation", enableInstancing = true };
                AssetDatabase.CreateAsset(d.vegetationMaterial, vegPath);
            }
            Dictionary<string, GameObject> models = ForestPlaceholderModels.CreateAll(d.vegetationMaterial, repaintPlaceholders);

            // the streetlights' glowing glass and halo
            string glassPath = MaterialsDir + "/ForestLampGlass.mat";
            Material glass = AssetDatabase.LoadAssetAtPath<Material>(glassPath);
            if (glass == null)
            {
                glass = new Material(Shader.Find("Havoc/Forest Vegetation")) { name = "ForestLampGlass", enableInstancing = true };
                glass.SetColor("_BaseColor", new Color(0.9f, 0.85f, 0.7f));
                glass.SetColor("_EmissionColor", new Color(1f, 0.8f, 0.6f) * 2f);
                glass.SetFloat("_WindStrength", 0f);
                AssetDatabase.CreateAsset(glass, glassPath);
            }
            string glowPath = MaterialsDir + "/ForestLampGlow.mat";
            Material glow = AssetDatabase.LoadAssetAtPath<Material>(glowPath);
            if (glow == null)
            {
                glow = new Material(Shader.Find("Havoc/Forest Lamp Glow")) { name = "ForestLampGlow", enableInstancing = true };
                AssetDatabase.CreateAsset(glow, glowPath);
            }
            foreach (KeyValuePair<string, GameObject> kv in ForestPlaceholderModels.CreateStreetLamps(d.vegetationMaterial, glass, glow, repaintPlaceholders))
                models[kv.Key] = kv.Value;
            string libPath = SettingsDir + "/ForestAssetLibrary.asset";
            d.library = AssetDatabase.LoadAssetAtPath<ForestAssetLibrary>(libPath);
            if (d.library == null)
            {
                d.library = ScriptableObject.CreateInstance<ForestAssetLibrary>();
                d.library.categories = DefaultCategories(models);
                AssetDatabase.CreateAsset(d.library, libPath);
            }

            AssetDatabase.SaveAssets();
            return d;
        }

        /// <summary>The default categories, all pointing at the placeholder models.</summary>
        static List<ForestAssetCategory> DefaultCategories(Dictionary<string, GameObject> m)
        {
            System.Func<Vector2, string[], List<ForestAssetVariant>> variants = (scale, names) =>
            {
                var list = new List<ForestAssetVariant>();
                foreach (string n in names)
                {
                    GameObject p;
                    if (m.TryGetValue(n, out p)) list.Add(new ForestAssetVariant { prefab = p, weight = 1f, scale = scale });
                }
                return list;
            };

            return new List<ForestAssetCategory>
            {
                // ---- trees
                new ForestAssetCategory
                {
                    name = "Conifers", tag = "Conifer", kind = ForestAssetKind.Tree,
                    variants = variants(new Vector2(0.8f, 1.15f), new[] { "ConiferA", "ConiferB", "ConiferC" }),
                    density = 2.6f, spacing = 2.7f, clustering = 0.35f, clusterSize = 22f, boundaryDensity = 1.3f,
                    slope = new Vector2(0f, 45f), trailDistance = 0.2f, randomTilt = 2f, sink = 0.15f,
                    blocksMonster = true, colliders = true, canopyShade = 0.85f, groundMark = ForestGroundMark.Needles
                },
                new ForestAssetCategory
                {
                    name = "Young Conifers", tag = "YoungConifer", kind = ForestAssetKind.Tree,
                    variants = variants(new Vector2(0.7f, 1.3f), new[] { "YoungConiferA", "YoungConiferB" }),
                    density = 0.9f, spacing = 1.8f, clustering = 0.7f, clusterSize = 12f, boundaryDensity = 2f,
                    slope = new Vector2(0f, 45f), trailDistance = 1f, randomTilt = 4f, sink = 0.1f,
                    blocksMonster = false, colliders = true, canopyShade = 0.3f, groundMark = ForestGroundMark.Needles
                },
                new ForestAssetCategory
                {
                    name = "Autumn Trees", tag = "Deciduous", kind = ForestAssetKind.Tree,
                    variants = variants(new Vector2(0.8f, 1.2f), new[] { "AutumnTreeA", "AutumnTreeB", "AutumnTreeC" }),
                    density = 0.6f, spacing = 4f, clustering = 0.5f, clusterSize = 18f, boundaryDensity = 0.8f,
                    slope = new Vector2(0f, 35f), trailDistance = 0.6f, randomTilt = 3f, sink = 0.1f,
                    blocksMonster = true, colliders = true, canopyShade = 0.7f, groundMark = ForestGroundMark.LeafLitter
                },
                new ForestAssetCategory
                {
                    name = "Birches", tag = "Birch", kind = ForestAssetKind.Tree,
                    variants = variants(new Vector2(0.8f, 1.15f), new[] { "BirchA", "BirchB" }),
                    density = 0.45f, spacing = 2.6f, clustering = 0.7f, clusterSize = 16f, boundaryDensity = 0.5f,
                    slope = new Vector2(0f, 35f), trailDistance = 0.4f, randomTilt = 5f, sink = 0.1f,
                    blocksMonster = true, colliders = true, canopyShade = 0.4f, groundMark = ForestGroundMark.LeafLitter
                },
                new ForestAssetCategory
                {
                    name = "Dead Trees", tag = "DeadTree", kind = ForestAssetKind.Tree,
                    variants = variants(new Vector2(0.85f, 1.2f), new[] { "DeadTreeA", "DeadTreeB" }),
                    density = 0.12f, spacing = 6f, clustering = 0.3f, clusterSize = 25f, boundaryDensity = 0.6f,
                    slope = new Vector2(0f, 40f), trailDistance = 0.5f, randomTilt = 7f, sink = 0.15f,
                    blocksMonster = true, colliders = true, canopyShade = 0.12f, groundMark = ForestGroundMark.Roots
                },

                // ---- undergrowth
                new ForestAssetCategory
                {
                    name = "Bushes", tag = "Bush", kind = ForestAssetKind.Undergrowth,
                    variants = variants(new Vector2(0.7f, 1.4f), new[] { "BushA", "BushB" }),
                    density = 2.8f, spacing = 1.5f, clustering = 0.6f, clusterSize = 10f, boundaryDensity = 1.5f,
                    slope = new Vector2(0f, 40f), trailDistance = 0.3f, sink = 0.05f, colliders = false,
                    drawDistance = 55f, canopyShade = 0.15f
                },
                new ForestAssetCategory
                {
                    name = "Ferns", tag = "Fern", kind = ForestAssetKind.Undergrowth,
                    variants = variants(new Vector2(0.7f, 1.3f), new[] { "Fern" }),
                    density = 4f, spacing = 0.9f, clustering = 0.65f, clusterSize = 8f,
                    slope = new Vector2(0f, 35f), trailDistance = 0.1f, followGround = true, groundSurface = ForestGroundRole.Moss, groundInfluence = 0.5f,
                    colliders = false, drawDistance = 38f
                },
                new ForestAssetCategory
                {
                    name = "Bend Thickets", tag = "Bush", kind = ForestAssetKind.Undergrowth, placement = ForestPlacement.BendThicket,
                    variants = variants(new Vector2(0.9f, 1.4f), new[] { "BushA", "BushB", "YoungConiferA", "YoungConiferB" }),
                    density = 10f, spacing = 1.5f, slope = new Vector2(0f, 45f), trailDistance = 0.6f, sink = 0.05f, colliders = false,
                    drawDistance = 60f, canopyShade = 0.2f
                },

                // ---- ground cover (no colliders)
                new ForestAssetCategory
                {
                    name = "Grass Tufts", tag = "Grass", kind = ForestAssetKind.GroundCover,
                    variants = variants(new Vector2(0.7f, 1.4f), new[] { "GrassTuftA", "GrassTuftB" }),
                    density = 18f, spacing = 0.42f, clustering = 0.5f, clusterSize = 5f, inClearings = true,
                    slope = new Vector2(0f, 35f), trailDistance = -0.3f, followGround = true, groundSurface = ForestGroundRole.Grass, groundInfluence = 0.9f,
                    alignToSlope = 0.6f, colliders = false, drawDistance = 26f
                },
                new ForestAssetCategory
                {
                    name = "Fallen Branches", tag = "Branch", kind = ForestAssetKind.GroundCover,
                    variants = variants(new Vector2(0.7f, 1.3f), new[] { "FallenBranchA", "FallenBranchB" }),
                    density = 2f, spacing = 1.1f, clustering = 0.4f, inClearings = true, trailDistance = 0.1f,
                    slope = new Vector2(0f, 40f), alignToSlope = 1f, colliders = false, drawDistance = 30f
                },
                new ForestAssetCategory
                {
                    name = "Pebbles", tag = "Rock", kind = ForestAssetKind.GroundCover,
                    variants = variants(new Vector2(0.7f, 1.4f), new[] { "Pebbles" }),
                    density = 1.5f, spacing = 1f, clustering = 0.5f, inClearings = true, trailDistance = -0.6f,
                    slope = new Vector2(0f, 50f), alignToSlope = 1f, colliders = false, drawDistance = 24f
                },
                new ForestAssetCategory
                {
                    name = "Exposed Roots", tag = "Roots", kind = ForestAssetKind.GroundCover,
                    variants = variants(new Vector2(0.8f, 1.3f), new[] { "ExposedRoots" }),
                    density = 0.8f, spacing = 2f, trailDistance = -0.4f, followGround = true, groundSurface = ForestGroundRole.Roots, groundInfluence = 1f,
                    slope = new Vector2(0f, 40f), alignToSlope = 1f, colliders = false, drawDistance = 32f
                },

                // ---- solid obstacles
                new ForestAssetCategory
                {
                    name = "Rocks", tag = "Rock", kind = ForestAssetKind.Obstacle,
                    variants = variants(new Vector2(0.7f, 1.4f), new[] { "RockA", "RockB", "RockC" }),
                    density = 0.35f, spacing = 2.5f, clustering = 0.55f, clusterSize = 14f, boundaryDensity = 2f,
                    slope = new Vector2(0f, 50f), trailDistance = 0.3f, alignToSlope = 0.6f, sink = 0.05f,
                    blocksMonster = true, colliders = true, drawDistance = 60f, groundMark = ForestGroundMark.Moss
                },
                new ForestAssetCategory
                {
                    name = "Boulders", tag = "Boulder", kind = ForestAssetKind.Obstacle,
                    variants = variants(new Vector2(0.8f, 1.5f), new[] { "BoulderA", "BoulderB" }),
                    density = 0.05f, spacing = 12f, clustering = 0.4f, boundaryDensity = 3f,
                    slope = new Vector2(0f, 50f), trailDistance = 0.8f, alignToSlope = 0.4f, sink = 0.1f,
                    blocksMonster = true, colliders = true, groundMark = ForestGroundMark.Moss
                },
                new ForestAssetCategory
                {
                    name = "Fallen Logs", tag = "FallenLog", kind = ForestAssetKind.Obstacle,
                    variants = variants(new Vector2(0.8f, 1.2f), new[] { "FallenLogA", "FallenLogB" }),
                    density = 0.08f, spacing = 8f, clustering = 0.3f, boundaryDensity = 1.5f,
                    slope = new Vector2(0f, 30f), trailDistance = 0.8f, alignToSlope = 1f, sink = 0.08f,
                    blocksMonster = true, colliders = true, groundMark = ForestGroundMark.Moss
                },
                new ForestAssetCategory
                {
                    name = "Stumps", tag = "Stump", kind = ForestAssetKind.Obstacle,
                    variants = variants(new Vector2(0.8f, 1.3f), new[] { "Stump" }),
                    density = 0.12f, spacing = 5f, trailDistance = 0.3f, slope = new Vector2(0f, 35f), sink = 0.05f,
                    blocksMonster = false, colliders = true, drawDistance = 50f, groundMark = ForestGroundMark.Roots
                },

                // ---- rock formations along the raised boundary (where the edge is a cliff or a rocky incline)
                new ForestAssetCategory
                {
                    name = "Boundary Rocks", tag = "CliffRock", kind = ForestAssetKind.Special, placement = ForestPlacement.BoundaryRock,
                    variants = variants(new Vector2(0.8f, 1.3f), new[] { "CliffRockA", "CliffRockB", "CliffRockC" }),
                    density = 1f, spacing = 2.2f, alignToSlope = 0.5f, sink = 0.6f, blocksMonster = true, colliders = true,
                    groundMark = ForestGroundMark.Moss
                },

                // ---- streetlights (how many, and how many green, dead or flickering, is in Forest Settings > Streetlights)
                new ForestAssetCategory
                {
                    name = "Streetlights", tag = "Streetlight", kind = ForestAssetKind.Special, placement = ForestPlacement.Streetlight,
                    variants = variants(new Vector2(0.95f, 1.05f), new[] { "StreetLampA", "StreetLampB" }),
                    density = 1f, sink = 0.2f, blocksMonster = false, colliders = true, groundMark = ForestGroundMark.Dirt
                },

                // ---- landmarks (one per landmark site)
                new ForestAssetCategory
                {
                    name = "Landmarks", tag = "Landmark", kind = ForestAssetKind.Special, placement = ForestPlacement.Landmark,
                    variants = variants(new Vector2(0.95f, 1.05f), new[] { "GiantDeadTree", "SplitBoulder", "StoneCairn", "OldSign" }),
                    density = 1f, sink = 0.1f, blocksMonster = true, colliders = true, canopyShade = 0.2f, groundMark = ForestGroundMark.Roots
                }
            };
        }

        // ---------------------------------------------------------------- atmosphere looks

        /// <summary>The trailhead at night: thin mist lit by a low moon (the profile's own defaults).</summary>
        public static void NightCalm(ForestAtmosphereProfile p)
        {
            var fresh = ScriptableObject.CreateInstance<ForestAtmosphereProfile>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(fresh), p);
            Object.DestroyImmediate(fresh);
        }

        /// <summary>Deep in at night: thicker, darker fog, the moon nearly gone, lamps brighter but unreliable.</summary>
        public static void NightDread(ForestAtmosphereProfile p)
        {
            NightCalm(p);
            p.fogColor = new Color(0.045f, 0.055f, 0.072f);
            p.fogDensity = 0.046f;
            p.skyAmbient = new Color(0.055f, 0.065f, 0.09f);
            p.horizonAmbient = new Color(0.032f, 0.037f, 0.05f);
            p.groundAmbient = new Color(0.01f, 0.01f, 0.012f);
            p.sunColor = new Color(0.55f, 0.65f, 0.9f);
            p.sunIntensity = 0.025f;
            p.lampBrightness = 1.4f;
            p.lampDropouts = 0.12f;
        }

        /// <summary>The original daytime look (overcast, misty), trailhead.</summary>
        public static void DayCalm(ForestAtmosphereProfile p)
        {
            p.fogColor = new Color(0.55f, 0.6f, 0.62f);
            p.fogDensity = 0.028f;
            p.skyAmbient = new Color(0.42f, 0.47f, 0.52f);
            p.horizonAmbient = new Color(0.32f, 0.35f, 0.36f);
            p.groundAmbient = new Color(0.15f, 0.14f, 0.12f);
            p.sunColor = new Color(1f, 0.9f, 0.76f);
            p.sunIntensity = 0.85f;
            p.sunElevation = 32f;
            p.sunAzimuth = 225f;
            p.lampBrightness = 1f;
            p.lampDropouts = 0f;
        }

        /// <summary>The original daytime look, deep in.</summary>
        public static void DayDread(ForestAtmosphereProfile p)
        {
            DayCalm(p);
            p.fogColor = new Color(0.33f, 0.37f, 0.38f);
            p.fogDensity = 0.046f;
            p.skyAmbient = new Color(0.27f, 0.31f, 0.34f);
            p.horizonAmbient = new Color(0.2f, 0.22f, 0.23f);
            p.groundAmbient = new Color(0.09f, 0.085f, 0.08f);
            p.sunColor = new Color(0.82f, 0.87f, 0.9f);
            p.sunIntensity = 0.35f;
            p.sunElevation = 20f;
            p.lampBrightness = 1.3f;
            p.lampDropouts = 0.12f;
        }

        // ---------------------------------------------------------------- flashlight

        public const string FlashlightCookiePath = TexturesDir + "/FlashlightCookie.png";

        /// <summary>
        /// Imports the flashlight's beam pattern for a spot light cookie: grey RGB, linear, clamped edges. Not Unity's
        /// "Cookie" texture type: that makes an alpha-only texture, and URP copies cookies into its cookie atlas by their
        /// colour (Quest's grayscale atlas keeps the red channel), so an alpha-only cookie comes out black there.
        /// </summary>
        public static void ConfigureCookie(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            if (importer.textureType == TextureImporterType.Default && !importer.sRGBTexture && importer.wrapMode == TextureWrapMode.Clamp
                && importer.alphaSource == TextureImporterAlphaSource.None && importer.maxTextureSize == 256) return;
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 256;          // a soft pattern: the cookie atlas scales it anyway
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        /// <summary>Puts the flashlight (A button) on a forest object, with the default cookie.</summary>
        public static ForestFlashlight AddFlashlight(GameObject forest)
        {
            ForestFlashlight f = forest.GetComponent<ForestFlashlight>();
            if (f != null) return f;
            ConfigureCookie(FlashlightCookiePath);
            f = forest.AddComponent<ForestFlashlight>();
            f.cookie = AssetDatabase.LoadAssetAtPath<Texture>(FlashlightCookiePath);
            return f;
        }

        static T LoadOrCreate<T>(string path, System.Action<T> init) where T : ScriptableObject
        {
            T a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null) return a;
            a = ScriptableObject.CreateInstance<T>();
            init(a);
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static void WritePlaceholder(ForestGroundRole role, string albedoPath, string normalPath)
        {
            const int size = 512;
            ForestPlaceholderTextures.Result r = ForestPlaceholderTextures.Paint(role, size, 1000 + (int)role * 77);
            var albedo = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++)
            {
                Color c = r.albedo[i];
                c.a = r.height[i];
                px[i] = c;
            }
            albedo.SetPixels(px);
            File.WriteAllBytes(albedoPath, albedo.EncodeToPNG());
            Object.DestroyImmediate(albedo);

            var normal = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            normal.SetPixels(ForestPlaceholderTextures.NormalsFromHeight(r.height, size, ForestPlaceholderTextures.NormalStrength(role)));
            File.WriteAllBytes(normalPath, normal.EncodeToPNG());
            Object.DestroyImmediate(normal);

            AssetDatabase.ImportAsset(albedoPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(normalPath, ImportAssetOptions.ForceSynchronousImport);
            var ai = (TextureImporter)AssetImporter.GetAtPath(albedoPath);
            ai.alphaSource = TextureImporterAlphaSource.FromInput;
            ai.alphaIsTransparency = false;
            ai.wrapMode = TextureWrapMode.Repeat;
            ai.SaveAndReimport();
            var ni = (TextureImporter)AssetImporter.GetAtPath(normalPath);
            ni.textureType = TextureImporterType.NormalMap;
            ni.wrapMode = TextureWrapMode.Repeat;
            ni.SaveAndReimport();
        }

        static void WriteMacroNoise(string path)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.SetPixels(ForestPlaceholderTextures.MacroNoise(size, 4242));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.sRGBTexture = false;
            imp.alphaSource = TextureImporterAlphaSource.FromInput;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.SaveAndReimport();
        }

        // ---------------------------------------------------------------- the scene

        public static string CreateForestScene()
        {
            CreateDefaultAssets(false);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Defaults d = CreateDefaultAssets(false);

            var sunGO = new GameObject("Moon");
            Light sun = sunGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;       // Quest: no real-time sun shadows; the canopy shade is baked into the ground
            sun.intensity = 0.07f;          // the moon: Forest Atmosphere sets its colour, brightness and angle from the profiles

            var forestGO = new GameObject("Forest");
            var gen = forestGO.AddComponent<ForestGenerator>();
            gen.settings = d.settings;
            gen.groundLayers = d.layers;
            gen.assets = d.library;
            var atmosphere = forestGO.AddComponent<ForestAtmosphere>();
            atmosphere.calm = d.calm;
            atmosphere.dread = d.dread;
            atmosphere.sun = sun;
            AddFlashlight(forestGO);
            gen.setupVersion = ForestSceneUpgrade.CurrentVersion;

            EditorSceneManager.SaveScene(scene, ScenePath);     // first, so the saved forest files are named after the scene
            gen.Generate();
            atmosphere.RefreshFogVolume();     // the volumetric fog box, sized to the new forest
            ForestGameSetup.Run(scene);        // the player rig and multiplayer, on the trailhead

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            return ScenePath;
        }

        /// <summary>Copies the Gorilla rig from the ClaudeTest scene (it has your locomotion settings); falls back to the stock prefab.
        /// Used by ForestGameSetup when the Gorilla Rig prefab is missing.</summary>
        internal static GameObject CopyRig(Scene target)
        {
            GameObject copy = null;
            if (File.Exists(RigSourceScene))
            {
                Scene src = EditorSceneManager.OpenScene(RigSourceScene, OpenSceneMode.Additive);
                try
                {
                    GameObject rigRoot = null;
                    foreach (GameObject root in src.GetRootGameObjects())
                    {
                        var player = root.GetComponentInChildren<GorillaLocomotion.Player>(true);
                        if (player == null) continue;
                        Transform t = player.transform;
                        while (t.parent != null && t.parent.parent != null) t = t.parent;   // the child of the scene root that holds the player
                        rigRoot = t.gameObject;
                        break;
                    }
                    if (rigRoot != null)
                    {
                        copy = Object.Instantiate(rigRoot);
                        copy.name = rigRoot.name;
                        copy.transform.SetParent(null, true);
                        SceneManager.MoveGameObjectToScene(copy, target);
                    }
                }
                finally
                {
                    EditorSceneManager.CloseScene(src, true);
                }
            }
            if (copy == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigFallbackPrefab);
                if (prefab != null) copy = (GameObject)PrefabUtility.InstantiatePrefab(prefab, target);
            }
            if (copy == null) Debug.LogWarning("Havoc Forest: no Gorilla rig found to copy; add your player rig to the Forest scene yourself.");
            return copy;
        }

        public static void AddToBuildSettings(string path)
        {
            List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
            int existing = scenes.FindIndex(s => s.path == path);
            if (existing >= 0)
            {
                if (scenes[existing].enabled) return;
                scenes[existing].enabled = true;      // listed but switched off
            }
            else scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
