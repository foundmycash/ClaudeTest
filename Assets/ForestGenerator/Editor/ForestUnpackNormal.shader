// Editor only: copies a normal map into plain "xyz * 0.5 + 0.5" colours, whatever encoding Unity stored it in
// (DXT5nm / BC5 / RGB), so the ground texture array builder can read any normal map it is given.
Shader "Hidden/HavocForest/UnpackNormal"
{
    Properties
    {
        _MainTex ("Normal Map", 2D) = "bump" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            float4 frag(v2f_img i) : SV_Target
            {
                float3 n = UnpackNormal(tex2D(_MainTex, i.uv));
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDCG
        }
    }
}
