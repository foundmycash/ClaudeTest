using UnityEngine;

namespace HavocCave
{
    public enum CaveBakeLightType
    {
        Point,
        Spot,
        Directional
    }

    /// <summary>A light the lightmapper bakes. Positions and directions are in cave space (the space of the generated chunk meshes).</summary>
    public struct CaveBakeLight
    {
        public CaveBakeLightType type;
        public Vector3 position;
        public Vector3 direction;       // spot axis, or the way a directional light travels
        public Color color;             // linear, intensity already multiplied in
        public float range;
        public float radius;            // size of the light source: soft shadows
        public float spotAngle;         // full outer cone angle, degrees
        public float innerSpotAngle;    // degrees
        public bool windowed;           // true: (1 - d/range)^2 falloff, the same curve the vertex bake uses. false: inverse square.
        public int ignoreObject;        // occluder objectId that never shadows this light (the fixture it hangs in), or -1
    }

    /// <summary>A point where the lightmapper measures the light reaching a prop: its light probe. Cave space.</summary>
    public struct CaveBakeProbe
    {
        public Vector3 position;
        public int ignoreObject;        // occluder objectId of the prop the probe sits in (its own triangles never block the probe's rays), or -1
    }

    /// <summary>A static mesh that blocks light and casts shadows into the lightmap but is not lightmapped itself (a prop).</summary>
    public sealed class CaveBakeOccluder
    {
        public float[] triangles;       // 9 floats per triangle, cave space
        public int objectId;
        public Color albedo;            // linear, for light that bounces off it
    }

    /// <summary>What a bake produces. The two arrays are ready to upload as textures.</summary>
    public sealed class CaveBakeResult
    {
        public int width, height;
        public uint[] color;            // RGB9E5, one per texel
        public uint[] direction;        // RGBA8 per texel: xyz = dominant light direction (0.5 + 0.5 * d, cave space), w = how strongly it faces the surface. Null when not baked.
        public float[][] probes;        // one entry per probe asked for: 6 floats per sample = unit direction towards where the light comes from (cave space), then linear rgb. Null when none were asked for.
        public int charts;
        public int texels;              // texels that cover the cave
        public int triangles;
        public int lights;
        public int occluderTriangles;
        public float texelsPerMeter;    // the density that fit the atlas
        public float seconds;
        public bool cancelled;
    }
}
