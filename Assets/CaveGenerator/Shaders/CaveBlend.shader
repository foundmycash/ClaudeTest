// Havoc/Cave Blend
//
// One material, two textures: rock on the walls, dirt on the floor path, faded smoothly into each other.
//
// The cave mesh has no UVs, so both textures are projected triplanar from world position.
// Where the dirt shows is decided from two things that are both smooth across the surface:
//   1. the vertex colour red channel, written by the cave generator: 1 along the dirt path, fading to 0 away from it
//   2. how much the surface faces up (per pixel), so dirt never climbs the walls
// The two are multiplied, eased with a smoothstep, and broken up a little by the dirt texture itself.
// The vertex colour green channel carries baked ambient occlusion.
//
// Written for URP 12 (Unity 2021.3).
Shader "Havoc/Cave Blend"
{
    Properties
    {
        [Header(Rock)]
        [NoScaleOffset] _RockMap ("Rock Albedo", 2D) = "gray" {}
        [NoScaleOffset][Normal] _RockNormalMap ("Rock Normal (optional)", 2D) = "bump" {}
        _RockColor ("Rock Tint", Color) = (1, 1, 1, 1)
        _RockTiling ("Rock Tiles Per Metre", Float) = 0.2
        _RockNormalStrength ("Rock Normal Strength", Range(0, 2)) = 0.6
        _RockSmoothness ("Rock Smoothness", Range(0, 1)) = 0.2

        [Header(Dirt)]
        [NoScaleOffset] _DirtMap ("Dirt Albedo", 2D) = "gray" {}
        [NoScaleOffset][Normal] _DirtNormalMap ("Dirt Normal (optional)", 2D) = "bump" {}
        _DirtColor ("Dirt Tint", Color) = (1, 1, 1, 1)
        _DirtTiling ("Dirt Tiles Per Metre", Float) = 0.3
        _DirtNormalStrength ("Dirt Normal Strength", Range(0, 2)) = 0.6
        _DirtSmoothness ("Dirt Smoothness", Range(0, 1)) = 0.05

        [Header(Blending)]
        _FloorStart ("Floor Fade Start (surface up-ness)", Range(0, 1)) = 0.3
        _FloorEnd ("Floor Fade End (surface up-ness)", Range(0, 1)) = 0.8
        _BlendBreakup ("Blend Breakup", Range(0, 1)) = 0.35
        _TriplanarSharpness ("Triplanar Sharpness", Range(1, 16)) = 10
        _AOStrength ("Baked AO Strength", Range(0, 1)) = 0.9
        [Toggle(_NORMALMAP)] _UseNormalMaps ("Use Normal Maps", Float) = 1

        [Header(Baked Lamp Light)]
        _LampColor ("Baked Lamp Colour", Color) = (1, 0.94, 0.84, 1)
        _LampStrength ("Baked Lamp Strength", Range(0, 3)) = 1

        [Header(Baked Lightmap)]
        _CaveLightmapStrength ("Baked Lightmap Strength", Range(0, 4)) = 1
        [HideInInspector] _CaveLightmapMode ("Lightmap Mode (set by the cave generator)", Float) = 0
        [HideInInspector][NoScaleOffset] _CaveLightmap ("Cave Lightmap (set by the cave generator)", 2D) = "black" {}
        [HideInInspector][NoScaleOffset] _CaveLightmapDir ("Cave Lightmap Direction (set by the cave generator)", 2D) = "black" {}

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Render Faces (Off = both sides)", Float) = 0

        [Header(Underground Lighting)]
        _SunStrength ("Sun / Main Light Strength", Range(0, 1)) = 0
        _SkyLightStrength ("Sky Ambient Strength", Range(0, 1)) = 0.4
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

        TEXTURE2D(_RockMap);        SAMPLER(sampler_RockMap);
        TEXTURE2D(_RockNormalMap);  SAMPLER(sampler_RockNormalMap);
        TEXTURE2D(_DirtMap);        SAMPLER(sampler_DirtMap);
        TEXTURE2D(_DirtNormalMap);  SAMPLER(sampler_DirtNormalMap);
        TEXTURE2D(_CaveLightmap);     SAMPLER(sampler_CaveLightmap);
        TEXTURE2D(_CaveLightmapDir);  SAMPLER(sampler_CaveLightmapDir);

        // Every pass must see the same material constants for the SRP Batcher.
        CBUFFER_START(UnityPerMaterial)
            half4 _RockColor;
            half4 _DirtColor;
            half _RockTiling;
            half _DirtTiling;
            half _RockNormalStrength;
            half _DirtNormalStrength;
            half _RockSmoothness;
            half _DirtSmoothness;
            half _FloorStart;
            half _FloorEnd;
            half _BlendBreakup;
            half _TriplanarSharpness;
            half _AOStrength;
            half _UseNormalMaps;
            half _SunStrength;
            half _SkyLightStrength;
            half _Cull;
            half4 _LampColor;
            half _LampStrength;
            half _CaveLightmapMode;         // 0 = none (use the vertex baked lamp light), 1 = lightmap, 2 = lightmap with light direction
            half _CaveLightmapStrength;
        CBUFFER_END
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

            #pragma shader_feature_local_fragment _NORMALMAP

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            // Cheaper surface for the Quest 2 (QuestOptimization.CaveDetail turns this keyword on globally): two texture projections
            // instead of three and no normal maps, which is 4 texture reads per pixel instead of 6 (12 with normal maps).
            #pragma multi_compile_fragment _ _CAVE_LITE

            // A cave is underground: by default it ignores the sun and most of the sky's ambient light, so it is lit
            // by its own lamps. (The sun would otherwise leak in through the one-sided ceiling mesh.)
            // Scaling _MainLightColor here is seen by URP's lighting code, which is included right after.
            #define _MainLightColor (_MainLightColor * _SunStrength)
            #define _ENVIRONMENTREFLECTIONS_OFF

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                half4  color      : COLOR;
                float2 lightmapUV : TEXCOORD1;   // the cave lightmapper's UV2
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half3  maskAO     : TEXCOORD2;   // x = dirt path mask, y = baked ambient occlusion, z = baked lamp light
                float2 bakeUV     : TEXCOORD6;   // lightmap UV
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                half4  fogFactorAndVertexLight : TEXCOORD3;
            #else
                half   fogFactor  : TEXCOORD3;
            #endif
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD4;
            #endif
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.maskAO = half3(input.color.r, input.color.g, input.color.b);
                output.bakeUV = input.lightmapUV;

                half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
                half fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
            #else
                output.fogFactor = fogFactor;
            #endif

                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(vertexInput);
            #endif
                return output;
            }

            half3 TriplanarWeights(half3 n)
            {
                half3 w = pow(abs(n), _TriplanarSharpness);
                return w / (w.x + w.y + w.z + 1e-5h);
            }

            half4 TriplanarSample(TEXTURE2D_PARAM(tex, samp), float3 p, half3 w)
            {
                half4 x = SAMPLE_TEXTURE2D(tex, samp, p.zy);
                half4 y = SAMPLE_TEXTURE2D(tex, samp, p.xz);
                half4 z = SAMPLE_TEXTURE2D(tex, samp, p.xy);
                return x * w.x + y * w.y + z * w.z;
            }

        #if defined(_CAVE_LITE)
            // The same as TriplanarSample with the weakest of the three projections left out (the other two are blended again to
            // add up to 1). Which projection that is can change from one pixel to the next, so the texture gradients are given
            // explicitly: otherwise the mip level would jump where two neighbouring pixels pick different projections.
            half4 BiplanarSample(TEXTURE2D_PARAM(tex, samp), float3 p, float3 dpdx, float3 dpdy, half3 w, bool dropX, bool dropY)
            {
                float2 uvA, uvB, dxA, dxB, dyA, dyB;
                half wA, wB;
                if (dropX)
                {
                    uvA = p.xz; dxA = dpdx.xz; dyA = dpdy.xz; wA = w.y;
                    uvB = p.xy; dxB = dpdx.xy; dyB = dpdy.xy; wB = w.z;
                }
                else if (dropY)
                {
                    uvA = p.zy; dxA = dpdx.zy; dyA = dpdy.zy; wA = w.x;
                    uvB = p.xy; dxB = dpdx.xy; dyB = dpdy.xy; wB = w.z;
                }
                else
                {
                    uvA = p.zy; dxA = dpdx.zy; dyA = dpdy.zy; wA = w.x;
                    uvB = p.xz; dxB = dpdx.xz; dyB = dpdy.xz; wB = w.y;
                }
                half4 a = SAMPLE_TEXTURE2D_GRAD(tex, samp, uvA, dxA, dyA);
                half4 b = SAMPLE_TEXTURE2D_GRAD(tex, samp, uvB, dxB, dyB);
                return (a * wA + b * wB) / (wA + wB);
            }
        #endif

            half4 Frag(Varyings input, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float3 posWS = input.positionWS;
                half3 normalWS = normalize(input.normalWS);
                normalWS *= IS_FRONT_VFACE(isFrontFace, 1.0h, -1.0h);      // the far side of the one-sided cave mesh: light it as the outside of the rock

                // Which way is this bit of surface really facing? The smooth vertex normal can disagree with the actual
                // triangle, a lot on coarse meshes, and the texture then gets projected along the wrong axis and smears.
                // The true facet normal comes from the screen-space slope of the world position. Where the two agree
                // we keep the smooth normal (no visible facets on fine meshes); where they disagree we lean on the facet.
                float3 posDx = ddx(posWS);
                float3 posDy = ddy(posWS);
                float3 facetN = cross(posDy, posDx);
                float facetLen = length(facetN);
                facetN = facetLen > 1e-12 ? facetN / facetLen : (float3)normalWS;
                facetN = dot(facetN, (float3)normalWS) < 0.0 ? -facetN : facetN;
                half disagree = saturate((1.0h - dot(normalWS, (half3)facetN)) * 4.0h);
                half3 projN = normalize(lerp(normalWS, (half3)facetN, disagree));
                half3 w = TriplanarWeights(projN);

                float3 rockP = posWS * _RockTiling;
                float3 dirtP = posWS * _DirtTiling;
            #if defined(_CAVE_LITE)
                bool dropX = w.x <= w.y && w.x <= w.z;
                bool dropY = !dropX && w.y <= w.z;
                half4 rockSample = BiplanarSample(TEXTURE2D_ARGS(_RockMap, sampler_RockMap), rockP, posDx * _RockTiling, posDy * _RockTiling, w, dropX, dropY);
                half4 dirtSample = BiplanarSample(TEXTURE2D_ARGS(_DirtMap, sampler_DirtMap), dirtP, posDx * _DirtTiling, posDy * _DirtTiling, w, dropX, dropY);
            #else
                half4 rockSample = TriplanarSample(TEXTURE2D_ARGS(_RockMap, sampler_RockMap), rockP, w);
                half4 dirtSample = TriplanarSample(TEXTURE2D_ARGS(_DirtMap, sampler_DirtMap), dirtP, w);
            #endif

                // ---- how much dirt? path mask (per vertex, smooth) x floor-ness (per pixel, smooth)
                half floorness = smoothstep(_FloorStart, max(_FloorEnd, _FloorStart + 0.01h), normalWS.y);
                half mask = saturate(input.maskAO.x) * floorness;

                // Let the dirt texture's own brightness nibble at the fade so it isn't a perfectly even gradient.
                half lum = dot(dirtSample.rgb, half3(0.299h, 0.587h, 0.114h));
                half bell = 1.0h - abs(mask * 2.0h - 1.0h);          // only active inside the fade zone
                mask = saturate(mask + (lum - 0.5h) * _BlendBreakup * bell * 2.0h);
                mask = mask * mask * (3.0h - 2.0h * mask);           // ease in and out

            #if defined(_NORMALMAP) && !defined(_CAVE_LITE)
                // Blend the two tangent-space normals per projection, then do the world-space triplanar blend once.
                half3 nX = lerp(UnpackNormalScale(SAMPLE_TEXTURE2D(_RockNormalMap, sampler_RockNormalMap, rockP.zy), _RockNormalStrength),
                                UnpackNormalScale(SAMPLE_TEXTURE2D(_DirtNormalMap, sampler_DirtNormalMap, dirtP.zy), _DirtNormalStrength), mask);
                half3 nY = lerp(UnpackNormalScale(SAMPLE_TEXTURE2D(_RockNormalMap, sampler_RockNormalMap, rockP.xz), _RockNormalStrength),
                                UnpackNormalScale(SAMPLE_TEXTURE2D(_DirtNormalMap, sampler_DirtNormalMap, dirtP.xz), _DirtNormalStrength), mask);
                half3 nZ = lerp(UnpackNormalScale(SAMPLE_TEXTURE2D(_RockNormalMap, sampler_RockNormalMap, rockP.xy), _RockNormalStrength),
                                UnpackNormalScale(SAMPLE_TEXTURE2D(_DirtNormalMap, sampler_DirtNormalMap, dirtP.xy), _DirtNormalStrength), mask);

                // Whiteout blend (Ben Golus): swizzle the surface normal into each projection's tangent space.
                nX = half3(nX.xy + normalWS.zy, abs(nX.z) * normalWS.x);
                nY = half3(nY.xy + normalWS.xz, abs(nY.z) * normalWS.y);
                nZ = half3(nZ.xy + normalWS.xy, abs(nZ.z) * normalWS.z);
                normalWS = normalize(nX.zyx * w.x + nY.xzy * w.y + nZ.xyz * w.z);
            #endif

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = lerp(rockSample.rgb * _RockColor.rgb, dirtSample.rgb * _DirtColor.rgb, mask);
                surfaceData.metallic = 0.0h;
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = lerp(_RockSmoothness, _DirtSmoothness, mask);
                surfaceData.occlusion = lerp(1.0h, input.maskAO.y, _AOStrength);
                surfaceData.alpha = 1.0h;
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);

                InputData inputData = (InputData)0;
                inputData.positionWS = posWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(posWS);
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(posWS);
            #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
            #endif
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                inputData.fogCoord = InitializeInputDataFog(float4(posWS, 1.0), input.fogFactorAndVertexLight.x);
                inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
            #else
                inputData.fogCoord = InitializeInputDataFog(float4(posWS, 1.0), input.fogFactor);
            #endif
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, normalWS) * _SkyLightStrength;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                if (_CaveLightmapMode > 0.5h)
                {
                    // The lightmap the cave generator baked (lamps, bounced light, ambient occlusion all included, so the vertex AO is not applied again).
                    half3 baked = SAMPLE_TEXTURE2D(_CaveLightmap, sampler_CaveLightmap, input.bakeUV).rgb;
                #if !defined(_CAVE_LITE)    // (the light direction only matters to the normal maps, which the lite surface has none of)
                    if (_CaveLightmapMode > 1.5h)
                    {
                        // The lightmap holds the light as it falls on the smooth surface. The direction map says where it mostly
                        // comes from, which lets the normal maps catch it: scale by how much the real normal faces that way
                        // compared with how much the smooth surface normal did.
                        half4 dirSample = SAMPLE_TEXTURE2D(_CaveLightmapDir, sampler_CaveLightmapDir, input.bakeUV);
                        half3 lightDirWS = TransformObjectToWorldDir(dirSample.xyz * 2.0h - 1.0h);
                        half halfLambert = dot(normalWS, lightDirWS) * 0.5h + 0.5h;
                        baked *= halfLambert / max(dirSample.w, 0.1h);
                    }
                #endif
                    color.rgb += surfaceData.albedo * baked * _CaveLightmapStrength;
                }
                else
                {
                    // Light from the lamps, baked into the vertices when the cave was generated (stored in quarters).
                    color.rgb += surfaceData.albedo * _LampColor.rgb * (input.maskAO.z * 4.0h * _LampStrength) * surfaceData.occlusion;
                }
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1.0h;
                return color;
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            // URP's own Lit shader gets this indirectly through LitInput.hlsl; Shadows.hlsl needs it (LerpWhiteTo).
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;
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
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_TARGET
            {
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag

            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD1;
            };

            NormalsVaryings NormalsVert(NormalsAttributes input)
            {
                NormalsVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
                return output;
            }

            half4 NormalsFrag(NormalsVaryings input) : SV_TARGET
            {
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
}
