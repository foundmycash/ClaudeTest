// Havoc/Forest Lamp Glow
//
// The soft halo around a streetlight in the fog: a small camera-facing quad, added on top (no lighting, no depth write).
// Cheap on Quest: one quad per lamp, faded out close to the camera (so it never fills the view with overdraw) and into
// the fog with distance (Unity fog, or the volumetric fog through _ForestFogExtinction). Its colour is set per lamp by
// ForestLamp (_GlowColor).
Shader "Havoc/Forest Lamp Glow"
{
    Properties
    {
        [HDR] _GlowColor ("Glow Colour", Color) = (0.6, 0.45, 0.3, 1)
        _Size ("Size (metres)", Float) = 2.4
        _NearFade ("Fade Out Closer Than (metres)", Float) = 2.5
        _Softness ("Falloff", Range(1, 6)) = 2.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+20"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Glow"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _GlowColor;
                float _Size;
                float _NearFade;
                half _Softness;
            CBUFFER_END

            float _ForestFogExtinction;     // set by Forest Atmosphere while the volumetric fog is on (per metre), else 0
            float _ForestFogClearDistance;  // metres of the clear zone around the camera the fog effectively starts after

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
                half2  fade       : TEXCOORD1;     // x = near fade, y = fog factor
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // billboard: the quad's corners laid out along the camera's right and up axes around the object's pivot
                float3 center = float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
                float3 right = float3(UNITY_MATRIX_V._m00, UNITY_MATRIX_V._m01, UNITY_MATRIX_V._m02);
                float3 up = float3(UNITY_MATRIX_V._m10, UNITY_MATRIX_V._m11, UNITY_MATRIX_V._m12);
                float3 positionWS = center + (right * input.positionOS.x + up * input.positionOS.y) * _Size;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = input.uv;
                float dist = distance(center, GetCameraPositionWS());
                // the volumetric fog is drawn before the halos, so they fade into it here (Unity fog: the fog factor)
                half nearFade = saturate((dist - _NearFade * 0.4) / max(0.01, _NearFade)) * exp(-_ForestFogExtinction * max(0.0, dist - _ForestFogClearDistance));
                o.fade = half2(nearFade, ComputeFogFactor(o.positionCS.z));
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half d = saturate(length(input.uv - 0.5h) * 2.0h);
                half glow = pow(1.0h - d, _Softness);
                half3 c = _GlowColor.rgb * glow * input.fade.x;
                c = MixFogColor(c, half3(0.0h, 0.0h, 0.0h), input.fade.y);    // fades to nothing in the fog
                return half4(c, 1.0h);
            }
            ENDHLSL
        }
    }
}
