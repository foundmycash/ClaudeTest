// Full-screen "inside the fireball" overlay used by LoadingScreen.SceneTransition. It hides the switch from the Loading
// scene to the main scene.
//
// It is drawn with one oversized triangle whose vertices are written straight to clip space (the same technique as the
// wake-up overlay), so it covers the whole view of every eye on single-pass instanced, multiview and multi-pass VR.
// The flames are computed from each pixel's view direction, turned into the XR rig's tracking space (_SpaceRotation):
//   * both eyes see the same flames "at infinity", so there is no depth conflict between the eyes,
//   * the flames stay put when the head turns instead of swimming with it,
//   * the pattern does not jump when the transition hands over from the Loading camera to the main scene's camera,
//     because the head pose in tracking space is the same for both rigs.
//
// Output is premultiplied alpha:  result = rgb + framebuffer * (1 - a)
//   _Cover   flames creep in from the edge of the view toward the centre (0 = none, 1 = view filled)
//   _Reveal  the flames burn away from the centre outward with glowing edges (0 = filled, 1 = gone)
//   _Heat    temperature of the flames (see FireRamp)
//   _Flash   additive white flash, _Glow additive orange wash (both also work while nothing is covered)
Shader "Hidden/Loading/FireOverlay"
{
    Properties
    {
        _NoiseTex ("Noise (3D)", 3D) = "" {}
        _Cover ("Cover", Range(0, 1)) = 0
        _Reveal ("Reveal", Range(0, 1)) = 0
        _Heat ("Heat", Range(0, 1.5)) = 1
        _Flash ("Flash", Range(0, 4)) = 0
        _Glow ("Glow", Range(0, 2)) = 0
        _FireTime ("Fire Time", Float) = 0
        _NoiseScale ("Noise Scale", Float) = 1.6
        _EdgeWidth ("Burn Edge Width", Range(0.02, 0.5)) = 0.14
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "FireOverlay"

            Blend One OneMinusSrcAlpha
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/Loading/FireCommon.hlsl"

            TEXTURE3D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);

            CBUFFER_START(UnityPerMaterial)
                float _Cover;
                float _Reveal;
                float _Heat;
                float _Flash;
                float _Glow;
                float _FireTime;
                float _NoiseScale;
                float _EdgeWidth;
                float4x4 _SpaceRotation;   // world -> rig tracking space, rotation only
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionWS : TEXCOORD0;   // camera -> near plane, not normalized
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // The mesh vertices already are clip-space coordinates (-1..3 for the oversized triangle).
                output.positionCS = float4(input.positionOS.xy, UNITY_NEAR_CLIP_VALUE, 1.0);

                // Point on the near plane seen through this vertex, per eye. Points on a plane are an affine function
                // of the screen position, so interpolating them across the triangle is exact.
                float4 nearWS = mul(UNITY_MATRIX_I_VP, output.positionCS);
                output.directionWS = nearWS.xyz / nearWS.w - GetCameraPositionWS();
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 directionWS = normalize(input.directionWS);
                float3 direction = mul((float3x3)_SpaceRotation, directionWS);
                // 1 where the player looks, 0 about 60 degrees out (the edge of a headset's view).
                float centre = saturate((dot(directionWS, GetViewForwardDir()) - 0.45) / 0.55);

                // Billowing flames: a slow, large layer warps a faster rising one.
                float t = _FireTime;
                float3 p = direction * _NoiseScale;
                float4 slow = SAMPLE_TEXTURE3D(_NoiseTex, sampler_NoiseTex, p * 0.35 + float3(0.0, -t * 0.12, t * 0.03));
                float3 warp = (slow.rgb - 0.5) * 0.7;
                float4 fast = SAMPLE_TEXTURE3D(_NoiseTex, sampler_NoiseTex, p * 0.9 + warp + float3(t * 0.05, -t * 0.38, 0.0));
                float fire = saturate(slow.a * 0.5 + fast.r * 0.5);

                float3 rgb = FireRamp(_Heat + (fire - 0.5) * 1.1);

                // Approach: flames wrap in from the edge of the view first, hot spots a little ahead of the rest.
                float coverField = (1.0 - centre) * 0.6 + fire * 0.4;
                float cover = saturate((coverField - (1.0 - _Cover * 1.35)) * 4.0);

                // Reveal: holes open where the player looks and in the cooler noise, edged by a glowing rim. The two
                // terms spread the pixels over the whole 0..1 range so the fire burns away gradually.
                float w = _EdgeWidth;
                float revealField = centre * 0.6 + (1.0 - fire) * 0.4;
                float threshold = (1.0 + w) - _Reveal * (1.0 + 2.0 * w);
                float burnt = revealField - threshold;   // > 0: burnt through
                float revealed = smoothstep(0.0, w * 0.35, burnt);
                float rim = saturate(1.0 - abs(burnt + w * 0.5) / (w * 0.5)) * (1.0 - revealed);

                float a = cover * (1.0 - revealed);
                float3 result = rgb * a;
                result += FireRamp(0.72) * (rim * rim * 1.5 * cover);
                result += half3(1.0, 0.9, 0.7) * _Flash + half3(1.0, 0.2, 0.03) * _Glow;   // linear colours
                return half4(result, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
