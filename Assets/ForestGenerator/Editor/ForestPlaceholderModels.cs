using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HavocForest.EditorTools
{
    /// <summary>
    /// Builds the placeholder models (Assets/ForestGenerator/Placeholders): low-poly, vertex-coloured stand-ins with the right
    /// sizes, LOD Groups and colliders, so the forest works before your own models exist. Every prefab follows the rules your
    /// own prefabs need: pivot at the base, +Y up, metres, colliders only where something should be solid.
    /// They all share one material (Havoc/Forest Vegetation, GPU instancing on).
    /// </summary>
    public static class ForestPlaceholderModels
    {
        public const string Dir = ForestSetup.Root + "/Placeholders";
        const string MeshDir = Dir + "/Meshes";

        static readonly float[] TreeLods = { 0.45f, 0.2f, 0.004f };
        static readonly float[] SmallTreeLods = { 0.18f, 0.07f, 0.004f };
        static readonly float[] BushLods = { 0.1f, 0.004f };
        static readonly float[] RockLods = { 0.1f, 0.04f, 0.004f };
        static readonly float[] BoulderLods = { 0.12f, 0.05f, 0.004f };
        static readonly float[] LogLods = { 0.2f, 0.08f, 0.004f };
        static readonly float[] SingleLod = { 0.004f };

        delegate ForestMeshBuilder LodRecipe(int lod);

        /// <summary>Makes every placeholder prefab that is missing (or all of them when rebuild is true). Returns them by name.</summary>
        public static Dictionary<string, GameObject> CreateAll(Material mat, bool rebuild)
        {
            Ensure(Dir);
            Ensure(MeshDir);
            var made = new Dictionary<string, GameObject>();
            System.Action<string, int, LodRecipe, float[], System.Action<GameObject>> model = (name, lods, recipe, heights, colliders) =>
            {
                string path = Dir + "/" + name + ".prefab";
                GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (existing != null && !rebuild)
                {
                    made[name] = existing;
                    return;
                }
                var meshes = new Mesh[lods];
                for (int l = 0; l < lods; l++) meshes[l] = recipe(l).ToMesh(name + "_LOD" + l);
                meshes = SaveMeshes(name, meshes);
                made[name] = SavePrefab(path, name, meshes, heights, mat, colliders);
            };

            // ---- trees
            for (int v = 0; v < 3; v++)
            {
                int variant = v;
                model("Conifer" + "ABC"[v], 3, l => Conifer(l, variant), TreeLods, go => TrunkCollider(go, ConiferTrunk(variant), ConiferHeight(variant) * 0.92f));
            }
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("YoungConifer" + "AB"[v], 3, l => YoungConifer(l, variant), SmallTreeLods, go => TrunkCollider(go, 0.09f, 3.5f));
            }
            for (int v = 0; v < 3; v++)
            {
                int variant = v;
                model("AutumnTree" + "ABC"[v], 3, l => Broadleaf(l, variant), TreeLods, go => TrunkCollider(go, 0.3f, 5f));
            }
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("Birch" + "AB"[v], 3, l => Birch(l, variant), TreeLods, go => TrunkCollider(go, 0.16f, 14f));
            }
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("DeadTree" + "AB"[v], 3, l => DeadTree(l, variant), TreeLods, go => TrunkCollider(go, 0.3f, 12f));
            }

            // ---- undergrowth and ground cover (no colliders: walk through them)
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("Bush" + "AB"[v], 2, l => Bush(l, variant), BushLods, null);
            }
            model("Fern", 2, l => Fern(l), BushLods, null);
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("GrassTuft" + "AB"[v], 1, l => Grass(variant), SingleLod, null);
            }
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("FallenBranch" + "AB"[v], 1, l => Branch(variant), SingleLod, null);
            }
            model("Pebbles", 1, l => Pebbles(), SingleLod, null);
            model("ExposedRoots", 1, l => Roots(), SingleLod, null);

            // ---- solid obstacles
            for (int v = 0; v < 3; v++)
            {
                int variant = v;
                model("Rock" + "ABC"[v], 3, l => Rock(l, variant), RockLods, go => ConvexCollider(go, 1));
            }
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("Boulder" + "AB"[v], 3, l => Boulder(l, variant), BoulderLods, go => ConvexCollider(go, 1));
            }
            for (int v = 0; v < 3; v++)
            {
                int variant = v;
                model("CliffRock" + "ABC"[v], 3, l => CliffRock(l, variant), BoulderLods, go => ConvexCollider(go, 1));
            }
            for (int v = 0; v < 2; v++)
            {
                int variant = v;
                model("FallenLog" + "AB"[v], 3, l => Log(l, variant), LogLods, go =>
                {
                    var c = go.AddComponent<CapsuleCollider>();
                    c.direction = 0;
                    c.radius = LogRadius(variant);
                    c.height = LogLength(variant);
                    c.center = new Vector3(0f, LogRadius(variant), 0f);
                });
            }
            model("Stump", 2, l => Stump(l), BushLods, go => TrunkCollider(go, 0.42f, 0.8f));

            // ---- landmarks
            model("GiantDeadTree", 3, l => GiantDeadTree(l), TreeLods, go => TrunkCollider(go, 1.1f, 24f));
            model("SplitBoulder", 3, l => SplitBoulder(l), BoulderLods, go => ConvexCollider(go, 1));
            model("StoneCairn", 2, l => Cairn(l), BushLods, go => TrunkCollider(go, 0.55f, 1.8f));
            model("OldSign", 1, l => Sign(), SingleLod, go =>
            {
                var c = go.AddComponent<BoxCollider>();
                c.center = new Vector3(0f, 1.1f, 0f);
                c.size = new Vector3(0.16f, 2.2f, 0.16f);
            });

            AssetDatabase.SaveAssets();
            if (rebuild) RefreshOpenForests();
            return made;
        }

        // ---------------------------------------------------------------- streetlights

        /// <summary>
        /// The old streetlights: an iron pole with a curved arm (pointing along +Z, over the trail, about 6.4 m up so the
        /// monster passes under it), a shade, an emissive glass and a halo, with a Forest Lamp component wired to them.
        /// </summary>
        public static Dictionary<string, GameObject> CreateStreetLamps(Material body, Material glass, Material glow, bool rebuild)
        {
            Ensure(Dir);
            Ensure(MeshDir);
            var made = new Dictionary<string, GameObject>();
            for (int v = 0; v < 2; v++)
            {
                string name = "StreetLamp" + "AB"[v];
                string path = Dir + "/" + name + ".prefab";
                GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (existing != null && !rebuild)
                {
                    made[name] = existing;
                    continue;
                }
                Vector3 head;
                ForestMeshBuilder bodyMb = LampBody(v, out head);
                var glassMb = new ForestMeshBuilder();
                glassMb.Blob(head + Vector3.down * 0.08f, 0.15f, new Vector3(1f, 0.6f, 1f), 1, 0.05f, 2300 + v, mid => new Color(1f, 1f, 1f, 0f));
                Mesh[] meshes = SaveMeshes(name, new[] { bodyMb.ToMesh(name + "_Body"), glassMb.ToMesh(name + "_Glass"), Quad(name + "_Halo") });

                var root = new GameObject(name);
                try
                {
                    var bodyGO = new GameObject("Body");
                    bodyGO.transform.SetParent(root.transform, false);
                    bodyGO.AddComponent<MeshFilter>().sharedMesh = meshes[0];
                    var bodyR = bodyGO.AddComponent<MeshRenderer>();
                    bodyR.sharedMaterial = body;
                    bodyR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                    var glassGO = new GameObject("Glass");
                    glassGO.transform.SetParent(root.transform, false);
                    glassGO.AddComponent<MeshFilter>().sharedMesh = meshes[1];
                    var glassR = glassGO.AddComponent<MeshRenderer>();
                    glassR.sharedMaterial = glass;
                    glassR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                    var lightPoint = new GameObject("LightPoint");
                    lightPoint.transform.SetParent(root.transform, false);
                    lightPoint.transform.localPosition = head + Vector3.down * 0.25f;

                    var haloGO = new GameObject("Halo");
                    haloGO.transform.SetParent(lightPoint.transform, false);
                    haloGO.AddComponent<MeshFilter>().sharedMesh = meshes[2];
                    var haloR = haloGO.AddComponent<MeshRenderer>();
                    haloR.sharedMaterial = glow;
                    haloR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    haloR.receiveShadows = false;
                    haloR.localBounds = new Bounds(Vector3.zero, Vector3.one * 3f);     // the billboard is bigger than the quad

                    var col = root.AddComponent<CapsuleCollider>();
                    col.direction = 1;
                    col.radius = 0.12f;
                    col.height = 6.2f;
                    col.center = new Vector3(0f, 3.1f, 0f);

                    var lamp = root.AddComponent<ForestLamp>();
                    lamp.lightPoint = lightPoint.transform;
                    lamp.glass = new Renderer[] { glassR };
                    lamp.halo = haloR;

                    made[name] = PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }
            AssetDatabase.SaveAssets();
            if (rebuild) RefreshOpenForests();
            return made;
        }

        static ForestMeshBuilder LampBody(int v, out Vector3 head)
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Iron;
            float lean = v == 0 ? 0f : 0.35f;       // the second one leans and has a shorter arm
            float reach = v == 0 ? 1.6f : 1.25f;
            System.Func<float, float, Color> iron = (t, a) =>
            {
                float n = ForestNoise.Gradient(t * 14f, a * 1.3f, 2200 + v) * 0.5f + 0.5f;
                return Color.Lerp(Lin(0.16f, 0.15f, 0.14f), Lin(0.32f, 0.2f, 0.12f), n * n);     // dark iron, patches of rust
            };
            var top = new Vector3(lean, 6.3f, 0f);
            mb.Tube(new List<Vector3> { new Vector3(0f, -0.3f, 0f), new Vector3(0f, 0.45f, 0f) }, t => Mathf.Lerp(0.16f, 0.12f, t), 8, iron, false, true, 0.02f, 2210);
            mb.Tube(new List<Vector3> { new Vector3(0f, 0.45f, 0f), new Vector3(lean * 0.5f, 3.5f, 0f), top }, t => Mathf.Lerp(0.085f, 0.06f, t), 8, iron, false, true, 0.03f, 2211);
            head = new Vector3(lean, 6.36f, reach);
            var arm = new List<Vector3> { top + Vector3.down * 0.4f, top + new Vector3(0f, 0.05f, 0.35f), new Vector3(lean, 6.44f, reach * 0.6f), head + Vector3.up * 0.03f };
            mb.Tube(arm, t => Mathf.Lerp(0.05f, 0.035f, t), 6, iron, false, false);
            // the shade: a flared cone over the glass
            mb.Tube(new List<Vector3> { head + Vector3.up * 0.04f, head + Vector3.down * 0.2f }, t => Mathf.Lerp(0.07f, 0.34f, t), 10,
                    (t, a) => Lin(0.13f, 0.13f, 0.12f), true, false);
            return mb;
        }

        static Mesh Quad(string name)
        {
            var m = new Mesh { name = name };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateNormals();
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 3f);
            return m;
        }

        // ---------------------------------------------------------------- saving

        static void Ensure(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            Ensure(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>After a rebuild: every open forest rebuilds its draw lists from the new models, and the views repaint.</summary>
        static void RefreshOpenForests()
        {
            ForestPrefabInfo.ClearCache();
            foreach (ForestInstancedRenderer r in Object.FindObjectsByType<ForestInstancedRenderer>(FindObjectsSortMode.None)) r.Rebuild();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Overwrites dst with the geometry of src through the Mesh API. EditorUtility.CopySerialized looks the same on disk and
        /// in the Inspector, but Unity does not re-upload the mesh to the graphics card afterwards: a scene that is already open
        /// keeps drawing the OLD model until it is reloaded. Setting the data through the API makes the open scene follow at once.
        /// </summary>
        public static void CopyMeshData(Mesh src, Mesh dst)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            src.GetVertices(vertices);
            src.GetNormals(normals);
            src.GetColors(colors);
            int uvDimension = src.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)
                ? src.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0) : 0;
            var uv3 = new List<Vector3>();
            var uv2 = new List<Vector2>();
            if (uvDimension == 3) src.GetUVs(0, uv3);
            else if (uvDimension == 2) src.GetUVs(0, uv2);
            int subMeshes = src.subMeshCount;
            var triangles = new List<int>[subMeshes];
            for (int s = 0; s < subMeshes; s++)
            {
                triangles[s] = new List<int>();
                src.GetTriangles(triangles[s], s);
            }
            Bounds bounds = src.bounds;

            dst.Clear(false);
            dst.indexFormat = src.indexFormat;
            dst.SetVertices(vertices);
            if (normals.Count == vertices.Count) dst.SetNormals(normals);
            if (colors.Count == vertices.Count) dst.SetColors(colors);
            if (uvDimension == 3) dst.SetUVs(0, uv3);
            else if (uvDimension == 2) dst.SetUVs(0, uv2);
            dst.subMeshCount = subMeshes;
            for (int s = 0; s < subMeshes; s++) dst.SetTriangles(triangles[s], s, false);
            dst.bounds = bounds;
            EditorUtility.SetDirty(dst);
        }

        /// <summary>All levels of a model in one asset file; existing meshes are overwritten in place so references stay valid.</summary>
        static Mesh[] SaveMeshes(string name, Mesh[] meshes)
        {
            string path = MeshDir + "/" + name + ".asset";
            var existing = new Dictionary<string, Mesh>();
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var m = o as Mesh;
                if (m != null) existing[m.name] = m;
            }
            // Unity names a file's main mesh after the file, so the first level is not found under its own name: reuse the
            // main mesh for it, or every rebuild would add a second copy and leave the old one behind
            var main = AssetDatabase.LoadMainAssetAtPath(path) as Mesh;
            if (main != null && meshes.Length > 0 && !existing.ContainsKey(meshes[0].name)) existing[meshes[0].name] = main;
            var result = new Mesh[meshes.Length];
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh target;
                if (existing.TryGetValue(meshes[i].name, out target))
                {
                    CopyMeshData(meshes[i], target);
                    Object.DestroyImmediate(meshes[i]);
                }
                else
                {
                    target = meshes[i];
                    if (i == 0 && existing.Count == 0) AssetDatabase.CreateAsset(target, path);
                    else AssetDatabase.AddObjectToAsset(target, path);
                }
                // the main mesh keeps the file's name (Unity warns about every file whose main object is named differently)
                if (i == 0 && target.name != name)
                {
                    target.name = name;
                    EditorUtility.SetDirty(target);
                }
                result[i] = target;
            }
            return result;
        }

        static GameObject SavePrefab(string path, string name, Mesh[] meshes, float[] heights, Material mat, System.Action<GameObject> colliders)
        {
            var root = new GameObject(name);
            try
            {
                var lods = new LOD[meshes.Length];
                for (int i = 0; i < meshes.Length; i++)
                {
                    var child = new GameObject(meshes.Length > 1 ? "LOD" + i : "Mesh");
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                    var mr = child.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lods[i] = new LOD(heights[Mathf.Min(i, heights.Length - 1)], new Renderer[] { mr });
                }
                if (meshes.Length > 1)
                {
                    LODGroup group = root.AddComponent<LODGroup>();
                    group.SetLODs(lods);
                    group.RecalculateBounds();
                }
                if (colliders != null) colliders(root);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void TrunkCollider(GameObject go, float radius, float height)
        {
            var c = go.AddComponent<CapsuleCollider>();
            c.direction = 1;
            c.radius = radius;
            c.height = height;
            c.center = new Vector3(0f, height * 0.5f, 0f);
        }

        /// <summary>A convex mesh collider from one of the model's levels (a low one keeps it cheap).</summary>
        static void ConvexCollider(GameObject go, int lod)
        {
            MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>();
            MeshFilter f = filters[Mathf.Min(lod, filters.Length - 1)];
            var c = go.AddComponent<MeshCollider>();
            c.sharedMesh = f.sharedMesh;
            c.convex = true;
        }

        // ---------------------------------------------------------------- colour helpers

        static Color Lin(float r, float g, float b, float wind = 0f)
        {
            return ForestMeshBuilder.Lin(r, g, b, wind);
        }

        static Color Vary(Color c, float amount, float h)
        {
            float k = 1f + (h - 0.5f) * 2f * amount;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        static Color Bark(float t, float a, int seed, float r = 0.25f, float g = 0.2f, float b = 0.16f)
        {
            float n = ForestNoise.Gradient(t * 9f, a * 1.4f, seed) * 0.5f + 0.5f;
            return Lin(r * (0.8f + 0.4f * n), g * (0.8f + 0.4f * n), b * (0.8f + 0.4f * n), 0f);
        }

        static List<Vector3> Line(Vector3 a, Vector3 b, int points, float wobble, int seed)
        {
            var list = new List<Vector3>();
            for (int i = 0; i < points; i++)
            {
                float t = (float)i / (points - 1);
                Vector3 p = Vector3.Lerp(a, b, t);
                float w = wobble * Mathf.Sin(t * Mathf.PI);
                p.x += w * ForestNoise.Gradient(t * 2.3f, 0.5f, seed);
                p.z += w * ForestNoise.Gradient(t * 2.3f, 3.5f, seed);
                list.Add(p);
            }
            return list;
        }

        // ---------------------------------------------------------------- trees

        static float ConiferHeight(int v) { return v == 0 ? 20f : v == 1 ? 16.5f : 24f; }
        static float ConiferTrunk(int v) { return v == 1 ? 0.4f : 0.34f; }

        static ForestMeshBuilder Conifer(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(500 + v, 1);
            float h = ConiferHeight(v);
            float rMax = v == 0 ? 2.5f : v == 1 ? 3.1f : 2.2f;
            float crownBase = h * (v == 0 ? 0.2f : v == 1 ? 0.12f : 0.3f);
            int sides = lod == 0 ? 7 : lod == 1 ? 5 : 4;
            float trunkR = ConiferTrunk(v);
            mb.Tube(Line(new Vector3(0f, -0.3f, 0f), new Vector3(0f, h * 0.97f, 0f), lod == 0 ? 5 : 3, 0.12f, 900 + v),
                    t => Mathf.Lerp(trunkR, 0.04f, Mathf.Pow(t, 0.8f)), sides, (t, a) => Bark(t, a, 40 + v), false, false);

            mb.surface = ForestSurface.Needles;
            int tiers = lod == 0 ? 13 : lod == 1 ? 7 : 3;
            int points = lod == 0 ? 9 : lod == 1 ? 7 : 5;
            Color top = Lin(0.105f, 0.16f, 0.12f), tip = Lin(0.17f, 0.25f, 0.17f), under = Lin(0.05f, 0.075f, 0.06f);
            for (int i = 0; i < tiers; i++)
            {
                float f = (float)i / tiers;
                float yBase = crownBase + (h - crownBase) * Mathf.Pow(f, 0.95f);
                float th = (h - crownBase) / tiers * (lod == 2 ? 2.1f : 1.75f);
                float r = rMax * Mathf.Pow(1f - f, 0.85f) * rng.Range(0.88f, 1.12f) + 0.25f;
                var apex = new Vector3(rng.Range(-0.1f, 0.1f), Mathf.Min(h, yBase + th), rng.Range(-0.1f, 0.1f));
                float ao = Mathf.Lerp(0.65f, 1.05f, f);
                mb.ConeTier(apex, yBase, r, points, r * 0.35f, rng.Angle(), Vary(top, 0.1f, rng.Value()) * ao, Vary(tip, 0.12f, rng.Value()) * ao, under,
                            0.25f + 0.75f * f, 1000 + i * 13 + v);
            }
            return mb;
        }

        static ForestMeshBuilder YoungConifer(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(600 + v, 1);
            float h = v == 0 ? 4.5f : 3.4f;
            mb.Tube(Line(new Vector3(0f, -0.2f, 0f), new Vector3(0f, h, 0f), 2, 0f, 0), t => Mathf.Lerp(0.09f, 0.02f, t), lod == 0 ? 5 : 4,
                    (t, a) => Bark(t, a, 61), false, false);
            mb.surface = ForestSurface.Needles;
            int tiers = lod == 0 ? 9 : lod == 1 ? 5 : 3;
            int points = lod == 0 ? 8 : lod == 1 ? 6 : 5;
            Color top = Lin(0.12f, 0.19f, 0.12f), tip = Lin(0.2f, 0.29f, 0.17f), under = Lin(0.05f, 0.08f, 0.05f);
            for (int i = 0; i < tiers; i++)
            {
                float f = (float)i / tiers;
                float yBase = 0.25f + (h - 0.25f) * f;
                float th = h / tiers * (lod == 2 ? 2f : 1.7f);
                float r = (v == 0 ? 1.45f : 1.2f) * Mathf.Pow(1f - f, 0.9f) + 0.15f;
                mb.ConeTier(new Vector3(0f, Mathf.Min(h, yBase + th), 0f), yBase, r, points, r * 0.3f, rng.Angle(), Vary(top, 0.12f, rng.Value()), Vary(tip, 0.12f, rng.Value()), under,
                            0.4f + 0.6f * f, 1200 + i * 7 + v);
            }
            return mb;
        }

        static ForestMeshBuilder Broadleaf(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(700 + v, 1);
            float h = v == 1 ? 11f : 13f;
            Color[] leafColors =
            {
                Lin(0.62f, 0.33f, 0.11f), Lin(0.50f, 0.2f, 0.1f), Lin(0.66f, 0.52f, 0.17f)
            };
            Color green = Lin(0.31f, 0.36f, 0.14f);
            int sides = lod == 0 ? 7 : lod == 1 ? 5 : 4;
            float fork = h * 0.38f;
            mb.Tube(Line(new Vector3(0f, -0.2f, 0f), new Vector3(0.1f, fork, 0f), lod == 0 ? 4 : 2, 0.1f, 710 + v), t => Mathf.Lerp(0.32f, 0.22f, t), sides,
                    (t, a) => Bark(t, a, 71 + v, 0.27f, 0.23f, 0.19f), false, false);
            int branches = 3;
            var ends = new List<Vector3>();
            for (int b = 0; b < branches; b++)
            {
                float a = b * Mathf.PI * 2f / branches + rng.Range(-0.4f, 0.4f);
                Vector3 end = new Vector3(Mathf.Cos(a) * rng.Range(2f, 3f), h * rng.Range(0.68f, 0.82f), Mathf.Sin(a) * rng.Range(2f, 3f));
                if (lod < 2)
                    mb.Tube(Line(new Vector3(0.1f, fork - 0.3f, 0f), end, 3, 0.4f, 720 + b), t => Mathf.Lerp(0.2f, 0.06f, t), Mathf.Max(4, sides - 2),
                            (t, ang) => Bark(t, ang, 73, 0.27f, 0.23f, 0.19f), false, false);
                ends.Add(end);
            }
            ends.Add(new Vector3(0f, h * 0.88f, 0f));
            mb.surface = ForestSurface.Leaves;
            int clumps = lod == 0 ? 8 : lod == 1 ? 5 : 2;
            int sub = lod == 0 ? 1 : 0;
            for (int c = 0; c < clumps; c++)
            {
                Vector3 baseP = lod == 2 ? new Vector3(0f, h * (c == 0 ? 0.62f : 0.82f), 0f) : ends[c % ends.Count];
                Vector3 center = baseP + new Vector3(rng.Range(-1.2f, 1.2f), rng.Range(-0.6f, 1f), rng.Range(-1.2f, 1.2f)) * (lod == 2 ? 0.3f : 1f);
                float r = (lod == 2 ? 3.6f : lod == 1 ? 2.8f : 2.3f) * rng.Range(0.85f, 1.15f);
                Color baseC = Color.Lerp(leafColors[v], green, rng.Range(0f, 0.3f));
                int seed = 730 + c * 17 + v;
                mb.Blob(center, r, new Vector3(1f, 0.78f, 1f), sub, 0.38f, seed, mid =>
                {
                    float shade = Mathf.Lerp(0.55f, 1.1f, Mathf.Clamp01((mid.y - center.y) / r * 0.5f + 0.5f));
                    Color col = Vary(baseC, 0.15f, ForestNoise.Hash01(Mathf.RoundToInt(mid.x * 10f), Mathf.RoundToInt(mid.z * 10f), seed)) * shade;
                    col.a = 0.7f;
                    return col;
                });
            }
            return mb;
        }

        static ForestMeshBuilder Birch(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(800 + v, 1);
            mb.surface = ForestSurface.BirchBark;
            float h = v == 0 ? 15f : 12.5f;
            int sides = lod == 0 ? 7 : lod == 1 ? 5 : 4;
            var trunk = Line(new Vector3(0f, -0.2f, 0f), new Vector3(0.3f, h, 0.1f), lod == 0 ? 8 : 4, 0.25f, 810 + v);
            mb.Tube(trunk, t => Mathf.Lerp(0.17f, 0.03f, t), sides, (t, a) =>
            {
                float mark = ForestNoise.Gradient(t * 38f, Mathf.Cos(a) * 1.6f + Mathf.Sin(a) * 0.7f, 820 + v);
                bool dark = mark > 0.35f || t < 0.06f;
                return dark ? Lin(0.13f, 0.12f, 0.11f) : Lin(0.80f, 0.78f, 0.72f) * (0.9f + 0.1f * mark);
            }, false, false);
            if (lod < 2)
                for (int b = 0; b < 4; b++)
                {
                    float y = h * rng.Range(0.45f, 0.8f), a = rng.Angle();
                    mb.Tube(Line(new Vector3(0.15f, y, 0.05f), new Vector3(Mathf.Cos(a) * 1.8f, y + 1.5f, Mathf.Sin(a) * 1.8f), 3, 0.2f, 830 + b), t => Mathf.Lerp(0.06f, 0.015f, t), 4,
                            (t, ang) => Lin(0.35f, 0.32f, 0.28f), false, false);
                }
            mb.surface = ForestSurface.Leaves;
            int clumps = lod == 0 ? 7 : lod == 1 ? 4 : 2;
            Color leaf = v == 0 ? Lin(0.55f, 0.54f, 0.2f) : Lin(0.68f, 0.56f, 0.19f);
            for (int c = 0; c < clumps; c++)
            {
                float y = h * rng.Range(0.58f, 0.95f);
                float a = rng.Angle();
                float d = lod == 2 ? 0.3f : rng.Range(0.4f, 1.6f);
                var center = new Vector3(Mathf.Cos(a) * d + 0.2f, y, Mathf.Sin(a) * d);
                float r = (lod == 2 ? 2.4f : 1.4f) * rng.Range(0.8f, 1.2f);
                int seed = 840 + c * 11 + v;
                mb.Blob(center, r, new Vector3(1f, 1.15f, 1f), lod == 0 ? 1 : 0, 0.42f, seed, mid =>
                {
                    Color col = Vary(leaf, 0.18f, ForestNoise.Hash01(Mathf.RoundToInt(mid.x * 10f), Mathf.RoundToInt(mid.y * 10f), seed));
                    col.a = 0.8f;
                    return col * Mathf.Lerp(0.6f, 1.1f, Mathf.Clamp01((mid.y - center.y) / r * 0.5f + 0.5f));
                });
            }
            return mb;
        }

        static ForestMeshBuilder DeadTree(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(900 + v, 1);
            mb.surface = ForestSurface.Wood;
            float h = v == 0 ? 13f : 9f;
            int sides = lod == 0 ? 7 : lod == 1 ? 5 : 4;
            mb.Tube(Line(new Vector3(0f, -0.3f, 0f), new Vector3(0.4f, h, -0.2f), lod == 0 ? 6 : 3, 0.3f, 910 + v), t => Mathf.Lerp(0.32f, 0.13f, t), sides,
                    (t, a) => Bark(t, a, 91 + v, 0.33f, 0.31f, 0.29f), false, true, 0.9f, 920 + v);
            if (lod < 2)
            {
                int branches = lod == 0 ? 6 : 3;
                for (int b = 0; b < branches; b++)
                {
                    float y = h * rng.Range(0.3f, 0.85f), a = rng.Angle(), len = rng.Range(1.5f, 3.5f);
                    var end = new Vector3(Mathf.Cos(a) * len, y + len * rng.Range(0.3f, 0.9f), Mathf.Sin(a) * len);
                    mb.Tube(Line(new Vector3(0.2f, y, -0.1f), end, 3, 0.3f, 930 + b), t => Mathf.Lerp(0.09f, 0.02f, t), 4,
                            (t, ang) => Lin(0.30f, 0.28f, 0.26f), false, false);
                }
            }
            return mb;
        }

        static ForestMeshBuilder GiantDeadTree(int lod)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(950, 1);
            mb.surface = ForestSurface.Bark;
            float h = 26f;
            int sides = lod == 0 ? 10 : lod == 1 ? 7 : 5;
            mb.Tube(Line(new Vector3(0f, -0.6f, 0f), new Vector3(0.8f, h, 0.5f), lod == 0 ? 8 : 4, 0.6f, 951), t => Mathf.Lerp(1.15f, 0.4f, Mathf.Pow(t, 0.7f)), sides,
                    (t, a) => Bark(t, a, 952, 0.21f, 0.19f, 0.18f), false, true, 2.2f, 953);
            int branches = lod == 0 ? 7 : lod == 1 ? 5 : 3;
            for (int b = 0; b < branches; b++)
            {
                float y = h * rng.Range(0.45f, 0.9f), a = rng.Angle(), len = rng.Range(4f, 8f);
                var start = new Vector3(0.5f, y, 0.3f);
                var end = start + new Vector3(Mathf.Cos(a) * len, len * rng.Range(0.1f, 0.7f), Mathf.Sin(a) * len);
                mb.Tube(Line(start, end, lod == 0 ? 4 : 3, 0.8f, 960 + b), t => Mathf.Lerp(0.35f, 0.05f, t), Mathf.Max(4, sides - 4),
                        (t, ang) => Lin(0.2f, 0.18f, 0.17f), false, false);
            }
            // big roots
            for (int r = 0; r < (lod == 2 ? 0 : 5); r++)
            {
                float a = r * Mathf.PI * 2f / 5f + rng.Range(-0.3f, 0.3f);
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var path = new List<Vector3> { dir * 0.6f + Vector3.up * 1.4f, dir * 1.6f + Vector3.up * 0.6f, dir * 3f + Vector3.down * 0.1f, dir * 4.2f + Vector3.down * 0.5f };
                mb.Tube(path, t => Mathf.Lerp(0.55f, 0.08f, t), 5, (t, ang) => Lin(0.2f, 0.18f, 0.16f), false, false);
            }
            return mb;
        }

        // ---------------------------------------------------------------- undergrowth and ground cover

        static ForestMeshBuilder Bush(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(1100 + v, 1);
            mb.surface = ForestSurface.Leaves;
            int blobs = lod == 0 ? 6 : 2;
            Color baseC = v == 0 ? Lin(0.14f, 0.21f, 0.11f) : Lin(0.22f, 0.24f, 0.11f);
            for (int b = 0; b < blobs; b++)
            {
                float a = rng.Angle();
                float d = lod == 0 ? rng.Range(0.2f, 0.75f) : 0.25f;
                var center = new Vector3(Mathf.Cos(a) * d, rng.Range(0.45f, 0.95f), Mathf.Sin(a) * d);
                float r = (lod == 0 ? rng.Range(0.45f, 0.75f) : 0.85f);
                int seed = 1110 + b * 7 + v;
                mb.Blob(center, r, new Vector3(1f, 0.8f, 1f), lod == 0 ? 1 : 0, 0.4f, seed, mid =>
                {
                    Color col = Vary(baseC, 0.2f, ForestNoise.Hash01(Mathf.RoundToInt(mid.x * 20f), Mathf.RoundToInt(mid.z * 20f), seed));
                    col *= Mathf.Lerp(0.5f, 1.1f, Mathf.Clamp01(mid.y / 1.3f));
                    col.a = Mathf.Clamp01(mid.y / 1.4f);
                    return col;
                });
            }
            return mb;
        }

        static ForestMeshBuilder Fern(int lod)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(1200, 1);
            mb.surface = ForestSurface.Blades;
            int fronds = lod == 0 ? 9 : 5;
            int segs = lod == 0 ? 6 : 4;
            for (int f = 0; f < fronds; f++)
            {
                float a = f * Mathf.PI * 2f / fronds + rng.Range(-0.25f, 0.25f);
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                float len = rng.Range(0.7f, 1.05f), rise = rng.Range(0.45f, 0.65f);
                var spine = new List<Vector3>();
                for (int s = 0; s <= segs; s++)
                {
                    float t = (float)s / segs;
                    spine.Add(dir * (len * t) + Vector3.up * (0.05f + rise * Mathf.Sin(t * Mathf.PI * 0.8f)));
                }
                Color c0 = rng.Chance(0.2f) ? Lin(0.40f, 0.30f, 0.14f) : Lin(0.19f, 0.32f, 0.12f);
                Color c1 = rng.Chance(0.2f) ? Lin(0.5f, 0.38f, 0.17f) : Lin(0.29f, 0.42f, 0.17f);
                mb.Strip(spine, Vector3.Cross(dir, Vector3.up).normalized, t => 0.24f * (1f - Mathf.Pow(t, 1.5f)) + 0.02f,
                         t => { Color c = Color.Lerp(c0, c1, t); c.a = t; return c; });
            }
            return mb;
        }

        static ForestMeshBuilder Grass(int v)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(1300 + v, 1);
            mb.surface = ForestSurface.Blades;
            int blades = v == 0 ? 14 : 10;
            for (int b = 0; b < blades; b++)
            {
                Vector2 o = rng.InsideCircle() * 0.14f;
                float a = rng.Angle();
                var lean = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rng.Range(0.05f, 0.22f);
                float hgt = rng.Range(0.25f, v == 0 ? 0.55f : 0.42f);
                var basePos = new Vector3(o.x, -0.03f, o.y);
                var spine = new List<Vector3> { basePos, basePos + Vector3.up * hgt * 0.5f + lean * 0.3f, basePos + Vector3.up * hgt + lean };
                Color c0 = v == 0 ? Lin(0.2f, 0.27f, 0.11f) : Lin(0.36f, 0.34f, 0.2f);
                Color c1 = v == 0 ? Lin(0.33f, 0.41f, 0.17f) : Lin(0.52f, 0.48f, 0.28f);
                Vector3 side = Vector3.Cross(lean.sqrMagnitude > 0f ? lean.normalized : Vector3.forward, Vector3.up).normalized;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                mb.Strip(spine, side, t => 0.035f * (1f - t), t => { Color c = Color.Lerp(c0, c1, t); c.a = t; return c; });
            }
            return mb;
        }

        static ForestMeshBuilder Branch(int v)
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Wood;
            float len = v == 0 ? 1.7f : 1.1f;
            var main = Line(new Vector3(-len * 0.5f, 0.05f, 0f), new Vector3(len * 0.5f, 0.03f, 0f), 4, 0.15f, 1400 + v);
            mb.Tube(main, t => Mathf.Lerp(0.05f, 0.015f, t), 5, (t, a) => Lin(0.28f, 0.23f, 0.18f), true, true);
            var fork = Line(main[1], main[1] + new Vector3(0.45f, 0.02f, 0.35f) * len * 0.6f, 3, 0.05f, 1410 + v);
            mb.Tube(fork, t => Mathf.Lerp(0.025f, 0.01f, t), 4, (t, a) => Lin(0.3f, 0.25f, 0.19f), false, true);
            return mb;
        }

        static ForestMeshBuilder Pebbles()
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(1500, 1);
            mb.surface = ForestSurface.Stone;
            for (int i = 0; i < 5; i++)
            {
                Vector2 o = rng.InsideCircle() * 0.45f;
                float r = rng.Range(0.06f, 0.16f);
                int seed = 1510 + i;
                Color baseC = Lin(0.42f, 0.41f, 0.38f) * rng.Range(0.7f, 1.1f);
                baseC.a = 0f;
                mb.Blob(new Vector3(o.x, r * 0.25f, o.y), r, new Vector3(1f, 0.6f, 1f), 0, 0.2f, seed, mid => baseC);
            }
            return mb;
        }

        static ForestMeshBuilder Roots()
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(1600, 1);
            mb.surface = ForestSurface.Bark;
            for (int i = 0; i < 3; i++)
            {
                float a = rng.Angle();
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var side = new Vector3(-dir.z, 0f, dir.x) * rng.Range(-0.3f, 0.3f);
                var path = new List<Vector3> { -dir * 0.9f + side + Vector3.down * 0.12f, -dir * 0.3f + Vector3.up * 0.1f, dir * 0.4f + side * 0.5f + Vector3.up * 0.08f, dir * 1.1f + Vector3.down * 0.12f };
                mb.Tube(path, t => Mathf.Lerp(0.08f, 0.035f, t), 5, (t, ang) => Bark(t, ang, 1610 + i, 0.3f, 0.24f, 0.18f), false, false);
            }
            return mb;
        }

        // ---------------------------------------------------------------- rocks, logs, stumps

        static ForestMeshBuilder Rock(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Stone;
            float r = v == 0 ? 0.7f : v == 1 ? 0.55f : 0.9f;
            Vector3 squash = v == 0 ? new Vector3(1.2f, 0.75f, 1f) : v == 1 ? new Vector3(1f, 0.9f, 0.85f) : new Vector3(1.35f, 0.55f, 1.1f);
            var center = new Vector3(0f, r * squash.y - 0.25f, 0f);
            int seed = 1700 + v;
            mb.Blob(center, r, squash, lod == 0 ? 2 : lod == 1 ? 1 : 0, 0.22f, seed, mid => RockColor(mid, center, seed));
            return mb;
        }

        static ForestMeshBuilder Boulder(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Stone;
            float r = v == 0 ? 1.6f : 1.9f;
            Vector3 squash = v == 0 ? new Vector3(1.3f, 0.85f, 1.05f) : new Vector3(1.5f, 0.6f, 1.2f);
            var center = new Vector3(0f, r * squash.y - 0.4f, 0f);
            int seed = 1800 + v;
            mb.Blob(center, r, squash, lod == 0 ? 2 : 1, 0.2f, seed, mid => RockColor(mid, center, seed));
            return mb;
        }

        /// <summary>Boundary rock: a big faceted slab (A), broad block (B) or pillar (C), half buried, long axis along X.</summary>
        static ForestMeshBuilder CliffRock(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Stone;
            float r = 3.2f;
            Vector3 squash = v == 0 ? new Vector3(1.6f, 1.15f, 0.85f) : v == 1 ? new Vector3(1.9f, 0.85f, 1.25f) : new Vector3(0.95f, 1.7f, 0.9f);
            var center = new Vector3(0f, r * squash.y * 0.62f, 0f);
            int seed = 2400 + v;
            mb.Blob(center, r, squash, lod == 0 ? 2 : 1, 0.24f, seed, mid =>
            {
                Color c = RockColor(mid, center, seed);
                return c * 0.85f;
            });
            if (lod == 0)
            {
                // a second, smaller block leaning on it breaks up the silhouette
                var c2 = center + new Vector3(r * squash.x * 0.55f, -r * 0.35f, r * 0.3f);
                mb.Blob(c2, r * 0.55f, new Vector3(1.2f, 0.9f, 1f), 1, 0.2f, seed + 7, mid => RockColor(mid, c2, seed + 7) * 0.85f);
            }
            return mb;
        }

        static ForestMeshBuilder SplitBoulder(int lod)
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Stone;
            for (int half = 0; half < 2; half++)
            {
                float side = half == 0 ? -1f : 1f;
                var center = new Vector3(side * 1.55f, 1.6f, half * 0.3f);
                int seed = 1850 + half;
                mb.Blob(center, 2.1f, new Vector3(0.75f, 1.05f, 1.2f), lod == 0 ? 2 : 1, 0.18f, seed, mid => RockColor(mid, center, seed));
            }
            return mb;
        }

        static ForestMeshBuilder Cairn(int lod)
        {
            var mb = new ForestMeshBuilder();
            var rng = new ForestRandom(1900, 1);
            mb.surface = ForestSurface.Stone;
            float y = 0f;
            int stones = lod == 0 ? 7 : 4;
            for (int i = 0; i < stones; i++)
            {
                float r = Mathf.Lerp(0.55f, 0.22f, (float)i / stones);
                float hh = r * 0.42f;
                var center = new Vector3(rng.Range(-0.06f, 0.06f), y + hh, rng.Range(-0.06f, 0.06f));
                int seed = 1910 + i;
                mb.Blob(center, r, new Vector3(1f, 0.42f, 0.9f), lod == 0 ? 1 : 0, 0.15f, seed, mid => RockColor(mid, center, seed));
                y += hh * 1.7f;
            }
            return mb;
        }

        static Color RockColor(Vector3 mid, Vector3 center, int seed)
        {
            Vector3 n = (mid - center).normalized;
            float k = ForestNoise.Hash01(Mathf.RoundToInt(mid.x * 7f), Mathf.RoundToInt(mid.z * 7f + mid.y * 13f), seed);
            Color c = Lin(0.4f, 0.39f, 0.37f) * (0.75f + 0.4f * k);
            float moss = Mathf.Clamp01((n.y - 0.45f) * 2.5f) * (0.5f + 0.5f * ForestNoise.Hash01(Mathf.RoundToInt(mid.x * 3f), Mathf.RoundToInt(mid.z * 3f), seed + 1));
            c = Color.Lerp(c, Lin(0.22f, 0.29f, 0.12f), moss);
            c.a = 0f;
            return c;
        }

        static float LogLength(int v) { return v == 0 ? 7f : 5f; }
        static float LogRadius(int v) { return v == 0 ? 0.38f : 0.3f; }

        static ForestMeshBuilder Log(int lod, int v)
        {
            var mb = new ForestMeshBuilder();
            float len = LogLength(v), r = LogRadius(v);
            int sides = lod == 0 ? 9 : lod == 1 ? 6 : 4;
            var path = new List<Vector3>();
            int rings = lod == 0 ? 7 : 3;
            for (int i = 0; i < rings; i++)
            {
                float t = (float)i / (rings - 1);
                path.Add(new Vector3(-len * 0.5f + len * t, r - 0.06f * Mathf.Sin(t * Mathf.PI), 0.15f * Mathf.Sin(t * 2.4f + v)));
            }
            mb.Tube(path, t => Mathf.Lerp(r, r * 0.8f, t), sides, (t, a) => Bark(t, a, 2000 + v, 0.23f, 0.19f, 0.15f), true, true, 0.35f, 2010 + v);
            // moss and damp on the upper side
            for (int i = 0; i < mb.pos.Count; i++)
            {
                Vector3 p = mb.pos[i];
                float up = (p.y - r) / r;
                float n = ForestNoise.Gradient(p.x * 1.3f, p.z * 2f, 2020 + v) * 0.5f + 0.5f;
                Color c = Color.Lerp(mb.col[i], Lin(0.2f, 0.27f, 0.11f), Mathf.Clamp01((up - 0.2f) * 2f) * n);
                c.a = 0f;
                mb.col[i] = c;
            }
            mb.surface = ForestSurface.Wood;                     // the broken branch stubs on top
            if (lod == 0)
                for (int s = 0; s < 3; s++)
                {
                    float x = -len * 0.3f + s * len * 0.28f;
                    var b = new Vector3(x, r * 1.5f, 0f);
                    mb.Tube(new List<Vector3> { new Vector3(x, r, 0f), b + new Vector3(0.1f, 0.25f, 0.2f * (s - 1)) }, t => Mathf.Lerp(0.06f, 0.02f, t), 4,
                            (t, a) => Lin(0.25f, 0.21f, 0.17f), false, true, 0.05f, 2030 + s);
                }
            return mb;
        }

        /// <summary>
        /// A clean sawn top for the stump, closed so nothing shows through: a small rounded edge (lod 0), then one flat disc
        /// that is lighter at the rim and darker at the heart. The tube's top ring is nearly level, so the disc is one plane
        /// and shades evenly. ringStart is the index of the ring's first vertex in the builder.
        /// </summary>
        static void StumpTop(ForestMeshBuilder mb, int ringStart, int sides, bool bevel)
        {
            var outer = new Vector3[sides];
            Vector3 center = Vector3.zero;
            for (int j = 0; j < sides; j++)
            {
                outer[j] = mb.pos[ringStart + j];
                center += outer[j];
            }
            center /= sides;

            Color rim = Lin(0.20f, 0.16f, 0.12f), cut = Lin(0.40f, 0.32f, 0.23f), heart = Lin(0.31f, 0.24f, 0.17f);
            mb.surface = ForestSurface.Wood;
            // a face is wound so that its normal points up (the whole top faces up)
            System.Action<Vector3, Vector3, Vector3, Color, Color, Color> face = (a, b, c, ca, cb, cc) =>
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), Vector3.up) >= 0f) mb.FlatT(a, b, c, ca, cb, cc);
                else mb.FlatT(a, c, b, ca, cc, cb);
            };

            Vector3 lift = Vector3.up * (bevel ? 0.03f : 0f);
            var inner = new Vector3[sides];
            for (int j = 0; j < sides; j++) inner[j] = bevel ? center + (outer[j] - center) * 0.86f + lift : outer[j];
            Vector3 middle = center + lift;

            if (bevel)
                for (int j = 0; j < sides; j++)
                {
                    int k = (j + 1) % sides;
                    face(outer[j], outer[k], inner[k], rim, rim, cut);
                    face(outer[j], inner[k], inner[j], rim, cut, cut);
                }
            for (int j = 0; j < sides; j++)
            {
                int k = (j + 1) % sides;
                face(middle, inner[j], inner[k], heart, bevel ? cut : rim, bevel ? cut : rim);
            }
        }

        static ForestMeshBuilder Stump(int lod)
        {
            var mb = new ForestMeshBuilder();
            int sides = lod == 0 ? 9 : 6;
            // 0.78 m to the top edge (+0.03 for the rounded rim): about the height the old broken top averaged, and the height of
            // the stump's capsule collider, so a hand landing on it meets the surface
            mb.Tube(new List<Vector3> { new Vector3(0f, -0.2f, 0f), new Vector3(0f, 0.36f, 0f), new Vector3(0.02f, 0.78f, 0f) },
                    t => Mathf.Lerp(0.48f, 0.38f, t), sides, (t, a) => Bark(t, a, 2100, 0.25f, 0.2f, 0.16f), false, false);
            StumpTop(mb, mb.pos.Count - (sides + 1), sides, lod == 0);       // the tube's last ring is its top edge
            mb.surface = ForestSurface.Bark;
            if (lod == 0)
                for (int r = 0; r < 4; r++)
                {
                    float a = r * Mathf.PI * 0.5f + 0.4f;
                    var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    var path = new List<Vector3> { dir * 0.25f + Vector3.up * 0.25f, dir * 0.6f + Vector3.up * 0.08f, dir * 1.1f + Vector3.down * 0.1f };
                    mb.Tube(path, t => Mathf.Lerp(0.16f, 0.05f, t), 5, (t, ang) => Lin(0.24f, 0.19f, 0.15f), false, false);
                }
            return mb;
        }

        static ForestMeshBuilder Sign()
        {
            var mb = new ForestMeshBuilder();
            mb.surface = ForestSurface.Wood;
            Color wood = Lin(0.35f, 0.28f, 0.2f), dark = Lin(0.22f, 0.18f, 0.14f);
            mb.Tube(new List<Vector3> { new Vector3(0f, -0.3f, 0f), new Vector3(0.03f, 2.2f, 0f) }, t => 0.08f, 4, (t, a) => dark, false, true, 0.05f, 2200);
            // a slanted board: a flat box made of a short wide tube
            var board = new List<Vector3> { new Vector3(-0.55f, 1.75f, 0.1f), new Vector3(0.6f, 1.62f, 0.1f) };
            mb.Tube(board, t => 0.22f, 4, (t, a) => wood * (0.85f + 0.2f * Mathf.Sin(a * 3f)), true, true);
            return mb;
        }
    }
}
