// Text shader for the loading screen (legacy UI Text). Turns plain font glyphs into lettering cut into the stone:
//   * the glyph outline is roughened with noise, so the edges look chiselled rather than vector-perfect,
//   * the letters are grooves: walls shaded by the key light, the floor holds a glowing inlay in the vertex colour
//     with a warmer edge,
//   * a thin dark lip surrounds each letter, like chipped stone around a cut,
//   * _Dissolve crumbles the letters to dust along a noise front, with a faint ember at the eroding edge.
// Same blending and stencil set-up as UI/Default, so it works anywhere a UI Text does.
Shader "Loading/StoneText"
{
    Properties
    {
        [PerRendererData] _MainTex ("Font Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        [NoScaleOffset] _NoiseTex ("Noise (3D)", 3D) = "" {}

        _Depth ("Groove depth", Range(0, 3)) = 1.8
        _Glow ("Inlay glow", Range(0, 1.5)) = 0.9
        _Roughness ("Edge roughness", Range(0, 0.6)) = 0.25
        _Blur ("Edge softness (atlas texels)", Range(0.5, 4)) = 2.0
        _Inset ("Inlay inset", Range(0, 0.4)) = 0.27
        _Underlight ("Light from below (the molten channel)", Range(0, 1)) = 0
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _AnimTime ("Animation time", Float) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+110"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "LoadingStoneText"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/Loading/FireCommon.hlsl"

            TEXTURE2D(_MainTex);  SAMPLER(sampler_MainTex);
            TEXTURE3D(_NoiseTex); SAMPLER(sampler_NoiseTex);

            float4 _TextureSampleAdd;   // (1,1,1,0) for alpha-only font textures

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float _Depth;
                float _Glow;
                float _Roughness;
                float _Blur;
                float _Inset;
                float _Underlight;
                float _Dissolve;
                float _AnimTime;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 canvasPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.canvasPos = input.positionOS.xy;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color * _Color;
                return output;
            }

            float Noise3(float3 p) { return SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, p, 0).r; }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                uint tw, th;
                _MainTex.GetDimensions(tw, th);
                float2 texel = 1.0 / float2(tw, th);
                float r = _Blur;

                #define GLYPH(ox, oy) SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + float2(ox, oy) * r * texel).a
                float a0 = GLYPH(0, 0);
                float aL = GLYPH(-1, 0), aR = GLYPH(1, 0), aD = GLYPH(0, -1), aU = GLYPH(0, 1);
                float dA = GLYPH(-0.7, -0.7), dB = GLYPH(0.7, -0.7), dC = GLYPH(-0.7, 0.7), dD = GLYPH(0.7, 0.7);
                float soft = (a0 * 2.0 + aL + aR + aD + aU + dA + dB + dC + dD) / 10.0;
                float2 grad = float2(aR - aL, aU - aD) + 0.7 * float2(dB + dD - dA - dC, dC + dD - dA - dB);   // points into the letter

                // chiselled edge: the threshold wanders with noise
                float wobble = Noise3(float3(input.canvasPos * 0.30, 0.17)) - 0.5;
                float wobble2 = Noise3(float3(input.canvasPos * 0.85, 0.61)) - 0.5;
                float thresh = 0.46 + (wobble * 0.8 + wobble2 * 0.5) * _Roughness * 1.6;
                float inside = smoothstep(thresh - 0.10, thresh + 0.10, soft);

                // dissolve
                float keep = 1.0, rim = 0.0;
                if (_Dissolve > 0.0)
                {
                    float dn = Noise3(float3(input.canvasPos * 0.10, 1.7)) * 0.7 + wobble2 * 0.3 + 0.15;
                    float edge = _Dissolve * 1.25 - 0.10;
                    keep = smoothstep(edge, edge + 0.07, dn);
                    rim = (smoothstep(edge - 0.02, edge + 0.02, dn) - smoothstep(edge + 0.05, edge + 0.16, dn));
                }

                // groove walls
                const float3 keyDir = normalize(float3(-0.50, 0.62, 0.62));
                float3 n = normalize(float3(grad * _Depth * 0.55, 1.0));
                float shade = 1.0 + (dot(n, keyDir) - keyDir.z) * 1.15;

                // inlay: a glowing stone set into the groove, inset from the cut so a dark chiselled wall shows around it;
                // hotter in the middle of strokes, warmer towards the edges
                float inlay = smoothstep(thresh + _Inset - 0.07, thresh + _Inset + 0.07, soft);
                float core = smoothstep(0.62, 0.98, soft);
                float mineral = 0.80 + 0.40 * Noise3(float3(input.canvasPos * 0.11, 2.3));
                float3 glow = input.color.rgb * (0.74 + 0.28 * core) * mineral;
                float3 warm = FireRamp(0.55) * 0.9;
                glow = lerp(glow, warm, (1.0 - core) * 0.45);
                float lowerEdge = saturate(grad.y * 0.9 + 0.1);
                glow += FireRamp(0.64) * _Underlight * (0.10 + 0.85 * lowerEdge * (1.0 - 0.35 * core));
                glow *= _Glow * (1.0 - 0.45 * _Dissolve);

                float lit = saturate(dot(n, keyDir));
                float3 wall = float3(0.050, 0.040, 0.034) * (0.30 + 3.4 * lit * lit) + FireRamp(0.28) * 0.05;
                float3 fill = lerp(wall, glow * shade, inlay);

                // dark lip around the cut
                float lip = saturate(soft * 1.9 - 0.12) * (1.0 - inside) * 0.6;

                float3 rgb = fill * inside * keep + FireRamp(0.30) * rim * inside * 0.5;
                float a = inside * keep + lip * keep;
                return half4(rgb, a) * input.color.a;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
