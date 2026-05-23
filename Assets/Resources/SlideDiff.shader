// Slideshow view shader. Samples the original source and the compressed (Spark-encoded)
// texture and outputs one of three views based on _Mode:
//   0 = original (_MainTex)
//   1 = compressed (_CompressedTex)
//   2 = abs(original - compressed) * _DiffAmplify
//
// _ChannelMode applies a per-format pre-processing pass to *both* textures before the
// _Mode selection, so each view (and the diff between them) reflects how the format would
// actually be sampled in a real shader:
//   0 = R    — replicate .r across rgb (greyscale).
//   1 = RG   — reconstruct z = sqrt(1 - x² - y²) and pack back to [0,1] so RG-only
//              encodes (BC5_RG / EAC_RG) display as full normal maps.
//   2 = RGB  — pass-through, alpha forced to 1.
//   3 = RGBA — premultiply rgb by alpha so transparent regions don't show false colors;
//              the diff then highlights both color and coverage error.
//
// Both textures are sampled with an explicit point-clamp sampler at mip 0, which makes the
// comparison honest regardless of each texture's FilterMode (the loaded source is usually
// Bilinear, the encoded RT is Point — a mismatch would put resampling noise into the diff).
Shader "Spark/SlideDiff"
{
    Properties
    {
        _MainTex       ("Original",    2D)    = "white" {}
        _CompressedTex ("Compressed",  2D)    = "white" {}
        _Mode          ("Mode",        Float) = 1.0
        _DiffAmplify   ("Amplify",     Float) = 8.0
        _ChannelMode   ("ChannelMode", Float) = 2.0
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

            Texture2D    _MainTex;
            Texture2D    _CompressedTex;
            // Unity recognizes this name and binds a point/clamp sampler automatically.
            SamplerState sampler_point_clamp;

            float _Mode;
            float _DiffAmplify;
            float _ChannelMode;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos    : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            half4 ApplyChannelMode(half4 c)
            {
                if (_ChannelMode < 0.5)
                    return half4(c.r, c.r, c.r, 1.0);              // R   — greyscale
                if (_ChannelMode < 1.5)
                {
                    half2 xy = c.rg * 2.0 - 1.0;
                    half  z  = sqrt(saturate(1.0 - dot(xy, xy)));
                    return half4(c.r, c.g, z * 0.5 + 0.5, 1.0);    // RG  — reconstruct Z
                }
                if (_ChannelMode < 2.5)
                    return half4(c.rgb, 1.0);                      // RGB — pass-through
                return half4(c.rgb * c.a, c.a);                    // RGBA — premultiplied
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 a = ApplyChannelMode(_MainTex      .SampleLevel(sampler_point_clamp, i.uv, 0));
                half4 b = ApplyChannelMode(_CompressedTex.SampleLevel(sampler_point_clamp, i.uv, 0));
                if (_Mode < 0.5) return a;
                if (_Mode < 1.5) return b;
                return half4(abs(a.rgb - b.rgb) * _DiffAmplify, 1.0);
            }
            ENDHLSL
        }
    }
}
