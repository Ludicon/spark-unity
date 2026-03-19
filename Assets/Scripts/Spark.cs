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
    /// Caller is responsible for destroying the returned texture when no longer needed.
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
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        // Resolve generic formats to concrete.
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
        GraphicsFormat outputFmt = IsSmallBlockFormat(format)
            ? GraphicsFormat.R32G32_UInt
            : GraphicsFormat.R32G32B32A32_UInt;

        var rt = GetOrCreateRT(blockW, blockH, outputFmt);

        // Create the compressed texture and copy entirely on the GPU.
        // The uint RT pixels map 1:1 to compressed blocks (same byte layout).
        GraphicsFormat compressedFmt = GetCompressedFormat(format, srgb);
        var result = new Texture2D(width, height, compressedFmt, TextureCreationFlags.None);


        int groupsX = (blockW + 15) / 16;
        int groupsY = (blockH + 15) / 16;

        // Dispatch via CommandBuffer so we get GPU profiler samples.
        var cmd = GetCommandBuffer();
        cmd.Clear();
        cmd.BeginSample(GpuSampleName);
        cmd.SetComputeTextureParam(shader, kernel, "_Src", source);
        cmd.SetComputeTextureParam(shader, kernel, "_Dst", rt);
        cmd.DispatchCompute(shader, kernel, groupsX, groupsY, 1);
        cmd.CopyTexture(rt, 0, 0, result, 0, 0);
        cmd.EndSample(GpuSampleName);
        Graphics.ExecuteCommandBuffer(cmd);

        //Graphics.CopyTexture(rt, 0, 0, result, 0, 0);
        return result;
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
            WarmupKernel(entry.shader, entry.kernel, entry.small);
    }

    /// <summary>
    /// Coroutine that preloads compute shader kernels spread across frames (one kernel per frame).
    /// Use with StartCoroutine(Spark.PreloadAsync(SparkQuality.Medium, SparkFormat.RGB, SparkFormat.RGBA)).
    /// </summary>
    public static IEnumerator PreloadAsync(SparkQuality quality, params SparkFormat[] formats)
    {
        foreach (var entry in CollectKernels(quality, formats))
        {
            WarmupKernel(entry.shader, entry.kernel, entry.small);
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

    static GraphicsFormat GetCompressedFormat(SparkFormat format, bool srgb)
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
        public ComputeShader shader;
        public int kernel;
        public bool small;
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

            bool small = IsSmallBlockFormat(format);

            string kernelName = GetKernelName(format, quality);
            if (!seen.Add(kernelName)) continue;

            int kid = shader.FindKernel(kernelName);
            kernels.Add(new KernelEntry { shader = shader, kernel = kid, small = small });
        }

        return kernels;
    }

    static Texture2D s_warmupSrc;

    static void WarmupKernel(ComputeShader shader, int kernel, bool small)
    {
        // Lazy-create a tiny 4x4 source texture (one block).
        if (s_warmupSrc == null)
        {
            s_warmupSrc = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            s_warmupSrc.name = "SparkWarmup";
        }

        GraphicsFormat outputFmt = small ? GraphicsFormat.R32G32_UInt : GraphicsFormat.R32G32B32A32_UInt;
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

    static RenderTexture GetOrCreateRT(int blockW, int blockH, GraphicsFormat outputFmt)
    {
        bool small = (outputFmt == GraphicsFormat.R32G32_UInt);
        RenderTexture cached = small ? s_cachedSmallRT : s_cachedLargeRT;

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

        if (small) s_cachedSmallRT = cached;
        else       s_cachedLargeRT = cached;

        return cached;
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
