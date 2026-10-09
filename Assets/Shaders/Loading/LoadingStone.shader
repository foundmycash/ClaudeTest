// Shader for the loading screen's carved stone slab (a UI graphic made of shards, see StoneSlabGraphic).
//
// Everything about the slab's look comes from three baked textures plus a little maths, all addressed in "rest space":
// the canvas position the vertex had before any shard moved (uv0, in canvas units, origin at the slab's centre).
//   _RockTex  rgb = albedo, a = height of the rock's relief
//   _SdfTex   r = signed distance to the slab's outline, g = to the recessed plaque, b = to the carved lines (canvas units)
// The slab is lit with a key light and by the glowing channel itself: the molten fill lights the rock around it, and
// the rock's relief catches that light.
//
// The progress bar is a channel cut into the plaque (_BarRect) that the molten fill (_Progress) runs along.
//
// While the slab breaks apart each shard carries (per vertex):
//   uv1.x  distance to the shard's border (canvas units)     uv1.y  random number of the shard
//   uv1.z  dissolve: 0 solid .. 1 gone to dust               uv1.w  damage: 0 intact .. 1 cracks fully open and glowing
//   uv2.xy direction from the border towards the inside of the shard    uv2.z  1 once the shard has broken loose
//   uv3.xy cos / sin of the spin of the shard (so lighting stays correct as it tumbles)
// Cracks open along shard borders (dark, with ember light inside); once loose, the border becomes a fresh,
// lighter broken face; then the shard erodes into dust along the grain of the rock.
Shader "Loading/Stone"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [NoScaleOffset] _RockTex ("Rock (rgb albedo, a height)", 2D) = "gray" {}
        [NoScaleOffset] _SdfTex ("Slab outline / plaque / carving (SDF)", 2D) = "black" {}
        [NoScaleOffset] _NoiseTex ("Noise (3D)", 3D) = "" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)

        _SlabSize ("Slab size (canvas units)", Vector) = (1240, 440, 0, 0)
        _BarRect ("Bar centre (xy), half size (zw)", Vector) = (0, -44, 480, 15)
        _Progress ("Progress", Range(0, 1)) = 0
        _AnimTime ("Animation time", Float) = 0
        _Shadow ("Drop shadow", Range(0, 1)) = 1
        _EmberLight ("Ember light on the rock", Range(0, 2)) = 1
        _Exposure ("Exposure", Range(0.2, 2.5)) = 1

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
            "Queue" = "Transparent+100"
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
            Name "LoadingStone"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/Loading/FireCommon.hlsl"

            TEXTURE2D(_RockTex);  SAMPLER(sampler_RockTex);
            TEXTURE2D(_SdfTex);   SAMPLER(sampler_SdfTex);
            TEXTURE3D(_NoiseTex); SAMPLER(sampler_NoiseTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _SlabSize;
                float4 _BarRect;
                float _Progress;
                float _AnimTime;
                float _Shadow;
                float _EmberLight;
                float _Exposure;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 uv2 : TEXCOORD2;
                float4 uv3 : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 rest : TEXCOORD0;
                float4 shard : TEXCOORD1;
                float4 face : TEXCOORD2;
                float2 spin : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color * _Color;
                output.rest = input.uv0.xy;
                output.shard = input.uv1;
                output.face = input.uv2;
                output.spin = input.uv3.xy;
                return output;
            }

            // ---- geometry helpers (all in rest-space canvas units) -------------------------------------------

            float2 ToUV(float2 p) { return p / _SlabSize.xy + 0.5; }

            // Channel for the progress bar: a rounded box.
            float BarDist(float2 p)
            {
                float2 q = abs(p - _BarRect.xy) - (_BarRect.zw - _BarRect.w);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - _BarRect.w;
            }

            // Height of the slab's surface in canvas units: rim of the rock, recessed plaque, channel, carved lines,
            // and the fine relief of the stone. 'channel' returns how deep inside the bar channel the point is (0..1).
            float SurfaceHeight(float2 p, out float channel)
            {
                float2 uv = ToUV(p);
                float4 s = SAMPLE_TEXTURE2D_LOD(_SdfTex, sampler_SdfTex, uv, 0);
                float rockA = SAMPLE_TEXTURE2D_LOD(_RockTex, sampler_RockTex, uv, 0).a;

                float rim = smoothstep(0.0, 1.0, saturate(-s.x / 13.0));
                float plaque = smoothstep(0.0, 1.0, saturate(-s.y / 8.0));
                float carve = smoothstep(0.0, 1.0, saturate(-s.z / 1.5));
                float bar = BarDist(p) + (rockA - 0.42) * 6.0;
                channel = smoothstep(0.0, 1.0, saturate(-bar / 5.5));

                float h = 17.0 * rim * (1.0 - 0.62 * plaque);
                h -= 3.2 * carve;
                h -= 7.5 * channel * plaque;
                h += 11.0 * (rockA - 0.42) * (1.0 - 0.72 * plaque);
                return h;
            }

            float3 SurfaceNormal(float2 p, float e)
            {
                float c0, c1, c2, c3;
                float hx1 = SurfaceHeight(p + float2(e, 0), c0);
                float hx0 = SurfaceHeight(p - float2(e, 0), c1);
                float hy1 = SurfaceHeight(p + float2(0, e), c2);
                float hy0 = SurfaceHeight(p - float2(0, e), c3);
                return normalize(float3(-(hx1 - hx0), -(hy1 - hy0), 2.0 * e));
            }

            float Noise3(float3 p) { return SAMPLE_TEXTURE3D_LOD(_NoiseTex, sampler_NoiseTex, p, 0).r; }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 p = input.rest;
                float2 uv = ToUV(p);

                float borderDist = input.shard.x;
                float shardRand = input.shard.y;
                float dissolve = input.shard.z;
                float damage = input.shard.w;
                float2 inward = input.face.xy;
                float released = input.face.z;

                float4 sdf = SAMPLE_TEXTURE2D(_SdfTex, sampler_SdfTex, uv);
                float4 rock = SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, uv);
                float dOut = sdf.x;

                // ---- relief and lighting --------------------------------------------------------------------
                float channelDepth;
                float h0 = SurfaceHeight(p, channelDepth);
                float3 n = SurfaceNormal(p, 1.1);

                // the rock's relief is stored in rest space: follow the shard's spin
                float2 spin = input.spin;
                if (dot(spin, spin) > 0.01)
                    n.xy = float2(n.x * spin.x - n.y * spin.y, n.x * spin.y + n.y * spin.x);

                // fresh broken face along the border of a loose shard
                const float faceWidth = 6.0;
                float face = released * (1.0 - smoothstep(0.0, faceWidth, borderDist));
                n.xy += -inward * face * 0.95;
                n = normalize(n);

                float3 albedo = rock.rgb;
                albedo = lerp(albedo, albedo * 1.45 + 0.02, face * 0.8);

                const float3 keyDir = normalize(float3(-0.50, 0.62, 0.62));
                float3 keyCol = float3(0.96, 0.90, 0.84) * 0.78;
                float3 ambient = float3(0.20, 0.21, 0.25) * 0.9;

                // ---- the molten fill -------------------------------------------------------------------------
                float barLeft = _BarRect.x - _BarRect.z, barLen = _BarRect.z * 2.0;
                float fillX = barLeft + _Progress * barLen;
                float flick = 0.92 + 0.08 * sin(_AnimTime * 11.0) + 0.05 * sin(_AnimTime * 27.0 + 1.3);

                // light from the fill: closest point on the filled stretch, a little above the stone
                float emberOn = smoothstep(0.0, 0.03, _Progress) * _EmberLight * (1.0 - released);
                float2 cp = float2(clamp(p.x, barLeft + 8.0, max(fillX, barLeft + 8.0)), _BarRect.y);
                float3 toL = float3(cp - p, 34.0);
                float dist = length(toL);
                float3 emberDir = toL / dist;
                float emberAtt = 1.0 / (1.0 + dist * dist / (170.0 * 170.0));
                float tipAtt = 1.0 / (1.0 + dot(p - float2(fillX, _BarRect.y), p - float2(fillX, _BarRect.y)) / (90.0 * 90.0));
                float3 emberCol = float3(1.0, 0.36, 0.07);
                float3 emberLight = emberCol * (saturate(dot(n, emberDir)) * emberAtt * 1.35 + tipAtt * 0.35) * emberOn * flick;

                float3 lit = albedo * (ambient + keyCol * saturate(dot(n, keyDir)) + emberLight);

                // wet-looking sheen on the high points near the light
                float3 hv = normalize(keyDir + float3(0, 0, 1));
                float spec = pow(saturate(dot(n, hv)), 28.0) * 0.10 * (0.4 + rock.a);
                lit += spec * keyCol;

                // carved lines: a dark cut with a faint ember inlay, brighter where the fill lights it
                float carve = saturate(-sdf.z / 1.5);
                lit *= 1.0 - 0.55 * carve;
                float inlay = smoothstep(0.35, -0.55, sdf.z);
                float inlayLight = 0.30 + 0.9 * saturate(emberAtt * 1.4) * emberOn;
                lit = lerp(lit, float3(0.62, 0.34, 0.14) * inlayLight * flick, inlay * 0.78);

                // ---- the channel floor and the lava in it ----------------------------------------------------
                float barD = BarDist(p) + (rock.a - 0.42) * 6.0;
                float inChannel = smoothstep(0.5, -1.2, barD);
                lit = lerp(lit, lit * 0.28 + albedo * 0.02, inChannel * 0.8);

                float along = (p.x - barLeft);
                float inFill = smoothstep(fillX + 1.0, fillX - 5.0, p.x) * inChannel * step(0.0005, _Progress);
                float2 qw = p + float2(Noise3(float3(p * 0.0137 + 0.3, _AnimTime * 0.03)) - 0.5, Noise3(float3(p * 0.0111 + 7.7, 0.2)) - 0.5) * 34.0;
                float n1 = Noise3(float3(qw.x * 0.0173 - _AnimTime * 0.17, qw.y * 0.0173, _AnimTime * 0.05));
                float n2 = Noise3(float3(qw.x * 0.0431 + 4.1 - _AnimTime * 0.10, qw.y * 0.0431 + 1.3, _AnimTime * 0.08 + 0.4));
                float n3 = Noise3(float3(qw.x * 0.0093 + 9.2, qw.y * 0.0093, _AnimTime * 0.02));
                float tip = exp(-max(fillX - p.x, 0.0) / 55.0);
                float edgeCool = smoothstep(0.0, 5.0, -barD);                  // lava cools against the walls
                float heat = 0.33 + 0.40 * n1 + 0.22 * n2 + 0.50 * tip;
                heat *= lerp(0.55, 1.0, edgeCool);
                heat *= 1.0 - 0.50 * smoothstep(0.56, 0.78, n3 * 0.55 + n2 * 0.45);   // darker crust patches
                heat = saturate(heat);
                float3 lava = FireRamp(heat) * (0.85 + 0.25 * n1) * flick;
                lit = lerp(lit, lava, inFill);

                // ---- shard cracks, dissolve ------------------------------------------------------------------
                float crackNoise = Noise3(float3(p * 0.045, 0.31 + shardRand)) - 0.5;
                float bd = borderDist + crackNoise * 7.0 * damage;
                float crackHalf = damage * 2.7;
                float inCrack = smoothstep(crackHalf, crackHalf - 1.6, bd) * step(0.001, damage) * (1.0 - released);
                float glowAmt = damage * (1.0 - released);
                float3 crackFire = FireRamp(0.30 + 0.42 * (Noise3(float3(p * 0.08, _AnimTime * 0.3)))) * (0.8 + 0.4 * flick);
                float halo = exp(-max(bd - crackHalf, 0.0) / 5.0) * glowAmt * (1.0 - inCrack);
                lit += crackFire * halo * 0.26;
                lit = lerp(lit, crackFire * 0.95, inCrack);

                // ---- coverage, shadow, dissolve --------------------------------------------------------------
                float cover = saturate(0.5 - dOut / 1.4);
                float erode = 1.0;
                float rim = 0.0;
                if (dissolve > 0.0)
                {
                    float grain = rock.a * 0.55 + Noise3(float3(p * 0.035, shardRand * 3.7)) * 0.45;
                    float edge = dissolve * 1.25 - 0.12;
                    erode = smoothstep(edge, edge + 0.06, grain);
                    rim = (smoothstep(edge - 0.02, edge + 0.02, grain) - smoothstep(edge + 0.05, edge + 0.14, grain)) * (1.0 - dissolve);
                    lit *= 1.0 - 0.45 * dissolve;
                    lit += FireRamp(0.30) * rim * 0.45;
                }

                float shadow = exp(-max(dOut, 0.0) / 15.0) * 0.62 * _Shadow * (1.0 - cover);

                half3 rgb = (half3)(lit * _Exposure * cover * erode);
                half a = (half)(cover * erode + shadow);
                rgb *= input.color.rgb;
                return half4(rgb, a) * input.color.a;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
