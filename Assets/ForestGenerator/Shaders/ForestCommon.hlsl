// Havoc Forest: inputs shared by every forest shader. Include after URP's Core.hlsl / Lighting.hlsl.
#ifndef HAVOC_FOREST_COMMON_INCLUDED
#define HAVOC_FOREST_COMMON_INCLUDED

// ---- The generated forest (set as globals by ForestWorld, so one material serves every forest)
TEXTURE2D(_ForestControl0);     SAMPLER(sampler_ForestControl0);   // r dirt, g gravel, b moss, a rock
TEXTURE2D(_ForestControl1);                                        // r grass, g leaf litter, b roots, a needles
TEXTURE2D(_ForestLightMap);     SAMPLER(sampler_ForestLightMap);   // r sky light (AO), g sun through the canopy
float4 _ForestMapParams;        // xy = 1 / terrain size, zw = terrain size, in metres
float4x4 _ForestWorldToMap;     // world position -> (u, _, v) on the ground maps

float2 ForestMapUV(float3 positionWS)
{
    return mul(_ForestWorldToMap, float4(positionWS, 1.0)).xz;
}

// ---- Fake lamp light (set by ForestLampManager every frame for the lamps nearest the camera).
// Evaluated per vertex: a soft pool of light around every lamp, no shadows, no real-time light cost. Unused slots have
// zero colour.
#define FOREST_MAX_LAMPS 8
float4 _ForestLampPos[FOREST_MAX_LAMPS];      // xyz = world position, w = 1 / range^2
float4 _ForestLampColor[FOREST_MAX_LAMPS];    // rgb = linear colour * intensity

half3 ForestLampLight(float3 positionWS, half3 normalWS)
{
    half3 sum = half3(0.0h, 0.0h, 0.0h);
    UNITY_UNROLL
    for (int i = 0; i < FOREST_MAX_LAMPS; i++)
    {
        float3 d = _ForestLampPos[i].xyz - positionWS;
        float d2 = dot(d, d);
        float window = saturate(1.0 - d2 * _ForestLampPos[i].w);
        window *= window;
        float atten = window / (1.0 + d2 * 0.08);
        half ndl = saturate(dot(normalWS, (half3)(d * rsqrt(max(d2, 1e-4)))) * 0.7h + 0.3h);   // wrapped: lamps light undersides a little
        sum += (half3)_ForestLampColor[i].rgb * (half)atten * ndl;
    }
    return sum;
}

// ---- Real-time additional lights (the small pool of real lamp lights near the player and the flashlight), Forward and
// Forward+, with light cookies (the flashlight's beam pattern) when _LIGHT_COOKIES is on.
#if defined(_ADDITIONAL_LIGHTS)
half3 ForestAdditionalLights(float3 positionWS, half3 normalWS, float2 normalizedScreenUV)
{
    half3 sum = half3(0.0h, 0.0h, 0.0h);
    InputData inputData = (InputData)0;          // the cluster light loop reads these two
    inputData.positionWS = positionWS;
    inputData.normalizedScreenSpaceUV = normalizedScreenUV;
    uint pixelLightCount = GetAdditionalLightsCount();
#if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        Light light = GetAdditionalLight(lightIndex, positionWS);
        sum += light.color * (light.distanceAttenuation * saturate(dot(normalWS, light.direction)));
    }
#endif
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, positionWS, half4(1.0h, 1.0h, 1.0h, 1.0h));   // this overload applies cookies
        sum += light.color * (light.distanceAttenuation * light.shadowAttenuation * saturate(dot(normalWS, light.direction)));
    LIGHT_LOOP_END
    return sum;
}
#endif

#endif
