// Virtual texturing sampler. The incoming UV is the *virtual* UV (over the whole logical
// texture). We find which page it falls in, look that page up in the indirection table to
// get its slot in the compressed physical atlas, then sample the atlas at the right spot.
Shader "Spark/VTSample"
{
    Properties
    {
        _MainTex   ("Unused",     2D)    = "white" {}
        _Atlas     ("Atlas",      2D)    = "black" {}
        _PageTable ("PageTable",  2D)    = "black" {}
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

            Texture2D        _Atlas;
            SamplerState     sampler_linear_clamp;
            Texture2D<float4> _PageTable;   // each texel: rg = atlas slot (0..255), a = valid

            float  _PagesPerSide; // pages across the virtual texture at the current mip
            float  _AtlasTiles;   // tiles across the atlas
            float  _PageSize;     // texels per page side
            float4 _Window;       // xy = base page (top-left visible), zw = window size in pages

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
                float2 uv = i.uv;
                if (any(uv < 0.0) || any(uv > 1.0))
                    return half4(0.02, 0.02, 0.04, 1.0);          // outside the virtual texture

                float2 pf    = uv * _PagesPerSide;
                int2   page  = (int2)floor(pf);
                float2 local = pf - floor(pf);

                // Look the page up in the view-relative window.
                int2 cell = page - (int2)_Window.xy;
                if (any(cell < 0) || cell.x >= (int)_Window.z || cell.y >= (int)_Window.w)
                    return half4(0.02, 0.02, 0.04, 1.0);

                float4 pt = _PageTable.Load(int3(cell, 0));
                if (pt.a < 0.5)
                    return half4(0.1, 0.0, 0.1, 1.0);             // nothing resident, not even a coarser fallback

                // pt.b = how many mips coarser the resident tile is (0 = exact match). For a
                // fallback, this page is one cell of a 2^k × 2^k block inside the coarser tile.
                int k = (int)(pt.b * 255.0 + 0.5);
                int m = (1 << k) - 1;
                float2 sub = float2(page.x & m, page.y & m);
                float2 t   = (sub + local) / exp2((float)k);

                // Half-texel inset keeps bilinear taps inside the tile, hiding atlas seams.
                float inset = 0.5 / _PageSize;
                t = clamp(t, inset, 1.0 - inset);

                float2 slot     = floor(pt.rg * 255.0 + 0.5);
                float2 atlasUV  = (slot + t) / _AtlasTiles;
                return _Atlas.SampleLevel(sampler_linear_clamp, atlasUV, 0);
            }
            ENDHLSL
        }
    }
}
