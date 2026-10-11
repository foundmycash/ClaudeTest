// Used only by the Cave Generator's editor preview (inspector and Scene view). Flat vertex colours, back faces
// culled so that from above the roof disappears and you look straight into the cave like a cut-away model.
Shader "Hidden/Havoc/CavePreview"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Cull Back
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 col : COLOR0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.col = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = i.col;
                c.a = 1;
            #if !defined(UNITY_COLORSPACE_GAMMA)
                c.rgb = GammaToLinearSpace(c.rgb);
            #endif
                return c;
            }
            ENDCG
        }
    }
}
