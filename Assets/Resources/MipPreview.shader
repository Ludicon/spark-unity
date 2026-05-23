// Single-mip viewer for MipmapMode. Samples _MainTex at the level set in _MipLevel and
// returns the result unchanged. Point filtering is configured on the texture itself
// (FilterMode.Point) so the displayed mip shows individual texels crisply.
Shader "Spark/MipPreview"
{
    Properties
    {
        _MainTex  ("Texture",   2D)    = "white" {}
        _MipLevel ("Mip Level", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float     _MipLevel;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos    : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                return tex2Dlod(_MainTex, float4(i.uv, 0, _MipLevel));
            }
            ENDHLSL
        }
    }
}
