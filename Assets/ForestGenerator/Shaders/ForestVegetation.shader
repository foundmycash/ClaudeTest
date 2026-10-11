// Havoc/Forest Vegetation
//
// A cheap lit shader for everything that stands in the forest: trees, bushes, rocks, logs, props. Built for Quest:
//   - colour from the vertex colours (the placeholder models have no textures) times an optional colour texture
//   - each copy gets a slightly different shade from its position (no per-instance data needed), so a forest of the same
//     few models does not look copy-pasted
//   - cheap wind: the vertex colour alpha says how much a vertex sways (0 = trunk, 1 = needle tips)
//   - the forest's ground light map darkens the lower parts of things standing under the canopy, and the streetlights'
//     lamp light reaches them (both per vertex, so you see the green glow on nearby trunks)
//   - optional alpha clipping for leaf cards (costs more on Quest: use opaque geometry where you can)
// GPU instancing, the SRP Batcher, single-pass instanced stereo, Forward and Forward+ and built-in fog are supported.
// Your own models can use it too: give them a colour texture (and white vertex colours, or none) and set Wind to 0 for
// things that should not move.
Shader "Havoc/Forest Vegetation"
{
    Properties
    {
        [Header(Colour)]
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        _BaseMap ("Colour Texture (optional)", 2D) = "white" {}
        [Toggle] _UseVertexColor ("Use Vertex Colours", Float) = 1
        _ColorVariation ("Variation Between Copies", Range(0, 0.5)) = 0.12
        _HueVariation ("Autumn Shift Between Copies", Range(0, 1)) = 0.15

        [Header(Wind)]
        _WindStrength ("Wind Strength (metres)", Range(0, 0.5)) = 0.06
        _WindSpeed ("Wind Speed", Range(0, 4)) = 0.8

        [Header(Lighting)]
        _Wrap ("Soft Lighting (light wraps round)", Range(0, 1)) = 0.35
        _CanopyDarkening ("Darkening Under The Canopy", Range(0, 1)) = 0.8
        _CanopyHeight ("Canopy Height (metres)", Float) = 14
        _LampStrength ("Lamp Light Strength", Range(0, 4)) = 1
        _Smoothness ("Smoothness", Range(0, 1)) = 0.1

        [Header(Emission)]
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 0)

        [Header(Rendering)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip (leaf cards; costs more on Quest)", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Render Faces", Float) = 2
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
        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _UseVertexColor;
            half _ColorVariation;
            half _HueVariation;
            half _WindStrength;
            half _WindSpeed;
            half _Wrap;
            half _CanopyDarkening;
            float _CanopyHeight;
            half _LampStrength;
            half _Smoothness;
            half4 _EmissionColor;
            half _AlphaClip;
            half _Cutoff;
            half _Cull;
        CBUFFER_END

        float3 ObjectPivotWS()
        {
            return float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
        }

        // Sway: a slow lean plus a faster flutter, both phased by where the object stands, scaled by the vertex weight.
        float3 Wind(float3 positionWS, half weight)
        {
            if (_WindStrength <= 0.0h || weight <= 0.0h) return positionWS;
            float3 pivot = ObjectPivotWS();
            float phase = dot(pivot.xz, float2(0.13, 0.17));
            float t = _Time.y * _WindSpeed;
            float lean = sin(t * 0.9 + phase) * 0.7 + sin(t * 2.1 + phase * 1.7) * 0.3;
            float flutter = sin(t * 6.3 + dot(positionWS, float3(1.7, 1.3, 2.1))) * 0.25;
            float3 dir = float3(0.8, 0.0, 0.6);
            return positionWS + dir * ((lean + flutter) * _WindStrength * weight);
        }
        ENDHLSL

        // ------------------------------------------------------------------ main lit pass
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "ForestCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half4  color      : TEXCOORD3;      // rgb = albedo tint (vertex colour x copy variation), a = emission mask
                half4  lightFog   : TEXCOORD4;      // rgb = ambient + lamps + per-vertex lights, a = fog
                half   canopy     : TEXCOORD5;      // how much the canopy shades the sun here
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD6;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                half4 vc = _UseVertexColor > 0.5h ? input.color : half4(1.0h, 1.0h, 1.0h, input.color.a);
                float3 positionWS = Wind(TransformObjectToWorld(input.positionOS.xyz), input.color.a);
                half3 n = (half3)TransformObjectToWorldNormal(input.normalOS);
                o.positionWS = positionWS;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = n;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                // ---- a different shade for every copy, from where it stands
                float3 pivot = ObjectPivotWS();
                float h = frac(sin(dot(pivot.xz, float2(12.9898, 78.233))) * 43758.5453);
                float h2 = frac(h * 7.13 + 0.37);
                half3 tint = vc.rgb * (1.0h + (half)(h - 0.5) * 2.0h * _ColorVariation);
                half3 autumn = tint * half3(1.18h, 0.92h, 0.7h);
                tint = lerp(tint, autumn, (half)(h2 * h2) * _HueVariation);
                o.color = half4(tint, input.color.a);

                // ---- the forest's ground light map: sky light and sunlight under the canopy
                float2 mapUV = ForestMapUV(pivot);
                half2 baked = SAMPLE_TEXTURE2D_LOD(_ForestLightMap, sampler_ForestLightMap, mapUV, 0).rg;
                half up = saturate((half)((positionWS.y - pivot.y) / max(_CanopyHeight, 0.5)));     // the top of a tree is in the open
                half sky = lerp(lerp(1.0h, baked.r, _CanopyDarkening), 1.0h, up);
                o.canopy = lerp(lerp(1.0h, baked.g, _CanopyDarkening), 1.0h, up * up);

                half3 light = SampleSH(n) * sky + ForestLampLight(positionWS, n) * _LampStrength;
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                light += VertexLighting(positionWS, n);
            #endif
                o.lightFog = half4(light, ComputeFogFactor(o.positionCS.z));

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                VertexPositionInputs vpi = (VertexPositionInputs)0;
                vpi.positionWS = positionWS;
                vpi.positionCS = o.positionCS;
                o.shadowCoord = GetShadowCoord(vpi);
            #endif
                return o;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
            #if defined(_ALPHATEST_ON)
                clip(tex.a * _BaseColor.a - _Cutoff);
            #endif
                half3 albedo = tex.rgb * _BaseColor.rgb * input.color.rgb;
                half3 N = normalize(input.normalWS) * IS_FRONT_VFACE(face, 1.0h, -1.0h);

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord = input.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #else
                float4 shadowCoord = float4(0, 0, 0, 0);
            #endif
                Light mainLight = GetMainLight(shadowCoord);
                half ndl = saturate((dot(N, mainLight.direction) + _Wrap) / (1.0h + _Wrap));
                half3 direct = mainLight.color * (ndl * mainLight.shadowAttenuation * mainLight.distanceAttenuation * input.canopy);

                half3 lights = direct + input.lightFog.rgb;
            #if defined(_ADDITIONAL_LIGHTS)
                lights += ForestAdditionalLights(input.positionWS, N, GetNormalizedScreenSpaceUV(input.positionCS));
            #endif
                half3 color = albedo * lights;

                if (_Smoothness > 0.01h)
                {
                    half3 V = (half3)GetWorldSpaceNormalizeViewDir(input.positionWS);
                    half3 H = normalize(mainLight.direction + V);
                    color += direct * pow(saturate(dot(N, H)), exp2(_Smoothness * 9.0h + 1.0h)) * _Smoothness * 0.25h;
                }

                color += _EmissionColor.rgb;

                color = MixFog(color, input.lightFog.a);
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings o = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                float3 positionWS = Wind(TransformObjectToWorld(input.positionOS.xyz), input.color.a);
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
                o.positionCS = positionCS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
            #if defined(_ALPHATEST_ON)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
            #endif
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings o = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformWorldToHClip(Wind(TransformObjectToWorld(input.positionOS.xyz), input.color.a));
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 DepthFrag(DepthVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #if defined(_ALPHATEST_ON)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
            #endif
                return 0;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------ depth + normals
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON

            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            NormalsVaryings NormalsVert(NormalsAttributes input)
            {
                NormalsVaryings o = (NormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformWorldToHClip(Wind(TransformObjectToWorld(input.positionOS.xyz), input.color.a));
                o.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 NormalsFrag(NormalsVaryings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #if defined(_ALPHATEST_ON)
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
            #endif
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
