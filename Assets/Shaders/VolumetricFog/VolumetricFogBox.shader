// VR-friendly volumetric fog for a box (cube) volume. Put it on a cube through the VolumetricFog component.
//
// How it works
//   The cube's BACK faces are drawn, so the fog also shows when the camera is inside the box. For every pixel the view
//   ray is intersected with the box, clipped against the scene depth and marched in a few jittered steps. Each step
//   adds in-scattered light (ambient + main light + every additional light, e.g. point lights) weighted by a
//   Henyey-Greenstein phase function, and the ray loses light with Beer-Lambert extinction. Density is shaped by
//   animated 3D noise, a height falloff and soft box edges.
//
// Stereo / VR
//   Everything is evaluated per eye from the usual URP stereo-aware values (camera position, depth texture, matrices),
//   so it works with single-pass instanced, multiview and multi-pass rendering. No shadow lookups are done in the
//   loop, which keeps it affordable on standalone headsets.
Shader "VR Volumetric Fog/Box Volume"
{
    Properties
    {
        [Header(Look)]
        _FogColor ("Fog Color (scattering tint)", Color) = (0.78, 0.85, 0.95, 1)
        _Density ("Density (per meter)", Range(0, 3)) = 0.7
        _Anisotropy ("Light Anisotropy (forward scattering)", Range(-0.9, 0.95)) = 0.45
        _LightIntensity ("Light Intensity", Range(0, 8)) = 1
        _AmbientIntensity ("Ambient Intensity", Range(0, 4)) = 0.6
        _LightCoreRadius ("Light Core Radius (meters)", Range(0.05, 3)) = 0.4
        _LightAbsorption ("Light Absorption (fog swallows light from lamps)", Range(0, 2)) = 0

        [Header(Shape)]
        _EdgeFade ("Edge Fade (meters)", Range(0, 10)) = 1.5
        _HeightFalloff ("Height Falloff (per meter)", Range(0, 2)) = 0
        _HeightOffset ("Height Falloff Start (meters above bottom)", Range(0, 20)) = 0
        _NearFade ("Near Camera Fade (meters)", Range(0, 3)) = 0.6
        _MaxDistance ("Max Distance (meters)", Range(1, 200)) = 60

        [Header(Noise)]
        [NoScaleOffset] _NoiseTex ("3D Noise", 3D) = "white" {}
        _NoiseScale ("Noise Size (meters per tile)", Range(0.5, 40)) = 4
        _NoiseStrength ("Noise Strength", Range(0, 1)) = 1
        _NoiseCoverage ("Noise Coverage (higher = more gaps)", Range(0, 0.95)) = 0.4
        _NoiseSharpness ("Noise Sharpness", Range(0.2, 4)) = 1.8
        _NoiseDetail ("Fine Detail", Range(0, 1)) = 0.7
        _NoiseDetailScale ("Fine Detail Scale", Range(1.5, 8)) = 3.1
        _WindVelocity ("Wind (meters per second)", Vector) = (0.25, 0.05, 0.15, 0)
        _Turbulence ("Turbulence (internal swirling motion)", Range(0, 2)) = 1
        [HideInInspector] _NoiseTexelsPerTile ("Noise Texels Per Tile", Float) = 48

        [Header(Quality)]
        _Steps ("Max Steps", Range(4, 64)) = 20
        _StepSize ("Preferred Step Size (meters)", Range(0.05, 3)) = 0.35
        _MaxLights ("Max Additional Lights Per Step", Range(0, 8)) = 4
        _Jitter ("Step Jitter", Range(0, 1)) = 1

        [HideInInspector] _BoxCenter ("Box Center (object space)", Vector) = (0, 0, 0, 0)
        [HideInInspector] _BoxExtents ("Box Half Size (object space)", Vector) = (0.5, 0.5, 0.5, 0)
        [HideInInspector] [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 8
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "VolumetricFog"
            Tags { "LightMode" = "UniversalForward" }

            // Premultiplied alpha: result = scattered light + background * transmittance
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Front
            ZTest [_ZTest]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_local_fragment _ _SCENE_DEPTH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE3D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _FogColor;
                float _Density;
                float _Anisotropy;
                float _LightIntensity;
                float _AmbientIntensity;
                float _LightCoreRadius;
                float _LightAbsorption;

                float _EdgeFade;
                float _HeightFalloff;
                float _HeightOffset;
                float _NearFade;
                float _MaxDistance;

                float _NoiseScale;
                float _NoiseStrength;
                float _NoiseCoverage;
                float _NoiseSharpness;
                float _NoiseDetail;
                float _NoiseDetailScale;
                float4 _WindVelocity;
                float _Turbulence;
                float _NoiseTexelsPerTile;

                float _Steps;
                float _StepSize;
                float _MaxLights;
                float _Jitter;

                float4 _BoxCenter;
                float4 _BoxExtents;
            CBUFFER_END

            // How much fog (optical depth) one ray step should cover at most in thick fog. Lower = finer, costlier.
            static const float kOpticalDepthPerStep = 0.45;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                return output;
            }

            // Henyey-Greenstein, scaled so that g = 0 gives 1 (URP lights are not divided by 4*PI either).
            // x^1.5 is written as x * sqrt(x) because this runs for every light at every step.
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float d = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
                return (1.0 - g2) / (d * sqrt(d));
            }

            // Unstructured white noise from an integer hash (no sin()). A fixed screen-space pattern with no time term,
            // so it cannot flicker in a headset. Structured patterns such as interleaved gradient noise show up as a
            // regular crosshatch in smooth fog; plain grain is far less noticeable.
            float StepJitterNoise(float2 pixel)
            {
                uint2 q = uint2(pixel) * uint2(1597334673u, 3812015801u);
                uint n = (q.x ^ q.y) * 1597334673u;
                return float(n >> 8) * (1.0 / 16777216.0);
            }

            // Distance from a sample point to an additional light, so the fog between them can swallow some of its light.
            float AdditionalLightDistance(uint loopIndex, float3 posWS)
            {
                #if USE_CLUSTER_LIGHT_LOOP
                    int index = (int)loopIndex;
                #else
                    int index = GetPerObjectLightIndex(loopIndex);
                #endif
                #if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
                    float4 lightPositionWS = _AdditionalLightsBuffer[index].position;
                #else
                    float4 lightPositionWS = _AdditionalLightsPosition[index];
                #endif
                // Directional lights keep their direction in xyz and have w = 0: no distance to speak of.
                return length(lightPositionWS.xyz - posWS * lightPositionWS.w) * lightPositionWS.w;
            }

            // Fog density at a point of the ray. posOS is in the cube's object space, t is the distance from the eye and
            // stepLength is the distance between ray samples around this point.
            float FogDensity(float3 posWS, float3 posOS, float3 objectScale, float t, float stepLength)
            {
                // Soft box edges, in meters, so the cube itself never shows.
                float3 faceDistance = (_BoxExtents.xyz - abs(posOS - _BoxCenter.xyz)) * objectScale;
                float edge = smoothstep(0.0, max(_EdgeFade, 1e-3), min(faceDistance.x, min(faceDistance.y, faceDistance.z)));

                // Denser near the bottom of the box.
                float height = (posOS.y - (_BoxCenter.y - _BoxExtents.y)) * objectScale.y - _HeightOffset;
                float heightFactor = exp(-max(height, 0.0) * _HeightFalloff);

                float density = _Density * edge * heightFactor;

                // Fade in close to the eyes and out at the maximum distance.
                density *= smoothstep(0.0, max(_NearFade, 1e-3), t);
                density *= 1.0 - smoothstep(_MaxDistance * 0.7, _MaxDistance, t);

                if (density > 1e-4 && _NoiseStrength > 0.0)
                {
                    float time = _Time.y;
                    float3 wind = _WindVelocity.xyz * time;

                    // Turbulence: besides drifting with the wind, the two noise layers slowly meander against each other
                    // (different periods, so it never repeats), so the fog keeps evolving even when the wind is slow.
                    // Each layer moves on a slow circle (constant speed, so it never stalls), the two in opposite
                    // directions so the fog shears and billows, plus a little wobble on top.
                    float3 baseSwirl = _Turbulence * (3.0 * float3(sin(time * 0.05), 0.0, -cos(time * 0.05))
                                     + 0.6 * float3(sin(time * 0.21), 0.4 * sin(time * 0.17 + 1.0), cos(time * 0.19)));
                    float3 detailSwirl = _Turbulence * (2.0 * float3(-sin(time * 0.09 + 1.0), 0.0, cos(time * 0.09 + 1.0))
                                       + 1.2 * float3(cos(time * 0.43 + 0.7), 0.5 * sin(time * 0.37), sin(time * 0.41 + 2.0)));

                    // Noise detail finer than the ray step cannot be sampled properly and would turn into static grain.
                    // Read a blurrier mip level instead, so only detail the steps can resolve is kept.
                    float texelMeters = _NoiseScale / max(_NoiseTexelsPerTile, 1.0);

                    float3 uvw = (posWS - wind - baseSwirl) / _NoiseScale;
                    float n = SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, uvw, max(0.0, log2(stepLength / texelMeters))).r;
                    if (_NoiseDetail > 0.0)
                    {
                        // Second, finer lookup that moves differently so the pattern never visibly repeats.
                        float3 uvw2 = (posWS - wind * 0.6 - detailSwirl) / _NoiseScale * _NoiseDetailScale + float3(0.37, 0.61, 0.13);
                        float detailLod = max(0.0, log2(stepLength * _NoiseDetailScale / texelMeters));
                        float n2 = SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, uvw2, detailLod).r;
                        n = lerp(n, n2, _NoiseDetail * 0.5);
                    }

                    float shaped = saturate((n - _NoiseCoverage) / max(1.0 - _NoiseCoverage, 1e-3));
                    shaped = pow(shaped, _NoiseSharpness);
                    density *= lerp(1.0, shaped, _NoiseStrength);
                }
                return density;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 cameraWS = GetCameraPositionWS();
                float3 toPixel = input.positionWS - cameraWS;
                float3 dirWS = normalize(toPixel);

                // Ray / box intersection in object space. dirOS is not normalized, so t is the same distance in meters
                // as in world space.
                float4x4 worldToObject = GetWorldToObjectMatrix();
                float3 originOS = mul(worldToObject, float4(cameraWS, 1.0)).xyz;
                float3 dirOS = mul((float3x3)worldToObject, dirWS);
                // Avoid dividing by zero for rays parallel to a face, keeping the sign of each component.
                float3 safeDir = max(abs(dirOS), 1e-6) * (step(0.0, dirOS) * 2.0 - 1.0);
                float3 invDir = rcp(safeDir);
                float3 t0 = (_BoxCenter.xyz - _BoxExtents.xyz - originOS) * invDir;
                float3 t1 = (_BoxCenter.xyz + _BoxExtents.xyz - originOS) * invDir;
                float3 tMinV = min(t0, t1);
                float3 tMaxV = max(t0, t1);
                float tNear = max(0.0, max(tMinV.x, max(tMinV.y, tMinV.z)));
                float tFar = min(tMaxV.x, min(tMaxV.y, tMaxV.z));

                #if defined(_SCENE_DEPTH)
                    // Stop at whatever solid geometry is inside the box.
                    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    float eyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                    float sceneDistance = eyeDepth / max(dot(dirWS, GetViewForwardDir()), 1e-3);
                    tFar = min(tFar, sceneDistance);
                #endif
                tFar = min(tFar, _MaxDistance);

                float rayLength = tFar - tNear;
                clip(rayLength - 1e-4);

                float3 objectToWorldX = float3(UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20);
                float3 objectToWorldY = float3(UNITY_MATRIX_M._m01, UNITY_MATRIX_M._m11, UNITY_MATRIX_M._m21);
                float3 objectToWorldZ = float3(UNITY_MATRIX_M._m02, UNITY_MATRIX_M._m12, UNITY_MATRIX_M._m22);
                float3 objectScale = float3(length(objectToWorldX), length(objectToWorldY), length(objectToWorldZ));

                // Adaptive steps along the part of the ray that is inside the fog. Each step is sized from the density
                // found at the previous one so that it covers about the same optical depth: short steps in thick fog
                // (where the ray is still visible and lamps glow) and long steps through thin gaps. A step is never
                // longer than an even split of what is left, so the ray always reaches its end with the last step.
                // Each step is sampled at a random spot inside itself (jitter) to hide the stepping.
                int steps = (int)clamp(ceil(rayLength / max(_StepSize, 0.01)), 1.0, _Steps);
                float jitter = StepJitterNoise(input.positionCS.xy) * _Jitter;
                int maxLights = (int)_MaxLights;
                float previousDensity = _Density * 0.4;   // first guess, before anything has been sampled
                float rayPosition = tNear;

                // Light that does not depend on the position in the fog.
                Light mainLight = GetMainLight();
                float3 mainLightScatter = mainLight.color * (mainLight.distanceAttenuation * _LightIntensity
                                        * HenyeyGreenstein(dot(mainLight.direction, dirWS), _Anisotropy));
                float3 ambient = SampleSH(float3(0.0, 1.0, 0.0)) * _AmbientIntensity;

                InputData inputData = (InputData)0;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                float3 scattered = 0;
                float transmittance = 1.0;

                [loop]
                for (int i = 0; i < steps; i++)
                {
                    float remaining = tFar - rayPosition;
                    int stepsLeft = steps - i;
                    float stepLength = remaining / stepsLeft;   // an even split of what is left
                    if (stepsLeft > 1)
                        stepLength = min(stepLength, max(kOpticalDepthPerStep / max(previousDensity, 1e-3), stepLength * 0.06));

                    float t = rayPosition + stepLength * jitter;
                    rayPosition += stepLength;
                    float3 posWS = cameraWS + dirWS * t;
                    float3 posOS = originOS + dirOS * t;

                    float density = FogDensity(posWS, posOS, objectScale, t, stepLength);
                    previousDensity = density;
                    if (density < 1e-4)
                        continue;

                    float3 lightIn = ambient + mainLightScatter;

                    #if defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX) || USE_CLUSTER_LIGHT_LOOP
                    if (maxLights > 0)
                    {
                        inputData.positionWS = posWS;
                        uint pixelLightCount = GetAdditionalLightsCount();
                        int lightsUsed = 0;

                        #if USE_CLUSTER_LIGHT_LOOP
                        // Additional directional lights come first in the cluster light list.
                        [loop] for (uint dirLight = 0; dirLight < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); dirLight++)
                        {
                            Light light = GetAdditionalLight(dirLight, posWS);
                            lightIn += light.color * (_LightIntensity * HenyeyGreenstein(dot(light.direction, dirWS), _Anisotropy));
                        }
                        #endif

                        LIGHT_LOOP_BEGIN(pixelLightCount)
                            Light light = GetAdditionalLight(lightIndex, posWS);
                            // 1 / (d^2 + r^2): keeps the glow finite right next to a light.
                            float attenuation = light.distanceAttenuation / (1.0 + light.distanceAttenuation * _LightCoreRadius * _LightCoreRadius);
                            // Dense fog swallows light on its way from the lamp (Beer-Lambert with the base density):
                            // bright halos near lamps, darkness between them.
                            attenuation *= exp(-_LightAbsorption * _Density * AdditionalLightDistance(lightIndex, posWS));
                            lightIn += light.color * (attenuation * _LightIntensity * HenyeyGreenstein(dot(light.direction, dirWS), _Anisotropy));
                            lightsUsed++;
                            if (lightsUsed >= maxLights)
                                break;
                        LIGHT_LOOP_END
                    }
                    #endif

                    // Energy-conserving integration of scattering over one step (Frostbite-style).
                    float stepTransmittance = exp(-density * stepLength);
                    scattered += transmittance * (1.0 - stepTransmittance) * lightIn * _FogColor.rgb;
                    transmittance *= stepTransmittance;

                    if (transmittance < 0.01)
                        break;
                }

                return half4(scattered, 1.0 - transmittance);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
