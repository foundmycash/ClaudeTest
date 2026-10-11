using UnityEngine;

namespace HavocCave
{
    public enum CaveBakeQuality
    {
        [Tooltip("Fast. One bounce and few rays. Looks fine on Quest.")] Low,
        [Tooltip("Two bounces. A good default on PC.")] Medium,
        [Tooltip("Three bounces and plenty of rays. Cleanest result, slowest bake.")] High,
        [Tooltip("Use the Bounces, Rays Per Texel, Shadow Rays and Denoise Radius values below.")] Custom
    }

    public enum CaveLampFalloff
    {
        [Tooltip("Inverse square falloff, like a real lamp.")] Realistic,
        [Tooltip("The old flat fade across the whole range.")] Soft
    }

    public enum CaveAtlasSize
    {
        [InspectorName("512")] S512 = 512,
        [InspectorName("1024")] S1024 = 1024,
        [InspectorName("2048")] S2048 = 2048,
        [InspectorName("4096")] S4096 = 4096
    }

    /// <summary>
    /// Settings for the cave lightmapper: a path tracer that bakes the lamps' light (soft shadows, bounced light, ambient
    /// occlusion) into one lightmap texture every time a cave is generated.
    /// </summary>
    [System.Serializable]
    public class CaveBakeSettings
    {
        [Tooltip("Bake the lighting into a lightmap every time a cave is generated. Off: the cave falls back to the cheaper light baked into the vertices.")]
        public bool enabled = true;

        [Tooltip("Also bake when the cave is generated in play mode or in a build. Turn off if baking takes too long on your target (for example Quest) and bake in the editor instead.")]
        public bool bakeAtRuntime = true;

        [Tooltip("How much work the bake does. Mobile devices (Quest, phones) drop to Low, at most 5 texels per metre and a 1024 lightmap automatically unless this is Custom.")]
        public CaveBakeQuality quality = CaveBakeQuality.Medium;

        [Header("Resolution")]
        [Tooltip("Lightmap texels per metre of cave wall. 8 is about 12 cm per texel. If the cave is too big to fit the atlas at this density, the density is lowered to fit.")]
        [Range(2f, 32f)] public float texelsPerMeter = 8f;

        [Tooltip("The biggest the lightmap texture may be (it is made as small as the cave allows). Memory: about 8 bytes per texel with the direction map.")]
        public CaveAtlasSize maxSize = CaveAtlasSize.S2048;

        [Header("Quality (used when Quality = Custom)")]
        [Tooltip("How many times light may bounce off the rock before it reaches the wall. 0 = direct light only.")]
        [Range(0, 4)] public int bounces = 2;

        [Tooltip("Rays shot into the cave from every texel to gather bounced light and ambient occlusion. More = smoother, slower. It is rounded up to a square number. The blur below already pools the rays of the surrounding texels, so a few go a long way: 9 is within half a percent of a 400 ray bake.")]
        [Range(4, 256)] public int raysPerTexel = 9;

        [Tooltip("Shadow rays per lamp per texel. They soften the edge of shadows, so more only matters with a big Lamp Radius.")]
        [Range(1, 64)] public int shadowRays = 6;

        [Tooltip("Blur radius in texels for the noise in the bounced light. It never blurs across edges. 0 = off.")]
        [Range(0, 8)] public int denoiseRadius = 3;

        [Header("Light")]
        [Tooltip("Realistic: light fades with the square of the distance, like a real lamp (and like URP's real-time lights): bright close to a lamp, dark between lamps, " +
                 "shadows stay deep. Soft: the old flat fade that reaches the whole range, which lights everything about evenly.")]
        public CaveLampFalloff lampFalloff = CaveLampFalloff.Realistic;

        [Tooltip("How bright the lamps are in the bake: multiplies Lamp Light Intensity (in the Lamp Lights section above). Realistic light drops off fast, so it needs more than the flat fade did: " +
                 "5 gives the same overall brightness as the old flat fade at Intensity 1, but with bright pools round the lamps and dark stretches between them. Turn it up to brighten the cave, down to darken it.")]
        [Range(0f, 20f)] public float lampBrightness = 5f;

        [Tooltip("The strip lights hung from the ceiling shine down, not up into the ceiling, because their housing blocks that side. Off: they shine in every direction.")]
        public bool ceilingLampsShineDown = true;

        [Tooltip("Size of the lamp bulbs in metres. Bigger lamps cast softer shadows.")]
        [Range(0f, 1f)] public float lampRadius = 0.12f;

        [Tooltip("Multiplies the bounced light (indirect light). 0 = only direct light from the lamps.")]
        [Range(0f, 3f)] public float bounceIntensity = 1f;

        [Tooltip("Light that comes from everywhere, dimmed in corners and crevices. Black = none.")]
        [ColorUsage(false, true)] public Color ambientColor = Color.black;

        [Tooltip("How far (metres) an occluder has to be before it stops darkening the ambient light. Small = only tight crevices go dark.")]
        [Range(0.1f, 20f)] public float ambientRange = 3f;

        [Header("Surfaces (for bounced light)")]
        [Tooltip("Average colour of the rock. Light that bounces off the walls is tinted by this.")]
        public Color rockAlbedo = new Color(0.46f, 0.43f, 0.40f);

        [Tooltip("Average colour of the dirt path.")]
        public Color dirtAlbedo = new Color(0.50f, 0.40f, 0.30f);

        [Header("Output")]
        [Tooltip("Also bake the direction the light comes from, so the normal maps still react to the baked light. Costs one more texture the same size.")]
        public bool directional = true;

        [Tooltip("Props that were spawned in the cave (crates, barrels, timber frames) block the lamps' light and cast shadows on the walls and floor.")]
        public bool propsCastShadows = true;

        [Tooltip("Props (crates, barrels, timber frames, your own prefabs) get the baked light too: each one is lit by the lamps and the light bounced off the cave round it, " +
                 "like a light probe, so a crate is bright on the side that faces a lamp. Off: props only get the scene's own lighting. " +
                 "Costs a little at runtime: every prop mesh gets its own light values, which stops it being batched by the SRP Batcher.")]
        public bool lightProps = true;

        /// <summary>The numbers one bake really uses, with the quality preset and platform limits applied.</summary>
        public LmParams Resolve(bool linearColorSpace, bool mobile)
        {
            var p = new LmParams();
            CaveBakeQuality q = quality;
            if (mobile && q != CaveBakeQuality.Custom && q != CaveBakeQuality.Low) q = CaveBakeQuality.Low;

            switch (q)
            {
                case CaveBakeQuality.Low: p.bounces = 1; p.rays = 4; p.shadowRays = 3; p.denoise = 3; p.probeRays = 64; break;
                case CaveBakeQuality.Medium: p.bounces = 2; p.rays = 9; p.shadowRays = 6; p.denoise = 3; p.probeRays = 128; break;
                case CaveBakeQuality.High: p.bounces = 3; p.rays = 36; p.shadowRays = 12; p.denoise = 2; p.probeRays = 256; break;
                default: p.bounces = bounces; p.rays = raysPerTexel; p.shadowRays = shadowRays; p.denoise = denoiseRadius; p.probeRays = Mathf.Clamp(raysPerTexel * 8, 32, 512); break;
            }

            p.texelsPerMeter = Mathf.Max(0.5f, texelsPerMeter);
            p.maxSize = (int)maxSize;
            if (mobile && quality != CaveBakeQuality.Custom)
            {
                // A phone-class CPU is several times slower than a PC: fewer texels keeps the bake bearable.
                p.texelsPerMeter = Mathf.Min(p.texelsPerMeter, 5f);
                if (p.maxSize > 1024) p.maxSize = 1024;
            }

            p.lampRadius = Mathf.Max(0f, lampRadius);
            p.bounceIntensity = bounceIntensity;
            p.ambient = ToLinear(ambientColor, linearColorSpace);
            p.ambientRange = Mathf.Max(0.05f, ambientRange);
            p.rockAlbedo = ToLinear(rockAlbedo, linearColorSpace);
            p.dirtAlbedo = ToLinear(dirtAlbedo, linearColorSpace);
            p.directional = directional;
            p.propsCastShadows = propsCastShadows;
            return p;
        }

        public static Color ToLinear(Color c, bool linearColorSpace)
        {
            return linearColorSpace ? c.linear : c;
        }
    }

    /// <summary>The settings of one bake with presets and platform limits already applied. Plain data, safe on worker threads.</summary>
    public struct LmParams
    {
        public int bounces;
        public int rays;
        public int shadowRays;
        public int denoise;
        public float texelsPerMeter;
        public int maxSize;
        public float lampRadius;
        public float bounceIntensity;
        public Color ambient;
        public float ambientRange;
        public Color rockAlbedo;
        public Color dirtAlbedo;
        public bool directional;
        public bool propsCastShadows;
        public int probeRays;           // rays over the sphere for each prop's light probe
        public int seed;
    }
}
