// Full-screen "waking up" overlay used by WakeUp.WakeUpSequence: a solid colour fade plus blurry, bright vision.
//
// It is drawn with one oversized triangle whose vertices are written straight to clip space, so it fills the whole
// viewport of every eye no matter where the camera or the headset is. Because it is one uniform colour it is identical
// in both eyes, and the usual UNITY_*_STEREO macros are all that is needed for single-pass instanced, multiview and
// multi-pass VR rendering.
//
// Output is premultiplied alpha:  result = rgb + framebuffer * (1 - a)
//   cover = how much of the pixel is replaced by the solid colour (0 = see the world, 1 = solid colour)
//   blur  = how much of the pixel is replaced by the blurred, hazy version of the scene
// The blurred scene is read from _CameraOpaqueTexture, which WakeUpSequence switches on for the camera while the
// intro plays.
Shader "Hidden/WakeUp/EyelidOverlay"
{
    Properties
    {
        _Visibility ("Visibility (0 = solid colour, 1 = clear view)", Range(0, 1)) = 0
        _CoverColor ("Cover Color", Color) = (0, 0, 0, 1)
        _Blur ("Blur Amount", Range(0, 1)) = 0
        _BlurRadius ("Blur Radius At Full Blur (view angle, radians)", Float) = 0.026
        _Taps ("Blur Samples", Float) = 16
        _HazeColor ("Haze Color", Color) = (1, 0.95, 0.88, 1)
        _HazeStrength ("Haze Strength", Range(0, 1)) = 0.2
        _Exposure ("Brightness Boost", Range(0, 2)) = 0.12
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
            Name "EyelidOverlay"

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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Visibility;
                float4 _CoverColor;
                float _Blur;
                float _BlurRadius;
                float _Taps;
                float4 _HazeColor;
                float _HazeStrength;
                float _Exposure;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uvPerAngle : TEXCOORD0;   // screen UV spanned by one radian of view angle (per axis)
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

                // Lets the blur keep the same size in degrees of vision on any headset (FOV differs a lot).
                output.uvPerAngle = 0.5 * abs(float2(UNITY_MATRIX_P._m00, UNITY_MATRIX_P._m11));
                return output;
            }

            float3 SampleScene(float2 uv)
            {
                uv = ClampAndScaleUVForBilinear(UnityStereoTransformScreenSpaceTex(uv), _CameraOpaqueTexture_TexelSize.xy);
                return SAMPLE_TEXTURE2D_X_LOD(_CameraOpaqueTexture, sampler_LinearClamp, uv, 0).rgb;
            }

            // Defocus blur: average of samples spread over a disc (Vogel spiral). The spiral is rotated per pixel with
            // a fixed pattern so the few samples read as soft grain instead of visible ghost copies. The pattern does
            // not change over time, so it cannot flicker in a headset.
            float3 BlurScene(float2 uv, float2 radiusUV, float2 pixel)
            {
                float rotation = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) * 6.2831853;
                int taps = (int)clamp(_Taps, 4.0, 48.0);

                float3 sum = 0;
                [loop]
                for (int i = 0; i < taps; i++)
                {
                    float r = sqrt((i + 0.5) / taps);
                    float a = i * 2.39996323 + rotation;
                    sum += SampleScene(uv + float2(cos(a), sin(a)) * (r * radiusUV));
                }
                return sum / taps;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float cover = 1.0 - saturate(_Visibility);

                // ---- Blurry, bright vision -----------------------------------------------------------------
                float blur = saturate(_Blur);
                float3 vision = 0;
                if (blur > 0.001 && cover < 0.999)
                {
                    float2 uv = GetNormalizedScreenSpaceUV(input.positionCS);
                    float2 radiusUV = _BlurRadius * blur * input.uvPerAngle;

                    vision = BlurScene(uv, radiusUV, input.positionCS.xy);
                    vision = lerp(vision, _HazeColor.rgb, _HazeStrength * blur);   // veil of light
                    vision *= 1.0 + _Exposure * blur;                              // eyes not used to the light yet
                }

                float3 rgb = vision * (blur * (1.0 - cover)) + _CoverColor.rgb * cover;
                float a = 1.0 - (1.0 - blur) * (1.0 - cover);
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
