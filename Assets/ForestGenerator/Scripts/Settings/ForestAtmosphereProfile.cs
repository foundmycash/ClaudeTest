using UnityEngine;

namespace HavocForest
{
    /// <summary>
    /// One look for the forest: fog, ambient (sky) light, sun or moon, and lamp brightness. The Forest Atmosphere component
    /// blends between a Calm and a Dread profile as the player goes deeper in, and horror events can push their own
    /// profile on top. The defaults are a foggy night lit by a low moon; the daytime look is kept as the Day Calm / Day
    /// Dread profiles in Settings.
    /// </summary>
    [CreateAssetMenu(menuName = "Havoc/Forest/Atmosphere Profile", fileName = "ForestAtmosphere", order = 3)]
    public class ForestAtmosphereProfile : ScriptableObject
    {
        [Header("Fog")]
        [Tooltip("Colour of the fog. The camera background is set to it too, so distant trees fade into it. With the volumetric fog this is the colour it shows where it is thick; towards the sun / moon it is brighter. Keep it in proportion to the ambient and moon light: much darker fog than the light it gets makes the fog swallow lamp and flashlight light.")]
        public Color fogColor = new Color(0.085f, 0.105f, 0.14f);

        [Tooltip("How thick the fog is. It is fully opaque at about 2.15 / density metres (0.028 = 77 m), and the camera's far plane is pulled in to there, which saves drawing what the fog hides anyway. (Unity fog: this is its exponential squared density. Volumetric fog: it is set as thick as needed to be opaque at the same distance, which makes it thicker close up than Unity fog.)")]
        [Range(0.004f, 0.12f)] public float fogDensity = 0.03f;

        [Header("Ambient (sky light)")]
        public Color skyAmbient = new Color(0.1f, 0.12f, 0.17f);
        public Color horizonAmbient = new Color(0.06f, 0.07f, 0.095f);
        public Color groundAmbient = new Color(0.02f, 0.02f, 0.025f);

        [Header("Sun / Moon (the scene's directional light)")]
        public Color sunColor = new Color(0.62f, 0.72f, 1f);
        [Range(0f, 3f)] public float sunIntensity = 0.07f;

        [Tooltip("Height of the sun or moon above the horizon in degrees.")]
        [Range(1f, 90f)] public float sunElevation = 15f;

        [Tooltip("Compass direction the light shines towards, in degrees (0 = towards +Z). 180 = it comes from +Z, where the Cold Night skybox has its moon. The Calm profile's direction is also used for the canopy shade baked into the ground when the forest is generated.")]
        [Range(0f, 360f)] public float sunAzimuth = 180f;

        [Header("Lamps")]
        [Tooltip("Multiplier on the brightness of every streetlight.")]
        [Range(0f, 3f)] public float lampBrightness = 1.2f;

        [Tooltip("Extra chance per second that a working lamp drops out for a moment.")]
        [Range(0f, 1f)] public float lampDropouts = 0f;

        public ForestAtmosphereState ToState()
        {
            return new ForestAtmosphereState
            {
                fogColor = fogColor.linear,
                fogDensity = fogDensity,
                skyAmbient = skyAmbient.linear,
                horizonAmbient = horizonAmbient.linear,
                groundAmbient = groundAmbient.linear,
                sunColor = sunColor.linear,
                sunIntensity = sunIntensity,
                sunElevation = sunElevation,
                sunAzimuth = sunAzimuth,
                lampBrightness = lampBrightness,
                lampDropouts = lampDropouts
            };
        }
    }

    /// <summary>A profile's values, ready to blend. Colours are linear.</summary>
    public struct ForestAtmosphereState
    {
        public Color fogColor;
        public float fogDensity;
        public Color skyAmbient;
        public Color horizonAmbient;
        public Color groundAmbient;
        public Color sunColor;
        public float sunIntensity;
        public float sunElevation;
        public float sunAzimuth;
        public float lampBrightness;
        public float lampDropouts;

        public static ForestAtmosphereState Lerp(ForestAtmosphereState a, ForestAtmosphereState b, float t)
        {
            return new ForestAtmosphereState
            {
                fogColor = Color.Lerp(a.fogColor, b.fogColor, t),
                fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t),
                skyAmbient = Color.Lerp(a.skyAmbient, b.skyAmbient, t),
                horizonAmbient = Color.Lerp(a.horizonAmbient, b.horizonAmbient, t),
                groundAmbient = Color.Lerp(a.groundAmbient, b.groundAmbient, t),
                sunColor = Color.Lerp(a.sunColor, b.sunColor, t),
                sunIntensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t),
                sunElevation = Mathf.Lerp(a.sunElevation, b.sunElevation, t),
                sunAzimuth = Mathf.LerpAngle(a.sunAzimuth, b.sunAzimuth, t),
                lampBrightness = Mathf.Lerp(a.lampBrightness, b.lampBrightness, t),
                lampDropouts = Mathf.Lerp(a.lampDropouts, b.lampDropouts, t)
            };
        }

        /// <summary>Distance in metres at which exponential squared fog of this density is 99% opaque.</summary>
        public float OpaqueDistance
        {
            get { return 2.146f / Mathf.Max(0.0001f, fogDensity); }
        }

        /// <summary>The direction the sunlight travels (from the sun towards the ground).</summary>
        public Vector3 SunDirection
        {
            get { return Quaternion.Euler(sunElevation, sunAzimuth, 0f) * Vector3.forward; }
        }
    }
}
