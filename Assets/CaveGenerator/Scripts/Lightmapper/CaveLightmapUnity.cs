using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HavocCave.Lightmapping
{
    /// <summary>
    /// The Unity side of the lightmapper: gathers the lights and the props that cast shadows (main thread only), and turns
    /// a finished bake into textures and a material. Everything that needs the engine lives here, so the baking code itself
    /// can run on worker threads.
    /// </summary>
    public static class CaveLightmapUnity
    {
        public const string ColorProperty = "_CaveLightmap";
        public const string DirectionProperty = "_CaveLightmapDir";
        public const string ModeProperty = "_CaveLightmapMode";

        const int MaxOccluderTriangles = 120000;
        const int MaxTrianglesPerProp = 20000;

        // ---------------------------------------------------------------- lights

        /// <summary>
        /// Lights for one bake. lamps are in cave space already; scene lights with a CaveBakedLight are converted from world
        /// space into the space of caveRoot.
        /// </summary>
        public static CaveBakeLight[] CollectLights(CaveLamp[] lamps, float lampRange, float lampIntensity, Color lampColor, float lampRadius,
                                                    bool softFalloff, bool oneWayLamps, Transform caveRoot, List<PropGroup> props, bool linear)
        {
            var list = new List<CaveBakeLight>();
            Color lamp = CaveBakeSettings.ToLinear(lampColor, linear) * Mathf.Max(0f, lampIntensity);

            for (int i = 0; i < lamps.Length; i++)
            {
                // A fixture with an open side (the ceiling strip lights) is a half-space emitter: a spot with a 180 degree cone whose
                // brightness fades with the cosine towards the horizon. Glass lanterns shine all round.
                bool oneWay = oneWayLamps && lamps[i].direction.sqrMagnitude > 1e-6f;
                list.Add(new CaveBakeLight
                {
                    type = oneWay ? CaveBakeLightType.Spot : CaveBakeLightType.Point,
                    position = lamps[i].position,
                    direction = oneWay ? lamps[i].direction.normalized : Vector3.down,
                    spotAngle = 179f,
                    innerSpotAngle = 0f,
                    color = lamp,
                    range = lampRange,
                    radius = lampRadius,
                    windowed = softFalloff,
                    ignoreObject = FixtureAt(props, caveRoot.TransformPoint(lamps[i].position))
                });
            }

            float scale = Mathf.Max(0.0001f, Mathf.Abs(caveRoot.lossyScale.x));
            CaveBakedLight[] extra = Object.FindObjectsOfType<CaveBakedLight>();
            for (int i = 0; i < extra.Length; i++)
            {
                CaveBakedLight c = extra[i];
                if (!c.includeInBake || !c.isActiveAndEnabled) continue;
                Light l = c.GetComponent<Light>();
                if (l == null) continue;     // a disabled Light is fine: it is the baked-only way to use one

                var b = new CaveBakeLight
                {
                    position = caveRoot.InverseTransformPoint(l.transform.position),
                    direction = caveRoot.InverseTransformDirection(l.transform.forward),
                    color = CaveBakeSettings.ToLinear(l.color, linear) * (Mathf.Max(0f, l.intensity) * c.bakeIntensityScale),
                    range = l.range / scale,
                    radius = c.radius / scale,
                    spotAngle = l.spotAngle,
                    innerSpotAngle = l.innerSpotAngle > 0f ? l.innerSpotAngle : l.spotAngle * 0.6f,
                    windowed = !c.realisticFalloff,
                    ignoreObject = FixtureAt(props, l.transform.position)
                };
                if (l.type == LightType.Point) b.type = CaveBakeLightType.Point;
                else if (l.type == LightType.Spot) b.type = CaveBakeLightType.Spot;
                else if (l.type == LightType.Directional) b.type = CaveBakeLightType.Directional;
                else continue;
                list.Add(b);
            }
            return list.ToArray();
        }

        /// <summary>The prop (fixture) whose bounds hold the light, so its own shell does not shadow it. -1 if none.</summary>
        static int FixtureAt(List<PropGroup> props, Vector3 worldPoint)
        {
            int best = -1;
            float bestVolume = float.MaxValue;
            if (props == null) return best;
            for (int i = 0; i < props.Count; i++)
            {
                Bounds b = props[i].bounds;
                b.Expand(0.2f);
                if (!b.Contains(worldPoint)) continue;
                float volume = b.size.x * b.size.y * b.size.z;
                if (volume < bestVolume) { bestVolume = volume; best = props[i].objectId; }
            }
            return best;
        }

        // ---------------------------------------------------------------- props that cast shadows

        /// <summary>One spawned prop (everything under one child of the Props object), as the lightmapper sees it.</summary>
        public sealed class PropGroup
        {
            public int objectId;
            public Bounds bounds;           // world space
            public Transform root;          // the child of the Props object
        }

        /// <summary>
        /// Gathers the meshes under propsRoot: one group per prop and, when castShadows is on, the shadow casters. A mesh that is
        /// not readable (Read/Write off in the import settings, which is the default for models) cannot be read, so its bounding
        /// box is used as its shadow instead: exact for a crate, a little too big for a barrel.
        /// </summary>
        public static CaveBakeOccluder[] CollectOccluders(Transform propsRoot, Transform caveRoot, bool linear, bool castShadows, out List<PropGroup> groups)
        {
            groups = new List<PropGroup>();
            var result = new List<CaveBakeOccluder>();
            if (propsRoot == null) return result.ToArray();

            var groupOf = new Dictionary<Transform, PropGroup>();
            int triangles = 0;
            Matrix4x4 worldToCave = caveRoot.worldToLocalMatrix;
            var verts = new List<Vector3>();
            var idx = new List<int>();
            var sub = new List<int>();

            Renderer[] renderers = propsRoot.GetComponentsInChildren<Renderer>(false);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer rend = renderers[r];
                if (!rend.enabled) continue;
                MeshFilter mf = rend is MeshRenderer ? rend.GetComponent<MeshFilter>() : null;
                bool skinned = rend is SkinnedMeshRenderer;
                if (mf == null && !skinned) continue;               // particles, trails and the like are neither lit nor shadow casters

                Transform top = rend.transform;
                while (top.parent != null && top.parent != propsRoot) top = top.parent;
                PropGroup g;
                if (!groupOf.TryGetValue(top, out g))
                {
                    g = new PropGroup { objectId = groups.Count, bounds = rend.bounds, root = top };
                    groupOf[top] = g;
                    groups.Add(g);
                }
                else
                {
                    Bounds b = g.bounds;
                    b.Encapsulate(rend.bounds);
                    g.bounds = b;
                }

                if (!castShadows || mf == null || rend.shadowCastingMode == ShadowCastingMode.Off) continue;
                Mesh mesh = mf.sharedMesh;
                if (mesh == null) continue;

                Matrix4x4 m = worldToCave * rend.transform.localToWorldMatrix;
                float[] tri;
                if (mesh.isReadable)
                {
                    int meshTris = (int)(mesh.GetIndexCount(0) / 3);
                    for (int s = 1; s < mesh.subMeshCount; s++) meshTris += (int)(mesh.GetIndexCount(s) / 3);
                    if (meshTris > MaxTrianglesPerProp || triangles + meshTris > MaxOccluderTriangles) continue;

                    verts.Clear();
                    mesh.GetVertices(verts);
                    idx.Clear();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        sub.Clear();
                        mesh.GetTriangles(sub, s);
                        idx.AddRange(sub);
                    }

                    tri = new float[idx.Count * 3];
                    for (int i = 0; i < idx.Count; i++)
                    {
                        Vector3 p = m.MultiplyPoint3x4(verts[idx[i]]);
                        tri[i * 3] = p.x; tri[i * 3 + 1] = p.y; tri[i * 3 + 2] = p.z;
                    }
                }
                else
                {
                    if (triangles + 12 > MaxOccluderTriangles) continue;
                    tri = BoxTriangles(mesh.bounds, m);
                }
                triangles += tri.Length / 9;
                result.Add(new CaveBakeOccluder { triangles = tri, objectId = g.objectId, albedo = AlbedoOf(rend, linear) });
            }
            return result.ToArray();
        }

        static readonly int[] BoxCorners =
        {
            0, 1, 3, 0, 3, 2,     // -z
            4, 7, 5, 4, 6, 7,     // +z
            0, 4, 5, 0, 5, 1,     // -y
            2, 3, 7, 2, 7, 6,     // +y
            0, 2, 6, 0, 6, 4,     // -x
            1, 5, 7, 1, 7, 3      // +x
        };

        /// <summary>The 12 triangles of a mesh's bounding box, moved by m. (The baker's triangles are two sided, so the winding does not matter.)</summary>
        static float[] BoxTriangles(Bounds b, Matrix4x4 m)
        {
            var corner = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 local = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) != 0 ? 1f : -1f, (i & 2) != 0 ? 1f : -1f, (i & 4) != 0 ? 1f : -1f));
                corner[i] = m.MultiplyPoint3x4(local);
            }
            var tri = new float[BoxCorners.Length * 3];
            for (int i = 0; i < BoxCorners.Length; i++)
            {
                Vector3 p = corner[BoxCorners[i]];
                tri[i * 3] = p.x; tri[i * 3 + 1] = p.y; tri[i * 3 + 2] = p.z;
            }
            return tri;
        }

        // ---------------------------------------------------------------- baked light for props

        /// <summary>One probe per prop, in the middle of it. The prop's own triangles never block the probe's rays.</summary>
        public static CaveBakeProbe[] CollectProbes(List<PropGroup> groups, Transform caveRoot)
        {
            var probes = new CaveBakeProbe[groups.Count];
            for (int i = 0; i < probes.Length; i++)
                probes[i] = new CaveBakeProbe { position = caveRoot.InverseTransformPoint(groups[i].bounds.center), ignoreObject = groups[i].objectId };
            return probes;
        }

        /// <summary>
        /// Turns the measured light of each prop into spherical harmonics (what a light probe holds) and puts them on the prop,
        /// where a CavePropProbe hands them to its meshes. strength is the cave material's Baked Lightmap Strength, so props
        /// brighten and dim together with the walls. Returns how many props got light.
        /// </summary>
        public static int ApplyPropProbes(List<PropGroup> groups, float[][] samples, Transform caveRoot, float strength)
        {
            if (groups == null || samples == null) return 0;
            int applied = 0;
            for (int i = 0; i < groups.Count && i < samples.Length; i++)
            {
                PropGroup g = groups[i];
                float[] s = samples[i];
                if (g.root == null || s == null) continue;

                var sh = new SphericalHarmonicsL2();
                for (int k = 0; k + 5 < s.Length; k += 6)
                {
                    // Cave space to world space: the cave object may be turned. AddDirectionalLight counts a direction as pointing
                    // towards the light, and its intensity 1 gives twice the colour, hence the half.
                    Vector3 toLight = caveRoot.TransformDirection(new Vector3(s[k], s[k + 1], s[k + 2])).normalized;
                    sh.AddDirectionalLight(toLight, new Color(s[k + 3], s[k + 4], s[k + 5], 1f) * strength, 0.5f);
                }

                CavePropProbe probe = g.root.GetComponent<CavePropProbe>();
                if (probe == null) probe = g.root.gameObject.AddComponent<CavePropProbe>();
                probe.SetLight(sh);
                applied++;
            }
            return applied;
        }

        static Color AlbedoOf(Renderer r, bool linear)
        {
            Material m = r.sharedMaterial;
            Color c = new Color(0.5f, 0.5f, 0.5f, 1f);
            if (m != null)
            {
                if (m.HasProperty("_BaseColor")) c = m.GetColor("_BaseColor");
                else if (m.HasProperty("_Color")) c = m.GetColor("_Color");
            }
            return CaveBakeSettings.ToLinear(c, linear);
        }

        // ---------------------------------------------------------------- textures and material

        /// <summary>
        /// Uploads a bake as textures. keepReadable keeps a copy of the pixels in memory, which the editor needs to save
        /// the texture as an asset; in a build it only wastes memory.
        /// </summary>
        public static void CreateTextures(CaveBakeResult r, bool keepReadable, out Texture2D color, out Texture2D direction)
        {
            int w = r.width, h = r.height;
            int n = w * h;

            if (SystemInfo.SupportsTextureFormat(TextureFormat.RGB9e5Float))
            {
                color = new Texture2D(w, h, TextureFormat.RGB9e5Float, false, true);
                color.SetPixelData(r.color, 0);
            }
            else
            {
                // No shared exponent format on this GPU: half floats instead (twice the memory).
                var half = new ushort[n * 4];
                for (int i = 0; i < n; i++)
                {
                    float x, y, z;
                    LmMath.UnpackRgb9e5(r.color[i], out x, out y, out z);
                    half[i * 4] = LmMath.FloatToHalf(x);
                    half[i * 4 + 1] = LmMath.FloatToHalf(y);
                    half[i * 4 + 2] = LmMath.FloatToHalf(z);
                    half[i * 4 + 3] = 0x3C00;
                }
                color = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
                color.SetPixelData(half, 0);
            }
            Finish(color, "CaveLightmap_Color", keepReadable);

            direction = null;
            if (r.direction != null)
            {
                direction = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                direction.SetPixelData(r.direction, 0);
                Finish(direction, "CaveLightmap_Direction", keepReadable);
            }
        }

        static void Finish(Texture2D t, string name, bool keepReadable)
        {
            t.name = name;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.anisoLevel = 0;
            t.Apply(false, !keepReadable);
        }

        /// <summary>A copy of the cave material that reads the lightmap. One copy per cave, so every chunk still batches.</summary>
        public static Material CreateMaterial(Material source, string name, Texture2D color, Texture2D direction)
        {
            var m = new Material(source) { name = name };
            if (m.HasProperty(ColorProperty)) m.SetTexture(ColorProperty, color);
            if (m.HasProperty(DirectionProperty)) m.SetTexture(DirectionProperty, direction);
            if (m.HasProperty(ModeProperty)) m.SetFloat(ModeProperty, direction != null ? 2f : 1f);
            return m;
        }
    }
}
