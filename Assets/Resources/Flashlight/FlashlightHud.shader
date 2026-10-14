// Havoc/Flashlight HUD
//
// The high-beam gauge: a progress ring around a battery icon, drawn procedurally on one small quad (no textures, no
// canvas). Driven by ForestFlashlight: _Fill (0..1, clockwise from the top), _Color, _Glow (brightness) and _Alpha.
// Lives in Resources so it is always in the build.
Shader "Havoc/Flashlight HUD"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.85, 0.55, 1)
        _Fill ("Fill", Range(0, 1)) = 1
        _Glow ("Brightness", Range(0, 2)) = 1
        _Alpha ("Alpha", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Hud"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Fill;
                half _Glow;
                half _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            // signed distance to a box (centre at the origin, half size b), rounded by r
            float BoxSDF(float2 p, float2 b, float r)
            {
                float2 d = abs(p) - b + r;
                return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0;                         // -1..1, y up
                float aa = max(fwidth(p.x), 0.002) * 1.5;

                // ring: clockwise from the top, a dim track behind it
                float r = length(p);
                float ring = 1.0 - smoothstep(0.0, aa, abs(r - 0.82) - 0.1);
                float t = frac(atan2(p.x, p.y) / 6.2831853 + 1.0);   // 0 at the top, growing clockwise
                float lit = 1.0 - smoothstep(0.0, 0.004, t - _Fill);
                float ringA = ring * lerp(0.16, 1.0, lit);

                // battery: upright, a nub on top, the inside filled from the bottom as the ring fills
                float body = BoxSDF(p - float2(0.0, -0.04), float2(0.2, 0.32), 0.06);
                float nub = BoxSDF(p - float2(0.0, 0.37), float2(0.09, 0.06), 0.03);
                float outline = 1.0 - smoothstep(0.0, aa, abs(body) - 0.028);
                float nubA = 1.0 - smoothstep(0.0, aa, nub);
                float inner = BoxSDF(p - float2(0.0, -0.04), float2(0.2, 0.32), 0.06) + 0.075;
                float level = -0.285 + 0.49 * _Fill;                  // y of the top of the charge
                float charge = (1.0 - smoothstep(0.0, aa, inner)) * (1.0 - smoothstep(0.0, aa, p.y - level));
                float batA = max(max(outline, nubA), charge * 0.85);

                float a = saturate(max(ringA, batA)) * _Alpha;
                half3 c = _Color.rgb * _Glow;
                return half4(c, a);
            }
            ENDHLSL
        }
    }
}
