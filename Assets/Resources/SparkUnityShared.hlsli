#pragma once

#define SPK_ENABLE_FP16 0
#define SPK_ENABLE_FP16_INPUT 0

#pragma use_dxc metal vulkan

#if SHADER_API_GLES3 || SHADER_API_D3D11
    // In GLES target mediump/min16 types.
    #define SPK_HLSL_EXPLICIT_FP16 0
    #define SPK_HLSL_EXPLICIT_INT16 0
#elif UNITY_DEVICE_SUPPORTS_NATIVE_16BIT
    // Otherwise use explicit 16 bit types.
    #pragma dxc_option -enable-16bit-types
    #define SPK_HLSL_EXPLICIT_FP16 1
    #define SPK_HLSL_EXPLICIT_INT16 1
#else
    #define SPK_HLSL_EXPLICIT_FP16 0
    #define SPK_HLSL_EXPLICIT_INT16 0
#endif

#if SHADER_API_GLES3 || SHADER_API_VULKAN
    #define SPK_PREFER_ALU_TABLES 1
    #define SPK_ALLOW_INLINE_CONSTANTS 1 // FIXME: Ideally we should set the table values explicitly.
#elif SHADER_API_METAL
    #define SPK_PREFER_ALU_TABLES 0
    #define SPK_ALLOW_INLINE_CONSTANTS 1
#else
    #define SPK_PREFER_ALU_TABLES 1
    #define SPK_ALLOW_INLINE_CONSTANTS 1
#endif

#if SHADER_API_METAL
    // Avoid fastmath optimizations. This only works in metal. What happens on other platforms?
    #pragma disable_fastmath
#endif



#if defined(FORMAT_ASTC_RGB) || defined(FORMAT_ASTC_RGBA)
    #include "shaders/hlsl/spark_astc.hlsli"
#elif defined(FORMAT_EAC_R) || defined(FORMAT_EAC_RG) || defined(FORMAT_ETC2_RGB) || defined(FORMAT_ETC2_RGBA)
    #include "shaders/hlsl/spark_etc.hlsli"
#else
    #include "shaders/hlsl/spark_bcn.hlsli"
#endif



#define JOIN2(a, b) a##b
#define JOIN(a, b) JOIN2(a, b)

#define SPARK_KERNEL(encode, quality) \
    [numthreads(16, 16, 1)] void JOIN(encode, quality)(uint2 tid : SV_DispatchThreadID) { JOIN(encode,_tid)(tid); }


#if FORMAT_BC1_RGB || FORMAT_BC4_R || FORMAT_ETC2_RGB || FORMAT_EAC_R
    #if SHADER_API_GLES3
        // GLES does not support the rg32ui image format qualifier, so we use rgba16ui instead.
        #define DST_FORMAT min16uint4
    #else
        #define DST_FORMAT uint2
    #endif
#else
    #define DST_FORMAT uint4
#endif

Texture2D _Src;
SamplerState sampler_Src;
RWTexture2D<DST_FORMAT> _Dst;


#if SHADER_API_VULKAN
    // In Vulkan `textureGatherOffset` requires the `shaderImageGatherExtended` feature. This is available on all the relevant Adreno and Arm devices, but not on ImgTec GE8320.
    // In Mali-G78 the EAC encoder has issues with the gather code path, so this is disabled by default.
    // On low end Adreno devices the fetch offset code path appears to be slower, so this is disabled by default also.
    #define USE_GATHER 0
    #define USE_GATHER_OFFSET 0
    #define USE_FETCH_OFFSET 0
#elif SHADER_API_GLES3
    // In GLES textureGatherOffset only requires GLES 3.1, but it may be emulated on some devices.
    #define USE_GATHER 0
    #define USE_GATHER_OFFSET 0
    #define USE_FETCH_OFFSET 0
#elif SHADER_API_METAL
    #define USE_GATHER 1
    #define USE_GATHER_OFFSET 1
    #define USE_FETCH_OFFSET 1
#else
    #define USE_GATHER 1
    #define USE_GATHER_OFFSET 1
    #define USE_FETCH_OFFSET 1
#endif

#if USE_GATHER_OFFSET
#define OFFSET(x, y)  , int2(x, y)
#else
#define OFFSET(x, y)  + float2(x, y)
#endif

void LoadTexelBlockR(Texture2D tex, SamplerState samp, uint2 tid, out spk_float r[16])
{
#if USE_GATHER
    uint w, h;
    tex.GetDimensions(w, h);
    float2 uv = float2(tid * 4u) / float2(w, h);

    spk_float4 r00 = tex.GatherRed(samp, uv OFFSET(1, 1));
    spk_float4 r01 = tex.GatherRed(samp, uv OFFSET(3, 1));
    spk_float4 r10 = tex.GatherRed(samp, uv OFFSET(1, 3));
    spk_float4 r11 = tex.GatherRed(samp, uv OFFSET(3, 3));

    r[0]  = r00.w; r[1]  = r00.z;   r[2]  = r01.w; r[3]  = r01.z;
    r[4]  = r00.x; r[5]  = r00.y;   r[6]  = r01.x; r[7]  = r01.y;
    r[8]  = r10.w; r[9]  = r10.z;   r[10] = r11.w; r[11] = r11.z;
    r[12] = r10.x; r[13] = r10.y;   r[14] = r11.x; r[15] = r11.y;
#elif USE_FETCH_OFFSET
    int3 loc = int3(tid * 4, 0);
    r[0]  = tex.Load(loc, int2(0, 0)).r; r[1]  = tex.Load(loc, int2(1, 0)).r; r[2]  = tex.Load(loc, int2(2, 0)).r; r[3]  = tex.Load(loc, int2(3, 0)).r;
    r[4]  = tex.Load(loc, int2(0, 1)).r; r[5]  = tex.Load(loc, int2(1, 1)).r; r[6]  = tex.Load(loc, int2(2, 1)).r; r[7]  = tex.Load(loc, int2(3, 1)).r;
    r[8]  = tex.Load(loc, int2(0, 2)).r; r[9]  = tex.Load(loc, int2(1, 2)).r; r[10] = tex.Load(loc, int2(2, 2)).r; r[11] = tex.Load(loc, int2(3, 2)).r;
    r[12] = tex.Load(loc, int2(0, 3)).r; r[13] = tex.Load(loc, int2(1, 3)).r; r[14] = tex.Load(loc, int2(2, 3)).r; r[15] = tex.Load(loc, int2(3, 3)).r;
#else
    uint2 coord = tid.xy << 2;
    for (uint i = 0; i < 16; ++i)
    {
        uint2 offs = uint2(i & 3, i >> 2);
        spk_float4 pix = tex.Load(uint3(coord + offs, 0));
        r[i] = pix.r;
    }
#endif
}

void LoadTexelBlockRG(Texture2D tex, SamplerState samp, uint2 tid, out spk_float r[16], out spk_float g[16])
{
#if USE_GATHER
    uint w, h;
    tex.GetDimensions(w, h);
    float2 uv = float2(tid * 4u) / float2(w, h);

    spk_float4 r00 = tex.GatherRed(samp, uv OFFSET(1, 1));
    spk_float4 r01 = tex.GatherRed(samp, uv OFFSET(3, 1));
    spk_float4 r10 = tex.GatherRed(samp, uv OFFSET(1, 3));
    spk_float4 r11 = tex.GatherRed(samp, uv OFFSET(3, 3));

    r[0]  = r00.w; r[1]  = r00.z; r[2]  = r01.w; r[3]  = r01.z;
    r[4]  = r00.x; r[5]  = r00.y; r[6]  = r01.x; r[7]  = r01.y;
    r[8]  = r10.w; r[9]  = r10.z; r[10] = r11.w; r[11] = r11.z;
    r[12] = r10.x; r[13] = r10.y; r[14] = r11.x; r[15] = r11.y;

    spk_float4 g00 = tex.GatherGreen(samp, uv OFFSET(1, 1));
    spk_float4 g01 = tex.GatherGreen(samp, uv OFFSET(3, 1));
    spk_float4 g10 = tex.GatherGreen(samp, uv OFFSET(1, 3));
    spk_float4 g11 = tex.GatherGreen(samp, uv OFFSET(3, 3));

    g[0]  = g00.w; g[1]  = g00.z;   g[2]  = g01.w; g[3]  = g01.z;
    g[4]  = g00.x; g[5]  = g00.y;   g[6]  = g01.x; g[7]  = g01.y;
    g[8]  = g10.w; g[9]  = g10.z;   g[10] = g11.w; g[11] = g11.z;
    g[12] = g10.x; g[13] = g10.y;   g[14] = g11.x; g[15] = g11.y;
#elif USE_FETCH_OFFSET
    int3 loc = int3(tid * 4, 0);
    spk_float4 tmp;
    tmp = tex.Load(loc, int2(0, 0)); r[0] = tmp.r; g[0] = tmp.g;
    tmp = tex.Load(loc, int2(1, 0)); r[1] = tmp.r; g[1] = tmp.g;
    tmp = tex.Load(loc, int2(2, 0)); r[2] = tmp.r; g[2] = tmp.g;
    tmp = tex.Load(loc, int2(3, 0)); r[3] = tmp.r; g[3] = tmp.g;
    tmp = tex.Load(loc, int2(0, 1)); r[4] = tmp.r; g[4] = tmp.g;
    tmp = tex.Load(loc, int2(1, 1)); r[5] = tmp.r; g[5] = tmp.g;
    tmp = tex.Load(loc, int2(2, 1)); r[6] = tmp.r; g[6] = tmp.g;
    tmp = tex.Load(loc, int2(3, 1)); r[7] = tmp.r; g[7] = tmp.g;
    tmp = tex.Load(loc, int2(0, 2)); r[8] = tmp.r; g[8] = tmp.g;
    tmp = tex.Load(loc, int2(1, 2)); r[9] = tmp.r; g[9] = tmp.g;
    tmp = tex.Load(loc, int2(2, 2)); r[10] = tmp.r; g[10] = tmp.g;
    tmp = tex.Load(loc, int2(3, 2)); r[11] = tmp.r; g[11] = tmp.g;
    tmp = tex.Load(loc, int2(0, 3)); r[12] = tmp.r; g[12] = tmp.g;
    tmp = tex.Load(loc, int2(1, 3)); r[13] = tmp.r; g[13] = tmp.g;
    tmp = tex.Load(loc, int2(2, 3)); r[14] = tmp.r; g[14] = tmp.g;
    tmp = tex.Load(loc, int2(3, 3)); r[15] = tmp.r; g[15] = tmp.g;
#else
    uint2 coord = tid.xy << 2;
    for (uint i = 0; i < 16; ++i)
    {
        uint2 offs = uint2(i & 3, i >> 2);
        spk_float4 pix = tex.Load(uint3(coord + offs, 0));
        r[i] = pix.r;
        g[i] = pix.g;
    }
#endif
}

void LoadTexelBlockRGB(Texture2D tex, SamplerState samp, uint2 tid, out spk_float3 rgb[16])
{
#if USE_GATHER
    uint w, h;
    tex.GetDimensions(w, h);
    float2 uv = float2(tid * 4u) / float2(w, h);

    spk_float4 r00 = tex.GatherRed(samp, uv OFFSET(1, 1));
    spk_float4 r01 = tex.GatherRed(samp, uv OFFSET(3, 1));
    spk_float4 r10 = tex.GatherRed(samp, uv OFFSET(1, 3));
    spk_float4 r11 = tex.GatherRed(samp, uv OFFSET(3, 3));

    rgb[0].r  = r00.w; rgb[1].r  = r00.z;   rgb[2].r  = r01.w; rgb[3].r  = r01.z;
    rgb[4].r  = r00.x; rgb[5].r  = r00.y;   rgb[6].r  = r01.x; rgb[7].r  = r01.y;
    rgb[8].r  = r10.w; rgb[9].r  = r10.z;   rgb[10].r = r11.w; rgb[11].r = r11.z;
    rgb[12].r = r10.x; rgb[13].r = r10.y;   rgb[14].r = r11.x; rgb[15].r = r11.y;

    spk_float4 g00 = tex.GatherGreen(samp, uv OFFSET(1, 1));
    spk_float4 g01 = tex.GatherGreen(samp, uv OFFSET(3, 1));
    spk_float4 g10 = tex.GatherGreen(samp, uv OFFSET(1, 3));
    spk_float4 g11 = tex.GatherGreen(samp, uv OFFSET(3, 3));

    rgb[0].g  = g00.w; rgb[1].g  = g00.z;   rgb[2].g  = g01.w; rgb[3].g  = g01.z;
    rgb[4].g  = g00.x; rgb[5].g  = g00.y;   rgb[6].g  = g01.x; rgb[7].g  = g01.y;
    rgb[8].g  = g10.w; rgb[9].g  = g10.z;   rgb[10].g = g11.w; rgb[11].g = g11.z;
    rgb[12].g = g10.x; rgb[13].g = g10.y;   rgb[14].g = g11.x; rgb[15].g = g11.y;

    spk_float4 b00 = tex.GatherBlue(samp, uv OFFSET(1, 1));
    spk_float4 b01 = tex.GatherBlue(samp, uv OFFSET(3, 1));
    spk_float4 b10 = tex.GatherBlue(samp, uv OFFSET(1, 3));
    spk_float4 b11 = tex.GatherBlue(samp, uv OFFSET(3, 3));

    rgb[0].b  = b00.w; rgb[1].b  = b00.z;   rgb[2].b  = b01.w; rgb[3].b  = b01.z;
    rgb[4].b  = b00.x; rgb[5].b  = b00.y;   rgb[6].b  = b01.x; rgb[7].b  = b01.y;
    rgb[8].b  = b10.w; rgb[9].b  = b10.z;   rgb[10].b = b11.w; rgb[11].b = b11.z;
    rgb[12].b = b10.x; rgb[13].b = b10.y;   rgb[14].b = b11.x; rgb[15].b = b11.y;
#elif USE_FETCH_OFFSET
    int3 loc = int3(tid * 4, 0);
    rgb[0] = tex.Load(loc, int2(0, 0));
    rgb[1] = tex.Load(loc, int2(1, 0));
    rgb[2] = tex.Load(loc, int2(2, 0));
    rgb[3] = tex.Load(loc, int2(3, 0));
    rgb[4] = tex.Load(loc, int2(0, 1));
    rgb[5] = tex.Load(loc, int2(1, 1));
    rgb[6] = tex.Load(loc, int2(2, 1));
    rgb[7] = tex.Load(loc, int2(3, 1));
    rgb[8] = tex.Load(loc, int2(0, 2));
    rgb[9] = tex.Load(loc, int2(1, 2));
    rgb[10] = tex.Load(loc, int2(2, 2));
    rgb[11] = tex.Load(loc, int2(3, 2));
    rgb[12] = tex.Load(loc, int2(0, 3));
    rgb[13] = tex.Load(loc, int2(1, 3));
    rgb[14] = tex.Load(loc, int2(2, 3));
    rgb[15] = tex.Load(loc, int2(3, 3));
#else
    uint2 coord = tid.xy << 2;
    for (uint i = 0; i < 16; ++i)
    {
        uint2 offs = uint2(i & 3, i >> 2);
        spk_float4 pix = tex.Load(uint3(coord + offs, 0));
        rgb[i] = pix.rgb;
    }
#endif
}

void LoadTexelBlockRGBA(Texture2D tex, SamplerState samp, uint2 tid, out spk_float3 rgb[16], out spk_float alpha[16])
{
#if USE_GATHER
    uint w, h;
    tex.GetDimensions(w, h);
    float2 uv = float2(tid * 4u) / float2(w, h);

    spk_float4 r00 = tex.GatherRed(samp, uv OFFSET(1, 1));
    spk_float4 r01 = tex.GatherRed(samp, uv OFFSET(3, 1));
    spk_float4 r10 = tex.GatherRed(samp, uv OFFSET(1, 3));
    spk_float4 r11 = tex.GatherRed(samp, uv OFFSET(3, 3));

    rgb[0].r  = r00.w; rgb[1].r  = r00.z;   rgb[2].r  = r01.w; rgb[3].r  = r01.z;
    rgb[4].r  = r00.x; rgb[5].r  = r00.y;   rgb[6].r  = r01.x; rgb[7].r  = r01.y;
    rgb[8].r  = r10.w; rgb[9].r  = r10.z;   rgb[10].r = r11.w; rgb[11].r = r11.z;
    rgb[12].r = r10.x; rgb[13].r = r10.y;   rgb[14].r = r11.x; rgb[15].r = r11.y;

    spk_float4 g00 = tex.GatherGreen(samp, uv OFFSET(1, 1));
    spk_float4 g01 = tex.GatherGreen(samp, uv OFFSET(3, 1));
    spk_float4 g10 = tex.GatherGreen(samp, uv OFFSET(1, 3));
    spk_float4 g11 = tex.GatherGreen(samp, uv OFFSET(3, 3));

    rgb[0].g  = g00.w; rgb[1].g  = g00.z;   rgb[2].g  = g01.w; rgb[3].g  = g01.z;
    rgb[4].g  = g00.x; rgb[5].g  = g00.y;   rgb[6].g  = g01.x; rgb[7].g  = g01.y;
    rgb[8].g  = g10.w; rgb[9].g  = g10.z;   rgb[10].g = g11.w; rgb[11].g = g11.z;
    rgb[12].g = g10.x; rgb[13].g = g10.y;   rgb[14].g = g11.x; rgb[15].g = g11.y;

    spk_float4 b00 = tex.GatherBlue(samp, uv OFFSET(1, 1));
    spk_float4 b01 = tex.GatherBlue(samp, uv OFFSET(3, 1));
    spk_float4 b10 = tex.GatherBlue(samp, uv OFFSET(1, 3));
    spk_float4 b11 = tex.GatherBlue(samp, uv OFFSET(3, 3));

    rgb[0].b  = b00.w; rgb[1].b  = b00.z;   rgb[2].b  = b01.w; rgb[3].b  = b01.z;
    rgb[4].b  = b00.x; rgb[5].b  = b00.y;   rgb[6].b  = b01.x; rgb[7].b  = b01.y;
    rgb[8].b  = b10.w; rgb[9].b  = b10.z;   rgb[10].b = b11.w; rgb[11].b = b11.z;
    rgb[12].b = b10.x; rgb[13].b = b10.y;   rgb[14].b = b11.x; rgb[15].b = b11.y;

    spk_float4 a00 = tex.GatherAlpha(samp, uv OFFSET(1, 1));
    spk_float4 a01 = tex.GatherAlpha(samp, uv OFFSET(3, 1));
    spk_float4 a10 = tex.GatherAlpha(samp, uv OFFSET(1, 3));
    spk_float4 a11 = tex.GatherAlpha(samp, uv OFFSET(3, 3));

    alpha[0]  = a00.w; alpha[1]  = a00.z;   alpha[2]  = a01.w; alpha[3]  = a01.z;
    alpha[4]  = a00.x; alpha[5]  = a00.y;   alpha[6]  = a01.x; alpha[7]  = a01.y;
    alpha[8]  = a10.w; alpha[9]  = a10.z;   alpha[10] = a11.w; alpha[11] = a11.z;
    alpha[12] = a10.x; alpha[13] = a10.y;   alpha[14] = a11.x; alpha[15] = a11.y;

#elif USE_FETCH_OFFSET // @@ Is this broken???
    int3 loc = int3(tid * 4, 0);
    spk_float4 tmp;
    tmp = tex.Load(loc, int2(0, 0)); rgb[0]  = tmp.xyz; alpha[0]  = tmp.w;
    tmp = tex.Load(loc, int2(1, 0)); rgb[1]  = tmp.xyz; alpha[1]  = tmp.w;
    tmp = tex.Load(loc, int2(2, 0)); rgb[2]  = tmp.xyz; alpha[2]  = tmp.w;
    tmp = tex.Load(loc, int2(3, 0)); rgb[3]  = tmp.xyz; alpha[3]  = tmp.w;
    tmp = tex.Load(loc, int2(0, 1)); rgb[4]  = tmp.xyz; alpha[4]  = tmp.w;
    tmp = tex.Load(loc, int2(1, 1)); rgb[5]  = tmp.xyz; alpha[5]  = tmp.w;
    tmp = tex.Load(loc, int2(2, 1)); rgb[6]  = tmp.xyz; alpha[6]  = tmp.w;
    tmp = tex.Load(loc, int2(3, 1)); rgb[7]  = tmp.xyz; alpha[7]  = tmp.w;
    tmp = tex.Load(loc, int2(0, 2)); rgb[8]  = tmp.xyz; alpha[8]  = tmp.w;
    tmp = tex.Load(loc, int2(1, 2)); rgb[9]  = tmp.xyz; alpha[9]  = tmp.w;
    tmp = tex.Load(loc, int2(2, 2)); rgb[10] = tmp.xyz; alpha[10] = tmp.w;
    tmp = tex.Load(loc, int2(3, 2)); rgb[11] = tmp.xyz; alpha[11] = tmp.w;
    tmp = tex.Load(loc, int2(0, 3)); rgb[12] = tmp.xyz; alpha[12] = tmp.w;
    tmp = tex.Load(loc, int2(1, 3)); rgb[13] = tmp.xyz; alpha[13] = tmp.w;
    tmp = tex.Load(loc, int2(2, 3)); rgb[14] = tmp.xyz; alpha[14] = tmp.w;
    tmp = tex.Load(loc, int2(3, 3)); rgb[15] = tmp.xyz; alpha[15] = tmp.w;
#else
    uint2 coord = tid.xy << 2;
    for (uint i = 0; i < 16; ++i)
    {
        uint2 offs = uint2(i & 3, i >> 2);
        spk_float4 pix = tex.Load(uint3(coord + offs, 0));
        rgb[i] = pix.rgb;
        alpha[i] = pix.a;
    }
#endif
}

void LoadTexelBlockRGBA(Texture2D tex, SamplerState samp, uint2 tid, out spk_float4 rgba[16])
{
#if USE_FETCH_OFFSET
    int3 loc = int3(tid * 4, 0);
    rgba[0] = tex.Load(loc, int2(0, 0));
    rgba[1] = tex.Load(loc, int2(1, 0));
    rgba[2] = tex.Load(loc, int2(2, 0));
    rgba[3] = tex.Load(loc, int2(3, 0));
    rgba[4] = tex.Load(loc, int2(0, 1));
    rgba[5] = tex.Load(loc, int2(1, 1));
    rgba[6] = tex.Load(loc, int2(2, 1));
    rgba[7] = tex.Load(loc, int2(3, 1));
    rgba[8] = tex.Load(loc, int2(0, 2));
    rgba[9] = tex.Load(loc, int2(1, 2));
    rgba[10] = tex.Load(loc, int2(2, 2));
    rgba[11] = tex.Load(loc, int2(3, 2));
    rgba[12] = tex.Load(loc, int2(0, 3));
    rgba[13] = tex.Load(loc, int2(1, 3));
    rgba[14] = tex.Load(loc, int2(2, 3));
    rgba[15] = tex.Load(loc, int2(3, 3));
#else
    uint2 coord = tid.xy << 2;
    for (uint i = 0; i < 16; ++i)
    {
        uint2 offs = uint2(i & 3, i >> 2);
        rgba[i] = tex.Load(uint3(coord + offs, 0));
    }
#endif
}

inline spk_float linear_to_srgb(spk_float x) {
    if (x <= 0.0031308) return x * 12.92;
    return 1.055 * pow(x, 1.0 / 2.4) - 0.055;
}

inline spk_float linear_to_srgb_fast(spk_float x) {
    spk_float s1 = sqrt(x);
    spk_float s2 = sqrt(s1);
    spk_float s3 = sqrt(s2);
    return 0.662002687 * s1 + 0.684122060 * s2 - 0.323583601 * s3 - 0.0225411470 * x;
}

spk_float3 LinearToSRGB(spk_float3 linRGB)
{
    // from https://chilliant.blogspot.com/2012/08/srgb-approximations-for-hlsl.html
    //return max(1.055h * pow(saturate(linRGB), 0.416666667h) - 0.055h, 0.h);

    //return spk_float3(linear_to_srgb(linRGB.x), linear_to_srgb(linRGB.y), linear_to_srgb(linRGB.z));

    return spk_float3(linear_to_srgb_fast(linRGB.x), linear_to_srgb_fast(linRGB.y), linear_to_srgb_fast(linRGB.z));
}

void LinearToSRGB(inout spk_float3 rgb[16])
{
    for (int i = 0; i < 16; ++i)
        rgb[i] = LinearToSRGB(rgb[i]);
}

void LinearToSRGB(inout spk_float4 rgba[16])
{
    for (int i = 0; i < 16; ++i)
        rgba[i].rgb = LinearToSRGB(rgba[i].rgb);
}

#if SHADER_API_GLES3

    min16uint4 pack_uint2(uint2 v)
    {
        return min16uint4(v.x & 0xFFFF, v.x >> 16, v.y & 0xFFFF, v.y >> 16);
    }

#else

    uint2 pack_uint2(uint2 v)
    {
        return v;
    }

#endif
