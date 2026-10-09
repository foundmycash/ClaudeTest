// Explosion fireball for the Loading scene: one sphere mesh, pushed in and out by 3D noise in the vertex shader so the
// silhouette billows, and coloured by temperature in the fragment shader (white-hot where it faces the viewer, cooling
// to orange, red and smoke toward the edges). One draw call, no particles; this is what keeps it cheap.
//
// The sphere grows until the camera is inside it. LoadingScreen.ExplosionEffect then switches _Cull to Front so the
// inside of the ball is drawn, and the full-screen fire overlay takes over from there.
//
// Output is premultiplied alpha:  result = rgb + framebuffer * (1 - a)
Shader "Loading/Fireball"
{
    Properties
    {
        _NoiseTex ("Noise (3D)", 3D) = "" {}
        _Heat ("Heat", Range(0, 1.5)) = 1.2
        _Age ("Age (seconds)", Float) = 0
        _Displacement ("Displacement", Range(0, 1)) = 0.32
        _NoiseScale ("Noise Scale", Float) = 1.3
        _Opacity ("Opacity", Range(0, 1)) = 1
        _SmokeColor ("Smoke Color", Color) = (0.09, 0.07, 0.06, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+50"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Fireball"

            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

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
                float _Heat;
                float _Age;
                float _Displacement;
                float _NoiseScale;
                float _Opacity;
                float4 _SmokeColor;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 sphere : TEXCOORD2;    // undisplaced unit direction, for the detail noise
                float billow : TEXCOORD3;     // low-frequency noise used for the displacement
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 n = normalize(input.positionOS.xyz);
                float3 p = n * _NoiseScale * 0.5 + float3(0.0, -_Age * 0.45, _Age * 0.1);   // billows roll upward
                float4 noise = SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, p, 0);
                float billow = noise.r * 0.7 + noise.g * 0.3;

                float3 positionOS = n * (0.5 + (billow - 0.5) * _Displacement);
                output.positionWS = TransformObjectToWorld(positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(n);
                output.sphere = n;
                output.billow = billow;
                return output;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 viewDir = normalize(GetCameraPositionWS() - input.positionWS);
                // Seen from inside (back faces) the wall of fire faces the viewer everywhere.
                float facing = IS_FRONT_VFACE(face, saturate(dot(normalize(input.normalWS), viewDir)), 1.0);

                float detail = SAMPLE_TEXTURE3D(_NoiseTex, sampler_NoiseTex,
                    input.sphere * _NoiseScale * 1.4 + float3(0.0, -_Age * 1.3, 0.0)).b;
                float n = input.billow * 0.55 + detail * 0.45;

                float heat = _Heat * (0.45 + 0.55 * facing) + (n - 0.5) * 0.85 - (1.0 - facing) * 0.25;
                float flame = saturate(heat * 2.5);                       // 0 = only smoke left, 1 = burning
                float smoke = _SmokeColor.a * saturate(0.35 + n);
                float edge = smoothstep(0.0, 0.35, facing + (n - 0.5) * 0.3);   // soft, ragged silhouette

                float a = lerp(smoke, 1.0, flame) * edge * _Opacity;
                float3 rgb = lerp(_SmokeColor.rgb * smoke, FireRamp(heat), flame) * edge * _Opacity;
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
