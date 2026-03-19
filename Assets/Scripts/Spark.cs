using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

/// <summary>
/// Texture compression format.
/// </summary>
public enum SparkFormat
{
    // Generic — auto-resolved to the best supported format on the current GPU
    R,              // Single channel → BC4_R or EAC_R
    RG,             // Two channels   → BC5_RG or EAC_RG
    RGB,            // Color          → BC7_RGB, ASTC_4x4_RGB, ETC2_RGB, or BC1_RGB
    RGBA,           // Color + alpha  → BC7_RGBA, ASTC_4x4_RGBA, ETC2_RGBA, or BC3_RGBA

    // Desktop formats
    BC1_RGB,        // RGB, 4 bpp
    BC3_RGBA,       // RGBA, 8 bpp
    //BC3_YCoCg,      // YCoCg (RGB), 8 bpp
    //BC3_RGBM,       // HDR via RGBM encoding, 8 bpp
    BC4_R,          // Single channel (R), 4 bpp
    BC5_RG,         // Two channels (RG), 8 bpp — ideal for normal maps
    BC7_RGB,        // High-quality RGB, 8 bpp
    BC7_RGBA,       // High-quality RGBA, 8 bpp

    // Mobile formats
    ASTC_4x4_RGB,   // RGB, 8 bpp
    ASTC_4x4_RGBA,  // RGBA, 8 bpp
    //ASTC_4x4_RGBM,  // HDR via RGBM encoding, 8 bpp
    ETC2_RGB,       // RGB, 4 bpp
    ETC2_RGBA,      // RGBA, 8 bpp
    EAC_R,          // Single channel (R), 4 bpp
    EAC_RG,         // Two channels (RG), 8 bpp
}

/// <summary>
/// Compression quality level. Higher quality is slower.
/// </summary>
public enum SparkQuality
{
    Low    = 0,
    Medium = 1,
    High   = 2,
}

/// <summary>
/// GPU texture compression using Spark codecs.
///
///   // One-liner
///   Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.BC7_RGB);
///
///   // With options
///   Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.ASTC_4x4_RGB, SparkQuality.High, srgb: true);
///
///   // Auto-select best format for current GPU
///   Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB);
///
///   // Preload shaders to avoid first-encode hitch
///   Spark.Preload(SparkQuality.Medium, SparkFormat.RGB, SparkFormat.RGBA);
///   // Or spread across frames via coroutine:
///   StartCoroutine(Spark.PreloadAsync(SparkQuality.Medium, SparkFormat.RGB, SparkFormat.RGBA));
///
///   // Record into a CommandBuffer (for async compute or batching)
///   var cmd = new CommandBuffer();
///   var format = Spark.ResolveFormat(SparkFormat.RGB);
///   var dst = new Texture2D(w, h, Spark.GetCompressedFormat(format, false), TextureCreationFlags.None);
///   Spark.EncodeTexture(cmd, source, dst, format);
///   Graphics.ExecuteCommandBuffer(cmd);
///
///   // Release cached render targets when done
///   Spark.ReleaseCache();
/// </summary>
public static class Spark
{
    // ───────────────────────────────────────────────
    //  Public API
    // ───────────────────────────────────────────────

    /// <summary>
    /// Encode a texture into a GPU-compressed format.
    /// The returned Texture2D uses the corresponding compressed GraphicsFormat.
    ///
    /// Generic formats (R, RG, RGB, RGBA) are resolved to the best concrete format
    /// supported on the current GPU.
    /// </summary>
    /// <param name="source">Source texture (any GPU-readable format, e.g. RGBA32).</param>
    /// <param name="format">Target compressed format (concrete or generic).</param>
    /// <param name="quality">Compression quality (Low/Medium/High).</param>
    /// <param name="srgb">If true, the output texture uses an sRGB format.</param>
    public static Texture2D EncodeTexture(Texture source, SparkFormat format, SparkQuality quality = SparkQuality.Medium, bool srgb = false)
    {
        format = ResolveFormat(format);

        GraphicsFormat compressedFmt = GetCompressedFormat(format, srgb);
        var result = new Texture2D(source.width, source.height, compressedFmt,
            TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);

        var cmd = GetCommandBuffer();
        cmd.Clear();
        cmd.BeginSample(GpuSampleName);
        EncodeTexture(cmd, source, result, format, quality);
        cmd.EndSample(GpuSampleName);
        Graphics.ExecuteCommandBuffer(cmd);

        return result;
    }

    /// <summary>
    /// Record texture encoding commands into a CommandBuffer.
    /// The caller is responsible for executing the command buffer (e.g. via
    /// Graphics.ExecuteCommandBuffer or on an async compute queue).
    ///
    /// The destination texture must have the correct compressed GraphicsFormat
    /// and match the source dimensions. Use GetCompressedFormat to determine
    /// the right format.
    /// </summary>
    /// <param name="cmd">CommandBuffer to record into.</param>
    /// <param name="source">Source texture (any GPU-readable format, e.g. RGBA32).</param>
    /// <param name="destination">Destination texture with a compressed GraphicsFormat.</param>
    /// <param name="format">Target compressed format (concrete or generic).</param>
    /// <param name="quality">Compression quality (Low/Medium/High).</param>
    public static void EncodeTexture(CommandBuffer cmd, Texture source, Texture destination, SparkFormat format, SparkQuality quality = SparkQuality.Medium)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (cmd == null)
            throw new ArgumentNullException(nameof(cmd));
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));

        format = ResolveFormat(format);

        int width  = source.width;
        int height = source.height;

        if (width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"Texture dimensions ({width}x{height}) must be multiples of 4.");

        // Resolve shader and kernel.
        ComputeShader shader = IsDesktopFormat(format) ? DesktopShader : MobileShader;
        if (shader == null)
            throw new InvalidOperationException(
                $"Could not load compute shader. Make sure SparkUnityDesktop/SparkUnityMobile are in a Resources folder.");

        string kernelName = GetKernelName(format, quality);
        int kernel = shader.FindKernel(kernelName);

        int blockW = width  / 4;
        int blockH = height / 4;

        // Get or reuse cached render texture sized in blocks.
        GraphicsFormat outputFmt = GetTemporaryOutputFormat(format);
        var rt = GetOrCreateRT(blockW, blockH, outputFmt);

        // Bind constant tables for formats that need them (ASTC on Android).
        var constantBuffer = GetConstantBuffer(format);
        if (constantBuffer != null)
            cmd.SetComputeBufferParam(shader, kernel, "Tables", constantBuffer);

        int groupsX = (blockW + 15) / 16;
        int groupsY = (blockH + 15) / 16;

        cmd.SetComputeTextureParam(shader, kernel, "_Src", source);
        cmd.SetComputeTextureParam(shader, kernel, "_Dst", rt);
        cmd.DispatchCompute(shader, kernel, groupsX, groupsY, 1);
        // The uint RT pixels map 1:1 to compressed blocks (same byte layout).
        cmd.CopyTexture(rt, 0, 0, destination, 0, 0);
    }

    /// <summary>
    /// Returns true if the given format is supported on the current platform.
    /// Generic formats (R, RG, RGB, RGBA) return true if at least one candidate is supported.
    /// </summary>
    public static bool IsFormatSupported(SparkFormat format)
    {
        if (IsGenericFormat(format))
            format = ResolveFormat(format);

        return SystemInfo.IsFormatSupported(GetCompressedFormat(format, false), GraphicsFormatUsage.Sample);
    }

    /// <summary>
    /// Release cached render targets and reset resolved format state.
    /// Call this when you're done encoding (e.g. on scene unload) to free GPU memory.
    /// </summary>
    public static void ReleaseCache()
    {
        if (s_cachedSmallRT != null)
        {
            s_cachedSmallRT.Release();
            UnityEngine.Object.Destroy(s_cachedSmallRT);
            s_cachedSmallRT = null;
        }
        if (s_cachedLargeRT != null)
        {
            s_cachedLargeRT.Release();
            UnityEngine.Object.Destroy(s_cachedLargeRT);
            s_cachedLargeRT = null;
        }
        if (s_warmupSrc != null)
        {
            UnityEngine.Object.Destroy(s_warmupSrc);
            s_warmupSrc = null;
        }
        if (s_astcConstantBuffer != null)
        {
            s_astcConstantBuffer.Release();
            s_astcConstantBuffer = null;
        }
    }

    // ───────────────────────────────────────────────
    //  GPU timing
    // ───────────────────────────────────────────────

    const string GpuSampleName = "SparkEncode";
    static CommandBuffer s_cmd;
    static Recorder s_gpuRecorder;

    static CommandBuffer GetCommandBuffer()
    {
        if (s_cmd == null)
            s_cmd = new CommandBuffer { name = GpuSampleName };
        return s_cmd;
    }

    /// <summary>
    /// GPU time of the last encode in milliseconds (from GPU timestamp queries).
    /// Updated one frame after the encode — returns 0 until the first result is available.
    /// </summary>
    public static float GpuTimeMs
    {
        get
        {
            if (s_gpuRecorder == null)
            {
                var sampler = Sampler.Get(GpuSampleName);
                if (sampler.isValid)
                {
                    s_gpuRecorder = sampler.GetRecorder();
                    s_gpuRecorder.enabled = true;
                }
            }
            if (s_gpuRecorder != null && s_gpuRecorder.isValid && s_gpuRecorder.gpuElapsedNanoseconds > 0)
                return s_gpuRecorder.gpuElapsedNanoseconds / 1_000_000f;
            return 0f;
        }
    }

    /// <summary>
    /// Preload (warm up) compute shader kernels for the given quality and formats.
    /// Forces the GPU driver to compile each kernel by issuing a tiny 4x4 dummy dispatch.
    /// Generic formats are resolved before warmup.
    /// </summary>
    public static void Preload(SparkQuality quality, params SparkFormat[] formats)
    {
        foreach (var entry in CollectKernels(quality, formats))
            WarmupKernel(entry.shader, entry.kernel, entry.format);
    }

    /// <summary>
    /// Coroutine that preloads compute shader kernels spread across frames (one kernel per frame).
    /// Use with StartCoroutine(Spark.PreloadAsync(SparkQuality.Medium, SparkFormat.RGB, SparkFormat.RGBA)).
    /// </summary>
    public static IEnumerator PreloadAsync(SparkQuality quality, params SparkFormat[] formats)
    {
        foreach (var entry in CollectKernels(quality, formats))
        {
            WarmupKernel(entry.shader, entry.kernel, entry.format);
            yield return null;
        }
    }


    // ───────────────────────────────────────────────
    //  Compute Shaders
    // ───────────────────────────────────────────────

    static ComputeShader s_desktopShader;
    static ComputeShader s_mobileShader;

    static ComputeShader DesktopShader =>
        s_desktopShader != null ? s_desktopShader : (s_desktopShader = Resources.Load<ComputeShader>("SparkUnityDesktop"));

    static ComputeShader MobileShader =>
        s_mobileShader != null ? s_mobileShader : (s_mobileShader = Resources.Load<ComputeShader>("SparkUnityMobile"));

    static bool IsDesktopFormat(SparkFormat format)
    {
        switch (format)
        {
            case SparkFormat.BC1_RGB:
            case SparkFormat.BC3_RGBA:
            case SparkFormat.BC4_R:
            case SparkFormat.BC5_RG:
            case SparkFormat.BC7_RGB:
            case SparkFormat.BC7_RGBA:
                return true;
            default:
                return false;
        }
    }

    // Formats whose encoded block fits in uint2 (8 bytes). Everything else uses uint4 (16 bytes).
    static bool IsSmallBlockFormat(SparkFormat format)
    {
        return format == SparkFormat.BC1_RGB
            || format == SparkFormat.BC4_R
            || format == SparkFormat.ETC2_RGB
            || format == SparkFormat.EAC_R;
    }

    static GraphicsFormat GetTemporaryOutputFormat(SparkFormat format)
    {
        return IsSmallBlockFormat(format)
            ? ((SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3) ? GraphicsFormat.R16G16B16A16_UInt : GraphicsFormat.R32G32_UInt)
            : GraphicsFormat.R32G32B32A32_UInt;
    }

    static string GetKernelName(SparkFormat format, SparkQuality quality)
    {
        int q = (int)quality;

        switch (format)
        {
            case SparkFormat.BC1_RGB:        return $"spark_encode_bc1_rgb_q{q}";
            case SparkFormat.BC3_RGBA:       return $"spark_encode_bc3_rgba_q{q}";
            //case SparkFormat.BC3_RGBM:       return $"spark_encode_bc3_rgbm_q{q}";
            case SparkFormat.BC4_R:          return $"spark_encode_bc4_r_q{(q < 2 ? 1 : 2)}";
            case SparkFormat.BC5_RG:         return $"spark_encode_bc5_rg_q{(q < 2 ? 1 : 2)}";
            case SparkFormat.BC7_RGB:        return $"spark_encode_bc7_rgb_q{q}";
            case SparkFormat.BC7_RGBA:       return $"spark_encode_bc7_rgba_q{q}";
            case SparkFormat.ASTC_4x4_RGB:   return $"spark_encode_astc_4x4_rgb_q{q}";
            case SparkFormat.ASTC_4x4_RGBA:  return $"spark_encode_astc_4x4_rgba_q{q}";
            //case SparkFormat.ASTC_4x4_RGBM:  return $"spark_encode_astc_4x4_rgbm_q{q}";
            case SparkFormat.ETC2_RGB:       return $"spark_encode_etc2_rgb_q{q}";
            case SparkFormat.ETC2_RGBA:      return $"spark_encode_etc2_rgba_q{q}";
            case SparkFormat.EAC_R:          return $"spark_encode_eac_r_q{q}";
            case SparkFormat.EAC_RG:         return $"spark_encode_eac_rg_q{q}";
            default: throw new ArgumentException($"Unknown format: {format}");
        }
    }

    /// <summary>
    /// Returns the GraphicsFormat for a given SparkFormat. Use this to create
    /// the destination Texture2D for the CommandBuffer overload of EncodeTexture.
    /// </summary>
    public static GraphicsFormat GetCompressedFormat(SparkFormat format, bool srgb)
    {
        switch (format)
        {
            case SparkFormat.BC1_RGB:
                return srgb ? GraphicsFormat.RGBA_DXT1_SRGB  : GraphicsFormat.RGBA_DXT1_UNorm;
            case SparkFormat.BC3_RGBA:
                return srgb ? GraphicsFormat.RGBA_DXT5_SRGB  : GraphicsFormat.RGBA_DXT5_UNorm;
            // case SparkFormat.BC3_RGBM:
            //     return GraphicsFormat.RGBA_DXT5_UNorm;
            case SparkFormat.BC4_R:
                return GraphicsFormat.R_BC4_UNorm;
            case SparkFormat.BC5_RG:
                return GraphicsFormat.RG_BC5_UNorm;
            case SparkFormat.BC7_RGB:
            case SparkFormat.BC7_RGBA:
                return srgb ? GraphicsFormat.RGBA_BC7_SRGB   : GraphicsFormat.RGBA_BC7_UNorm;
            case SparkFormat.ASTC_4x4_RGB:
            case SparkFormat.ASTC_4x4_RGBA:
                return srgb ? GraphicsFormat.RGBA_ASTC4X4_SRGB : GraphicsFormat.RGBA_ASTC4X4_UNorm;
            // case SparkFormat.BC3_RGBM:
            //     return GraphicsFormat.RGBA_ASTC4X4_UNorm;
            case SparkFormat.ETC2_RGB:
                return srgb ? GraphicsFormat.RGB_ETC2_SRGB   : GraphicsFormat.RGB_ETC2_UNorm;
            case SparkFormat.ETC2_RGBA:
                return srgb ? GraphicsFormat.RGBA_ETC2_SRGB : GraphicsFormat.RGBA_ETC2_UNorm;
            case SparkFormat.EAC_R:
                return GraphicsFormat.R_EAC_UNorm;
            case SparkFormat.EAC_RG:
                return GraphicsFormat.RG_EAC_UNorm;
            default:
                throw new ArgumentException($"Unknown format: {format}");
        }
    }


    // ───────────────────────────────────────────────
    //  Preload Helpers
    // ───────────────────────────────────────────────

    struct KernelEntry
    {
        public SparkFormat format;
        public ComputeShader shader;
        public int kernel;
    }

    static List<KernelEntry> CollectKernels(SparkQuality quality, SparkFormat[] formats)
    {
        var kernels = new List<KernelEntry>();
        var seen = new HashSet<string>();

        foreach (var raw in formats)
        {
            var format = ResolveFormat(raw);
            ComputeShader shader = IsDesktopFormat(format) ? DesktopShader : MobileShader;
            if (shader == null) continue;

            string kernelName = GetKernelName(format, quality);
            if (!seen.Add(kernelName)) continue;

            int kid = shader.FindKernel(kernelName);
            kernels.Add(new KernelEntry { format = format, shader = shader, kernel = kid });
        }

        return kernels;
    }

    static Texture2D s_warmupSrc;

    static void WarmupKernel(ComputeShader shader, int kernel, SparkFormat format)
    {
        // Lazy-create a tiny 4x4 source texture (one block).
        if (s_warmupSrc == null)
        {
            s_warmupSrc = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain:false);
            s_warmupSrc.name = "SparkWarmup";
        }

        GraphicsFormat outputFmt = GetTemporaryOutputFormat(format);
        var rt = GetOrCreateRT(1, 1, outputFmt);

        shader.SetTexture(kernel, "_Src", s_warmupSrc);
        shader.SetTexture(kernel, "_Dst", rt);
        shader.Dispatch(kernel, 1, 1, 1);
    }


    // ───────────────────────────────────────────────
    //  Render target cache
    // ───────────────────────────────────────────────

    // One cached RT per output uint format (small = R32G32_UInt, large = R32G32B32A32_UInt).
    static RenderTexture s_cachedSmallRT;  // 8-byte blocks
    static RenderTexture s_cachedLargeRT;  // 16-byte blocks

    // Note, Unity also has builtin render target caching: https://docs.unity3d.com/ScriptReference/Rendering.CommandBuffer.GetTemporaryRT.html
    // It's unclear to me whether it would make more sense to use it here instead of a custom cache.
    static RenderTexture GetOrCreateRT(int blockW, int blockH, GraphicsFormat outputFmt)
    {
        bool large = (outputFmt == GraphicsFormat.R32G32B32A32_UInt);
        RenderTexture cached = large ? s_cachedLargeRT : s_cachedSmallRT;

        if (cached != null && cached.width == blockW && cached.height == blockH)
            return cached;

        if (cached != null)
        {
            cached.Release();
            UnityEngine.Object.Destroy(cached);
        }

        cached = new RenderTexture(blockW, blockH, 0, outputFmt)
        {
            filterMode        = FilterMode.Point,
            enableRandomWrite = true,
        };
        cached.Create();

        if (large) s_cachedLargeRT = cached;
        else       s_cachedSmallRT = cached;

        return cached;
    }

    // ───────────────────────────────────────────────
    //  Constant buffers (for Android where inline constants are not supported)
    // ───────────────────────────────────────────────

    static ComputeBuffer s_astcConstantBuffer;

    static bool IsAstcFormat(SparkFormat format)
    {
        return format == SparkFormat.ASTC_4x4_RGB || format == SparkFormat.ASTC_4x4_RGBA;
    }

    static bool NeedsConstantBuffer(SparkFormat format)
    {
        if (!Application.isMobilePlatform) return false;
        return IsAstcFormat(format);
    }

    static ComputeBuffer GetConstantBuffer(SparkFormat format)
    {
        if (!NeedsConstantBuffer(format))
            return null;

        return GetAstcConstantBuffer();
    }

    static ComputeBuffer GetAstcConstantBuffer()
    {
        if (s_astcConstantBuffer != null)
            return s_astcConstantBuffer;

        // Layout matches tbuffer Tables in spark_astc.hlsli:
        //   uint integer_of_trits_q192[256]
        //   uint integer_of_trits_q48[256]
        //   uint integer_of_trits[256]
        //   uint integer_of_quints_q160[128]
        //   uint integer_of_quints_q80[128]
        // Total: 1024 uints = 4096 bytes
        uint[] data = new uint[1024];

        // integer_of_trits_q192[256] (243 values + 13 padding)
        uint[] tritsQ192 = {
            0x00000000u, 0x00000001u, 0x00000002u, 0x00000100u, 0x00000101u, 0x00000102u, 0x00000200u, 0x00000201u, 0x00000202u, 0x00010000u, 0x00010001u, 0x00010002u, 0x00010100u, 0x00010101u, 0x00010102u, 0x00010200u, 0x00010201u, 0x00010202u, 0x00000003u, 0x00000103u, 0x00000303u, 0x00010003u, 0x00010103u, 0x00010203u, 0x00000300u, 0x00000301u, 0x00000302u,
            0x00800000u, 0x00800001u, 0x00800002u, 0x00800100u, 0x00800101u, 0x00800102u, 0x00800200u, 0x00800201u, 0x00800202u, 0x00810000u, 0x00810001u, 0x00810002u, 0x00810100u, 0x00810101u, 0x00810102u, 0x00810200u, 0x00810201u, 0x00810202u, 0x00800003u, 0x00800103u, 0x00800303u, 0x00810003u, 0x00810103u, 0x00810203u, 0x00800300u, 0x00800301u, 0x00800302u,
            0x01000000u, 0x01000001u, 0x01000002u, 0x01000100u, 0x01000101u, 0x01000102u, 0x01000200u, 0x01000201u, 0x01000202u, 0x01010000u, 0x01010001u, 0x01010002u, 0x01010100u, 0x01010101u, 0x01010102u, 0x01010200u, 0x01010201u, 0x01010202u, 0x01000003u, 0x01000103u, 0x01000303u, 0x01010003u, 0x01010103u, 0x01010203u, 0x01000300u, 0x01000301u, 0x01000302u,
            0x80000000u, 0x80000001u, 0x80000002u, 0x80000100u, 0x80000101u, 0x80000102u, 0x80000200u, 0x80000201u, 0x80000202u, 0x80010000u, 0x80010001u, 0x80010002u, 0x80010100u, 0x80010101u, 0x80010102u, 0x80010200u, 0x80010201u, 0x80010202u, 0x80000003u, 0x80000103u, 0x80000303u, 0x80010003u, 0x80010103u, 0x80010203u, 0x80000300u, 0x80000301u, 0x80000302u,
            0x80800000u, 0x80800001u, 0x80800002u, 0x80800100u, 0x80800101u, 0x80800102u, 0x80800200u, 0x80800201u, 0x80800202u, 0x80810000u, 0x80810001u, 0x80810002u, 0x80810100u, 0x80810101u, 0x80810102u, 0x80810200u, 0x80810201u, 0x80810202u, 0x80800003u, 0x80800103u, 0x80800303u, 0x80810003u, 0x80810103u, 0x80810203u, 0x80800300u, 0x80800301u, 0x80800302u,
            0x81000000u, 0x81000001u, 0x81000002u, 0x81000100u, 0x81000101u, 0x81000102u, 0x81000200u, 0x81000201u, 0x81000202u, 0x81010000u, 0x81010001u, 0x81010002u, 0x81010100u, 0x81010101u, 0x81010102u, 0x81010200u, 0x81010201u, 0x81010202u, 0x81000003u, 0x81000103u, 0x81000303u, 0x81010003u, 0x81010103u, 0x81010203u, 0x81000300u, 0x81000301u, 0x81000302u,
            0x01800000u, 0x01800001u, 0x01800002u, 0x01800100u, 0x01800101u, 0x01800102u, 0x01800200u, 0x01800201u, 0x01800202u, 0x01810000u, 0x01810001u, 0x01810002u, 0x01810100u, 0x01810101u, 0x01810102u, 0x01810200u, 0x01810201u, 0x01810202u, 0x01800003u, 0x01800103u, 0x01800303u, 0x01810003u, 0x01810103u, 0x01810203u, 0x01800300u, 0x01800301u, 0x01800302u,
            0x81800000u, 0x81800001u, 0x81800002u, 0x81800100u, 0x81800101u, 0x81800102u, 0x81800200u, 0x81800201u, 0x81800202u, 0x81810000u, 0x81810001u, 0x81810002u, 0x81810100u, 0x81810101u, 0x81810102u, 0x81810200u, 0x81810201u, 0x81810202u, 0x81800003u, 0x81800103u, 0x81800303u, 0x81810003u, 0x81810103u, 0x81810203u, 0x81800300u, 0x81800301u, 0x81800302u,
            0x00010300u, 0x00010301u, 0x00010302u, 0x00810300u, 0x00810301u, 0x00810302u, 0x01010300u, 0x01010301u, 0x01010302u, 0x80010300u, 0x80010301u, 0x80010302u, 0x80810300u, 0x80810301u, 0x80810302u, 0x81010300u, 0x81010301u, 0x81010302u, 0x00010303u, 0x00810303u, 0x01810303u, 0x80010303u, 0x80810303u, 0x81810303u, 0x81810300u, 0x81810301u, 0x81810302u,
        };
        Array.Copy(tritsQ192, 0, data, 0, tritsQ192.Length);

        // integer_of_trits_q48[256] (offset 256)
        uint[] tritsQ48 = {
            0x00000000u, 0x00000010u, 0x00000020u, 0x00000400u, 0x00000410u, 0x00000420u, 0x00000800u, 0x00000810u, 0x00000820u, 0x00010000u, 0x00010010u, 0x00010020u, 0x00010400u, 0x00010410u, 0x00010420u, 0x00010800u, 0x00010810u, 0x00010820u, 0x00000030u, 0x00000430u, 0x00000C30u, 0x00010030u, 0x00010430u, 0x00010830u, 0x00000C00u, 0x00000C10u, 0x00000C20u,
            0x00200000u, 0x00200010u, 0x00200020u, 0x00200400u, 0x00200410u, 0x00200420u, 0x00200800u, 0x00200810u, 0x00200820u, 0x00210000u, 0x00210010u, 0x00210020u, 0x00210400u, 0x00210410u, 0x00210420u, 0x00210800u, 0x00210810u, 0x00210820u, 0x00200030u, 0x00200430u, 0x00200C30u, 0x00210030u, 0x00210430u, 0x00210830u, 0x00200C00u, 0x00200C10u, 0x00200C20u,
            0x00400000u, 0x00400010u, 0x00400020u, 0x00400400u, 0x00400410u, 0x00400420u, 0x00400800u, 0x00400810u, 0x00400820u, 0x00410000u, 0x00410010u, 0x00410020u, 0x00410400u, 0x00410410u, 0x00410420u, 0x00410800u, 0x00410810u, 0x00410820u, 0x00400030u, 0x00400430u, 0x00400C30u, 0x00410030u, 0x00410430u, 0x00410830u, 0x00400C00u, 0x00400C10u, 0x00400C20u,
            0x08000000u, 0x08000010u, 0x08000020u, 0x08000400u, 0x08000410u, 0x08000420u, 0x08000800u, 0x08000810u, 0x08000820u, 0x08010000u, 0x08010010u, 0x08010020u, 0x08010400u, 0x08010410u, 0x08010420u, 0x08010800u, 0x08010810u, 0x08010820u, 0x08000030u, 0x08000430u, 0x08000C30u, 0x08010030u, 0x08010430u, 0x08010830u, 0x08000C00u, 0x08000C10u, 0x08000C20u,
            0x08200000u, 0x08200010u, 0x08200020u, 0x08200400u, 0x08200410u, 0x08200420u, 0x08200800u, 0x08200810u, 0x08200820u, 0x08210000u, 0x08210010u, 0x08210020u, 0x08210400u, 0x08210410u, 0x08210420u, 0x08210800u, 0x08210810u, 0x08210820u, 0x08200030u, 0x08200430u, 0x08200C30u, 0x08210030u, 0x08210430u, 0x08210830u, 0x08200C00u, 0x08200C10u, 0x08200C20u,
            0x08400000u, 0x08400010u, 0x08400020u, 0x08400400u, 0x08400410u, 0x08400420u, 0x08400800u, 0x08400810u, 0x08400820u, 0x08410000u, 0x08410010u, 0x08410020u, 0x08410400u, 0x08410410u, 0x08410420u, 0x08410800u, 0x08410810u, 0x08410820u, 0x08400030u, 0x08400430u, 0x08400C30u, 0x08410030u, 0x08410430u, 0x08410830u, 0x08400C00u, 0x08400C10u, 0x08400C20u,
            0x00600000u, 0x00600010u, 0x00600020u, 0x00600400u, 0x00600410u, 0x00600420u, 0x00600800u, 0x00600810u, 0x00600820u, 0x00610000u, 0x00610010u, 0x00610020u, 0x00610400u, 0x00610410u, 0x00610420u, 0x00610800u, 0x00610810u, 0x00610820u, 0x00600030u, 0x00600430u, 0x00600C30u, 0x00610030u, 0x00610430u, 0x00610830u, 0x00600C00u, 0x00600C10u, 0x00600C20u,
            0x08600000u, 0x08600010u, 0x08600020u, 0x08600400u, 0x08600410u, 0x08600420u, 0x08600800u, 0x08600810u, 0x08600820u, 0x08610000u, 0x08610010u, 0x08610020u, 0x08610400u, 0x08610410u, 0x08610420u, 0x08610800u, 0x08610810u, 0x08610820u, 0x08600030u, 0x08600430u, 0x08600C30u, 0x08610030u, 0x08610430u, 0x08610830u, 0x08600C00u, 0x08600C10u, 0x08600C20u,
            0x00010C00u, 0x00010C10u, 0x00010C20u, 0x00210C00u, 0x00210C10u, 0x00210C20u, 0x00410C00u, 0x00410C10u, 0x00410C20u, 0x08010C00u, 0x08010C10u, 0x08010C20u, 0x08210C00u, 0x08210C10u, 0x08210C20u, 0x08410C00u, 0x08410C10u, 0x08410C20u, 0x00010C30u, 0x00210C30u, 0x00610C30u, 0x08010C30u, 0x08210C30u, 0x08610C30u, 0x08610C00u, 0x08610C10u, 0x08610C20u,
        };
        Array.Copy(tritsQ48, 0, data, 256, tritsQ48.Length);

        // integer_of_trits[256] (offset 512)
        uint[] trits = {
            0,   1,  2, 4,   5,  6, 8,   9, 10,  16, 17, 18, 20, 21, 22, 24, 25, 26,  3,   7, 15, 19, 23, 27, 12, 13, 14,
            32, 33, 34, 36, 37, 38, 40, 41, 42,  48, 49, 50, 52, 53, 54, 56, 57, 58,  35, 39, 47, 51, 55, 59, 44, 45, 46,
            64, 65, 66, 68, 69, 70, 72, 73, 74,  80, 81, 82, 84, 85, 86, 88, 89, 90,  67, 71, 79, 83, 87, 91, 76, 77, 78,
            128, 129, 130, 132, 133, 134, 136, 137, 138,  144, 145, 146, 148, 149, 150, 152, 153, 154,  131, 135, 143, 147, 151, 155, 140, 141, 142,
            160, 161, 162, 164, 165, 166, 168, 169, 170,  176, 177, 178, 180, 181, 182, 184, 185, 186,  163, 167, 175, 179, 183, 187, 172, 173, 174,
            192, 193, 194, 196, 197, 198, 200, 201, 202,  208, 209, 210, 212, 213, 214, 216, 217, 218,  195, 199, 207, 211, 215, 219, 204, 205, 206,
            96,   97,  98, 100, 101, 102, 104, 105, 106,  112, 113, 114, 116, 117, 118, 120, 121, 122,   99, 103, 111, 115, 119, 123, 108, 109, 110,
            224, 225, 226, 228, 229, 230, 232, 233, 234,  240, 241, 242, 244, 245, 246, 248, 249, 250,  227, 231, 239, 243, 247, 251, 236, 237, 238,
            28,   29,  30,  60,  61,  62,  92,  93,  94,  156, 157, 158, 188, 189, 190, 220, 221, 222,   31,  63, 127, 159, 191, 255, 252, 253, 254,
        };
        Array.Copy(trits, 0, data, 512, trits.Length);

        // integer_of_quints_q160[128] (offset 768)
        uint[] quintsQ160 = {
            0x00000000u, 0x00000020u, 0x00000040u, 0x00000060u, 0x00000080u, 0x00002000u, 0x00002020u, 0x00002040u, 0x00002060u, 0x00002080u, 0x00004000u, 0x00004020u, 0x00004040u, 0x00004060u, 0x00004080u, 0x00006000u, 0x00006020u, 0x00006040u, 0x00006060u, 0x00006080u, 0x000000A0u, 0x000020A0u, 0x000040A0u, 0x000060A0u, 0x000000C0u,
            0x00100000u, 0x00100020u, 0x00100040u, 0x00100060u, 0x00100080u, 0x00102000u, 0x00102020u, 0x00102040u, 0x00102060u, 0x00102080u, 0x00104000u, 0x00104020u, 0x00104040u, 0x00104060u, 0x00104080u, 0x00106000u, 0x00106020u, 0x00106040u, 0x00106060u, 0x00106080u, 0x001000A0u, 0x001020A0u, 0x001040A0u, 0x001060A0u, 0x000020C0u,
            0x00200000u, 0x00200020u, 0x00200040u, 0x00200060u, 0x00200080u, 0x00202000u, 0x00202020u, 0x00202040u, 0x00202060u, 0x00202080u, 0x00204000u, 0x00204020u, 0x00204040u, 0x00204060u, 0x00204080u, 0x00206000u, 0x00206020u, 0x00206040u, 0x00206060u, 0x00206080u, 0x002000A0u, 0x002020A0u, 0x002040A0u, 0x002060A0u, 0x000040C0u,
            0x00300000u, 0x00300020u, 0x00300040u, 0x00300060u, 0x00300080u, 0x00302000u, 0x00302020u, 0x00302040u, 0x00302060u, 0x00302080u, 0x00304000u, 0x00304020u, 0x00304040u, 0x00304060u, 0x00304080u, 0x00306000u, 0x00306020u, 0x00306040u, 0x00306060u, 0x00306080u, 0x003000A0u, 0x003020A0u, 0x003040A0u, 0x003060A0u, 0x000060C0u,
            0x003000C0u, 0x003000E0u, 0x002000C0u, 0x002000E0u, 0x001000C0u, 0x003020C0u, 0x003020E0u, 0x002020C0u, 0x002020E0u, 0x001020C0u, 0x003040C0u, 0x003040E0u, 0x002040C0u, 0x002040E0u, 0x001040C0u, 0x003060C0u, 0x003060E0u, 0x002060C0u, 0x002060E0u, 0x001060C0u, 0x001000E0u, 0x001020E0u, 0x001040E0u, 0x001060E0u, 0x000060E0u,
            0, 0, 0, // padding
        };
        Array.Copy(quintsQ160, 0, data, 768, quintsQ160.Length);

        // integer_of_quints_q80[128] (offset 896)
        uint[] quintsQ80 = {
            0x00000000u, 0x00000010u, 0x00000020u, 0x00000030u, 0x00000040u, 0x00000800u, 0x00000810u, 0x00000820u, 0x00000830u, 0x00000840u, 0x00001000u, 0x00001010u, 0x00001020u, 0x00001030u, 0x00001040u, 0x00001800u, 0x00001810u, 0x00001820u, 0x00001830u, 0x00001840u, 0x00000050u, 0x00000850u, 0x00001050u, 0x00001850u, 0x00000060u,
            0x00020000u, 0x00020010u, 0x00020020u, 0x00020030u, 0x00020040u, 0x00020800u, 0x00020810u, 0x00020820u, 0x00020830u, 0x00020840u, 0x00021000u, 0x00021010u, 0x00021020u, 0x00021030u, 0x00021040u, 0x00021800u, 0x00021810u, 0x00021820u, 0x00021830u, 0x00021840u, 0x00020050u, 0x00020850u, 0x00021050u, 0x00021850u, 0x00000860u,
            0x00040000u, 0x00040010u, 0x00040020u, 0x00040030u, 0x00040040u, 0x00040800u, 0x00040810u, 0x00040820u, 0x00040830u, 0x00040840u, 0x00041000u, 0x00041010u, 0x00041020u, 0x00041030u, 0x00041040u, 0x00041800u, 0x00041810u, 0x00041820u, 0x00041830u, 0x00041840u, 0x00040050u, 0x00040850u, 0x00041050u, 0x00041850u, 0x00001060u,
            0x00060000u, 0x00060010u, 0x00060020u, 0x00060030u, 0x00060040u, 0x00060800u, 0x00060810u, 0x00060820u, 0x00060830u, 0x00060840u, 0x00061000u, 0x00061010u, 0x00061020u, 0x00061030u, 0x00061040u, 0x00061800u, 0x00061810u, 0x00061820u, 0x00061830u, 0x00061840u, 0x00060050u, 0x00060850u, 0x00061050u, 0x00061850u, 0x00001860u,
            0x00060060u, 0x00060070u, 0x00040060u, 0x00040070u, 0x00020060u, 0x00060860u, 0x00060870u, 0x00040860u, 0x00040870u, 0x00020860u, 0x00061060u, 0x00061070u, 0x00041060u, 0x00041070u, 0x00021060u, 0x00061860u, 0x00061870u, 0x00041860u, 0x00041870u, 0x00021860u, 0x00020070u, 0x00020870u, 0x00021070u, 0x00021870u, 0x00001870u,
            0, 0, 0, // padding
        };
        Array.Copy(quintsQ80, 0, data, 896, quintsQ80.Length);

        s_astcConstantBuffer = new ComputeBuffer(1024, sizeof(uint), ComputeBufferType.Constant);
        s_astcConstantBuffer.SetData(data);
        return s_astcConstantBuffer;
    }

    // ───────────────────────────────────────────────
    //  Generic format resolution
    // ───────────────────────────────────────────────

    // Cached resolved formats — computed once on first access.
    static SparkFormat s_resolvedR;
    static SparkFormat s_resolvedRG;
    static SparkFormat s_resolvedRGB;
    static SparkFormat s_resolvedRGBA;
    static bool        s_formatsResolved;

    static bool IsGenericFormat(SparkFormat format)
    {
        switch (format)
        {
            case SparkFormat.R:
            case SparkFormat.RG:
            case SparkFormat.RGB:
            case SparkFormat.RGBA:
                return true;
            default:
                return false;
        }
    }

    static void EnsureFormatsResolved()
    {
        if (s_formatsResolved) return;
        s_resolvedR    = PickFirstSupported(SparkFormat.BC4_R,    SparkFormat.EAC_R);
        s_resolvedRG   = PickFirstSupported(SparkFormat.BC5_RG,   SparkFormat.EAC_RG);
        s_resolvedRGB  = PickFirstSupported(SparkFormat.BC7_RGB,  SparkFormat.ASTC_4x4_RGB, SparkFormat.BC1_RGB, SparkFormat.ETC2_RGB);
        s_resolvedRGBA = PickFirstSupported(SparkFormat.BC7_RGBA, SparkFormat.ASTC_4x4_RGBA, SparkFormat.BC3_RGBA, SparkFormat.ETC2_RGBA);
        s_formatsResolved = true;
    }

    static SparkFormat PickFirstSupported(params SparkFormat[] candidates)
    {
        foreach (var fmt in candidates)
            if (IsFormatSupported(fmt))
                return fmt;
        // Fallback to first candidate even if unsupported — EncodeTexture will
        // produce a diagnostic error via Graphics.CopyTexture if truly unusable.
        return candidates[0];
    }

    public static SparkFormat ResolveFormat(SparkFormat format)
    {
        switch (format)
        {
            case SparkFormat.R:    EnsureFormatsResolved(); return s_resolvedR;
            case SparkFormat.RG:   EnsureFormatsResolved(); return s_resolvedRG;
            case SparkFormat.RGB:  EnsureFormatsResolved(); return s_resolvedRGB;
            case SparkFormat.RGBA: EnsureFormatsResolved(); return s_resolvedRGBA;
            default: return format;
        }
    }
}
