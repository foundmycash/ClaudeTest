// Havoc/Forest Ground
//
// The one material for the whole forest floor. Eight surfaces (dirt, gravel, moss, rock, grass, leaf litter, roots,
// needles) live in two texture arrays: colour + height, and normals. Two blend maps made by the forest generator say how
// much of each surface goes where; every pixel only samples the strongest 3 surfaces (2 in the Quest 2 "lite" mode), so the
// cost does not grow with the number of surfaces. Where surfaces meet, their height decides which one shows (stones poke
// through dirt, moss fills the gaps), which gives ragged natural edges instead of soft smears.
//
// Against visible tiling every surface is rotated by its own angle, a large-scale noise varies the colour, and (full
// quality) the strongest surface is mixed with a second, much larger copy of itself in the distance.
//
// Lighting: the sun (with the canopy shade baked by the generator), ambient light (with baked ambient occlusion), the
// streetlights (cheap per-vertex lamp light from ForestLampManager) and URP's real-time lights. Built-in fog.
//
// Sampling per pixel: 3 maps (2 blend + 1 light) + 3 colour (+3 normal, +1 anti-tiling, +1 macro) = 7 to 11 on full
// quality, 3 + 2 + 1 = 6 on lite.
Shader "Havoc/Forest Ground"
{
    Properties
    {
        [Header(Surfaces . set by the Ground Layers asset)]
        [NoScaleOffset] _AlbedoArray ("Colour + Height Array", 2DArray) = "" {}
        [NoScaleOffset] _NormalArray ("Normal Array", 2DArray) = "" {}
        _LayerTiling0 ("Repeats per Metre: Dirt Gravel Moss Rock", Vector) = (0.33, 0.45, 0.38, 0.22)
        _LayerTiling1 ("Repeats per Metre: Grass Leaves Roots Needles", Vector) = (0.45, 0.42, 0.31, 0.42)
        _LayerSmoothness0 ("Smoothness: Dirt Gravel Moss Rock", Vector) = (0.08, 0.15, 0.05, 0.2)
        _LayerSmoothness1 ("Smoothness: Grass Leaves Roots Needles", Vector) = (0.05, 0.1, 0.1, 0.06)
        _LayerNormalStrength0 ("Normal Strength: Dirt Gravel Moss Rock", Vector) = (1, 1, 1, 1)
        _LayerNormalStrength1 ("Normal Strength: Grass Leaves Roots Needles", Vector) = (1, 1, 1, 1)
        _Tint0 ("Dirt Tint", Color) = (1, 1, 1, 1)
        _Tint1 ("Gravel Tint", Color) = (1, 1, 1, 1)
        _Tint2 ("Moss Tint", Color) = (1, 1, 1, 1)
        _Tint3 ("Rock Tint", Color) = (1, 1, 1, 1)
        _Tint4 ("Grass Tint", Color) = (1, 1, 1, 1)
        _Tint5 ("Leaf Litter Tint", Color) = (1, 1, 1, 1)
        _Tint6 ("Roots Tint", Color) = (1, 1, 1, 1)
        _Tint7 ("Needles Tint", Color) = (1, 1, 1, 1)

        [Header(Blending)]
        _HeightBlendDepth ("Height Blend Depth (lower = sharper edges)", Range(0.02, 1)) = 0.22
        _HeightInfluence ("Height Influence", Range(0, 1.5)) = 0.6
        _WeightContrast ("Weight Contrast", Range(1, 4)) = 1.4

        [Header(Variation)]
        [NoScaleOffset] _MacroNoise ("Macro Noise (tileable)", 2D) = "gray" {}
        _MacroSize ("Macro Variation Size (m)", Float) = 38
        _MacroStrength ("Macro Variation Strength", Range(0, 1)) = 0.4
        _AntiTile ("Distance Anti-Tiling (full quality only)", Range(0, 1)) = 0.4

        [Header(Lighting)]
        _AOStrength ("Ambient Occlusion (light map)", Range(0, 1)) = 1
        _CanopyShade ("Canopy Shade (light map)", Range(0, 1)) = 1
        _LampStrength ("Lamp Light Strength", Range(0, 4)) = 1
        _Specular ("Specular Amount", Range(0, 1)) = 0.25
        [Toggle(_FOREST_NORMALMAPS)] _UseNormalMaps ("Normal Maps (full quality only)", Float) = 1

        [Header(Debug)]
        [Toggle(_FOREST_DEBUG)] _DebugOn ("Debug View", Float) = 0
        [Enum(Surfaces,0,LightMap,1,Strongest,2)] _DebugMode ("Debug Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_AlbedoArray);  SAMPLER(sampler_AlbedoArray);
        TEXTURE2D_ARRAY(_NormalArray);  SAMPLER(sampler_NormalArray);
        TEXTURE2D(_MacroNoise);         SAMPLER(sampler_MacroNoise);

        // Every pass must see the same material constants for the SRP Batcher.
        CBUFFER_START(UnityPerMaterial)
            float4 _LayerTiling0;
            float4 _LayerTiling1;
            half4 _LayerSmoothness0;
            half4 _LayerSmoothness1;
            half4 _LayerNormalStrength0;
            half4 _LayerNormalStrength1;
            half4 _Tint0, _Tint1, _Tint2, _Tint3, _Tint4, _Tint5, _Tint6, _Tint7;
            half _HeightBlendDepth;
            half _HeightInfluence;
            half _WeightContrast;
            float _MacroSize;
            half _MacroStrength;
            half _AntiTile;
            half _AOStrength;
            half _CanopyShade;
            half _LampStrength;
            half _Specular;
            half _UseNormalMaps;
            half _DebugOn;
            half _DebugMode;
        CBUFFER_END
        ENDHLSL

        // ------------------------------------------------------------------ main lit pass
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            // Quest 2: 2 surfaces per pixel, no normal maps, no anti-tiling sample (ForestDetail sets it globally)
            #pragma multi_compile_fragment _ _FOREST_LITE
            #pragma shader_feature_local_fragment _FOREST_NORMALMAPS
            #pragma shader_feature_local_fragment _FOREST_DEBUG

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "ForestCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;      // metres from the terrain's corner
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half4  lampFog    : TEXCOORD3;      // rgb = lamp (and per-vertex real-time) light, a = fog factor
                half3  sh         : TEXCOORD4;
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD5;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // cos, sin of each surface's own rotation (multiples of 0.82 rad), so no two surfaces tile along the same axes
            static const float2 kLayerRot[8] =
            {
                float2(1.0, 0.0), float2(0.6822, 0.7311), float2(-0.0691, 0.9976), float2(-0.7766, 0.6300),
                float2(-0.9904, -0.1380), float2(-0.5753, -0.8180), float2(0.2061, -0.9785), float2(0.8561, -0.5168)
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs vpi = GetVertexPositionInputs(input.positionOS.xyz);
                half3 n = (half3)TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = vpi.positionCS;
                o.positionWS = vpi.positionWS;
                o.normalWS = n;
                o.uv = input.uv;

                half3 lamp = ForestLampLight(vpi.positionWS, n) * _LampStrength;
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                lamp += VertexLighting(vpi.positionWS, n);
            #endif
                o.lampFog = half4(lamp, ComputeFogFactor(vpi.positionCS.z));
                o.sh = SampleSH(n);
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                o.shadowCoord = GetShadowCoord(vpi);
            #endif
                return o;
            }

            float Pick8(float4 a, float4 b, int i)
            {
                float4 v = i < 4 ? a : b;
                int j = i & 3;
                return j == 0 ? v.x : (j == 1 ? v.y : (j == 2 ? v.z : v.w));
            }

            half3 PickTint(int i)
            {
                half3 t = _Tint0.rgb;
                t = i == 1 ? _Tint1.rgb : t;
                t = i == 2 ? _Tint2.rgb : t;
                t = i == 3 ? _Tint3.rgb : t;
                t = i == 4 ? _Tint4.rgb : t;
                t = i == 5 ? _Tint5.rgb : t;
                t = i == 6 ? _Tint6.rgb : t;
                t = i == 7 ? _Tint7.rgb : t;
                return t;
            }

            // A surface's texture coordinates (and their screen-space gradients) from the position in metres.
            // The gradients are given explicitly: which surface a pixel samples can change from one pixel to the next,
            // and the hardware would otherwise see a jump in the coordinates there and pick the smallest mip (a seam).
            void LayerCoords(int idx, float2 metres, float2 dx, float2 dy, out float2 uv, out float2 gx, out float2 gy)
            {
                float t = Pick8(_LayerTiling0, _LayerTiling1, idx);
                float2 cs = kLayerRot[idx];
                float2 m = metres * t;
                uv = float2(cs.x * m.x - cs.y * m.y, cs.y * m.x + cs.x * m.y) + idx * 0.37;
                gx = float2(cs.x * dx.x - cs.y * dx.y, cs.y * dx.x + cs.x * dx.y) * t;
                gy = float2(cs.x * dy.x - cs.y * dy.y, cs.y * dy.x + cs.x * dy.y) * t;
            }

            half4 SampleAlbedo(int idx, float2 metres, float2 dx, float2 dy)
            {
                float2 uv, gx, gy;
                LayerCoords(idx, metres, dx, dy, uv, gx, gy);
                return SAMPLE_TEXTURE2D_ARRAY_GRAD(_AlbedoArray, sampler_AlbedoArray, uv, idx, gx, gy);
            }

            // Normal offset of one surface, turned back from the surface's rotation into the terrain's x/z frame.
            half2 SampleNormalOffset(int idx, float2 metres, float2 dx, float2 dy)
            {
                float2 uv, gx, gy;
                LayerCoords(idx, metres, dx, dy, uv, gx, gy);
                half2 n = SAMPLE_TEXTURE2D_ARRAY_GRAD(_NormalArray, sampler_NormalArray, uv, idx, gx, gy).rg * 2.0h - 1.0h;
                n *= (half)Pick8(_LayerNormalStrength0, _LayerNormalStrength1, idx);
                float2 cs = kLayerRot[idx];
                return half2(cs.x * n.x + cs.y * n.y, -cs.y * n.x + cs.x * n.y);
            }

            half3 DebugColor(int i)
            {
                half3 c = half3(0.45h, 0.3h, 0.15h);                // dirt
                c = i == 1 ? half3(0.75h, 0.75h, 0.7h) : c;         // gravel
                c = i == 2 ? half3(0.2h, 0.7h, 0.15h) : c;          // moss
                c = i == 3 ? half3(0.35h, 0.35h, 0.4h) : c;         // rock
                c = i == 4 ? half3(0.6h, 0.9h, 0.2h) : c;           // grass
                c = i == 5 ? half3(0.95h, 0.45h, 0.1h) : c;         // leaf litter
                c = i == 6 ? half3(0.5h, 0.15h, 0.35h) : c;         // roots
                c = i == 7 ? half3(0.65h, 0.5h, 0.3h) : c;          // needles
                return c;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 metres = input.uv;
                float2 mapUV = metres * _ForestMapParams.xy;
                half4 c0 = SAMPLE_TEXTURE2D(_ForestControl0, sampler_ForestControl0, mapUV);
                half4 c1 = SAMPLE_TEXTURE2D(_ForestControl1, sampler_ForestControl0, mapUV);
                half2 baked = SAMPLE_TEXTURE2D(_ForestLightMap, sampler_ForestLightMap, mapUV).rg;
                half3 N = normalize(input.normalWS);

                // ---- the strongest three surfaces (two on lite)
                half wv[8] = { c0.r, c0.g, c0.b, c0.a, c1.r, c1.g, c1.b, c1.a };
                int i0 = 0, i1 = 1, i2 = 2;
                half w0 = -1.0h, w1 = -1.0h, w2 = -1.0h;
                UNITY_UNROLL
                for (int k = 0; k < 8; k++)
                {
                    half v = wv[k];
                    bool g0 = v > w0;
                    bool g1 = v > w1;
                    bool g2 = v > w2;
                    w2 = g1 ? w1 : (g2 ? v : w2);
                    i2 = g1 ? i1 : (g2 ? k : i2);
                    w1 = g0 ? w0 : (g1 ? v : w1);
                    i1 = g0 ? i0 : (g1 ? k : i1);
                    w0 = g0 ? v : w0;
                    i0 = g0 ? k : i0;
                }
                w0 = pow(max(w0, 0.0h), _WeightContrast);
                w1 = pow(max(w1, 0.0h), _WeightContrast);
            #if defined(_FOREST_LITE)
                w2 = 0.0h;
            #else
                w2 = pow(max(w2, 0.0h), _WeightContrast);
            #endif

                float2 dx = ddx(metres);
                float2 dy = ddy(metres);
                half4 s0 = SampleAlbedo(i0, metres, dx, dy);
                half4 s1 = SampleAlbedo(i1, metres, dx, dy);
            #if defined(_FOREST_LITE)
                half4 s2 = half4(0.0h, 0.0h, 0.0h, 0.0h);
            #else
                half4 s2 = SampleAlbedo(i2, metres, dx, dy);
            #endif

                // ---- height blend: the surface that sits higher wins where two meet
                half a0 = w0 + s0.a * _HeightInfluence * saturate(w0 * 5.0h);
                half a1 = w1 + s1.a * _HeightInfluence * saturate(w1 * 5.0h);
                half a2 = w2 + s2.a * _HeightInfluence * saturate(w2 * 5.0h);
            #if defined(_FOREST_LITE)
                a2 = -10.0h;
            #endif
                half top = max(a0, max(a1, a2)) - _HeightBlendDepth;
                half b0 = max(a0 - top, 0.0h);
                half b1 = max(a1 - top, 0.0h);
                half b2 = max(a2 - top, 0.0h);
                half bs = b0 + b1 + b2 + 1e-4h;
                b0 /= bs; b1 /= bs; b2 /= bs;

                // ---- distance anti-tiling: the strongest surface mixed with a big, turned copy of itself
                half3 col0 = s0.rgb;
            #if !defined(_FOREST_LITE)
                {
                    float2 far = float2(metres.x * 0.54 - metres.y * 0.84, metres.x * 0.84 + metres.y * 0.54) * 0.29 + 0.5;
                    half4 sf = SampleAlbedo(i0, far, dx * 0.29, dy * 0.29);
                    float dist = distance(input.positionWS, GetCameraPositionWS());
                    col0 = lerp(col0, sf.rgb, _AntiTile * (half)saturate((dist - 5.0) / 25.0) * 0.6h);
                }
            #endif

                half3 albedo = col0 * PickTint(i0) * b0 + s1.rgb * PickTint(i1) * b1 + s2.rgb * PickTint(i2) * b2;
                half smooth = (half)Pick8(_LayerSmoothness0, _LayerSmoothness1, i0) * b0
                            + (half)Pick8(_LayerSmoothness0, _LayerSmoothness1, i1) * b1
                            + (half)Pick8(_LayerSmoothness0, _LayerSmoothness1, i2) * b2;

                // ---- large-scale colour variation
                half2 macro = SAMPLE_TEXTURE2D(_MacroNoise, sampler_MacroNoise, metres / _MacroSize).rg;
                albedo *= 1.0h + (macro.r - 0.5h) * 1.2h * _MacroStrength;
                albedo = lerp(albedo, albedo * half3(1.08h, 1.0h, 0.86h), saturate((macro.g - 0.5h) * 2.0h) * _MacroStrength);

            #if defined(_FOREST_NORMALMAPS) && !defined(_FOREST_LITE)
                half2 nt = SampleNormalOffset(i0, metres, dx, dy) * b0
                         + SampleNormalOffset(i1, metres, dx, dy) * b1
                         + SampleNormalOffset(i2, metres, dx, dy) * b2;
                half3 T = normalize(half3(1.0h, 0.0h, 0.0h) - N * N.x);
                half3 B = cross(T, N);
                N = normalize(N + T * nt.x + B * nt.y);
            #endif

            #if defined(_FOREST_DEBUG)
                if (_DebugMode < 0.5h)
                {
                    half3 dc = 0;
                    UNITY_UNROLL
                    for (int d = 0; d < 8; d++) dc += DebugColor(d) * wv[d];
                    return half4(dc, 1.0h);
                }
                if (_DebugMode < 1.5h) return half4(baked.r, baked.g, 0.0h, 1.0h);
                return half4(DebugColor(i0), 1.0h);
            #endif

                // ---- lighting
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord = input.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #else
                float4 shadowCoord = float4(0, 0, 0, 0);
            #endif
                Light mainLight = GetMainLight(shadowCoord);
                half ndl = saturate(dot(N, mainLight.direction));
                half sunThrough = lerp(1.0h, baked.g, _CanopyShade);
                half3 direct = mainLight.color * (ndl * mainLight.shadowAttenuation * mainLight.distanceAttenuation * sunThrough);

                half3 V = (half3)GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 H = normalize(mainLight.direction + V);
                half spec = pow(saturate(dot(N, H)), exp2(smooth * 9.0h + 1.0h)) * smooth * _Specular;

                half3 ambient = input.sh * lerp(1.0h, baked.r, _AOStrength);
                half3 lights = direct + ambient + input.lampFog.rgb * lerp(1.0h, baked.r, 0.5h);
            #if defined(_ADDITIONAL_LIGHTS)
                lights += ForestAdditionalLights(input.positionWS, N, GetNormalizedScreenSpaceUV(input.positionCS));
            #endif

                half3 color = albedo * lights + direct * spec;
                color = MixFog(color, input.lampFog.a);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------ shadows
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------ depth prepass
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------ depth + normals (SSAO etc.)
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag

            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            NormalsVaryings NormalsVert(NormalsAttributes input)
            {
                NormalsVaryings output = (NormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
                return output;
            }

            half4 NormalsFrag(NormalsVaryings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
