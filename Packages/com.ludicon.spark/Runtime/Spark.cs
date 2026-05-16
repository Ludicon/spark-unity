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
    // Generic - auto-resolved to the best supported format on the current GPU
    R,              // Single channel -> BC4_R or EAC_R
    RG,             // Two channels   -> BC5_RG or EAC_RG
    RGB,            // Color          -> BC7_RGB or ASTC_4x4_RGB
    RGBA,           // Color + alpha  -> BC7_RGBA or ASTC_4x4_RGBA

    // Desktop formats
    BC1_RGB,        // RGB, 4 bpp
    BC4_R,          // Single channel (R), 4 bpp
    BC5_RG,         // Two channels (RG), 8 bpp
    BC7_RGB,        // High-quality RGB, 8 bpp
    BC7_RGBA,       // High-quality RGBA, 8 bpp

    // Mobile formats
    ETC2_RGB,       // RGB, 4 bpp
    EAC_R,          // Single channel (R), 4 bpp
    EAC_RG,         // Two channels (RG), 8 bpp
    ASTC_4x4_RGB,   // RGB, 8 bpp
    ASTC_4x4_RGBA,  // RGBA, 8 bpp
}


/// <summary>
/// GPU texture compression using Spark codecs.
///
///   // One-liner
///   Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.BC7_RGB);
///
///   // With options
///   Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.ASTC_4x4_RGB, srgb: true);
///
///   // Auto-select best format for current GPU
///   Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB);
///
///   // Preload shaders to avoid first-encode hitch
///   Spark.Preload(SparkFormat.RGB, SparkFormat.RGBA);
///
///   // Or spread across frames via coroutine:
///   StartCoroutine(Spark.PreloadAsync(SparkFormat.RGB, SparkFormat.RGBA));
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
    /// <param name="source">Source texture. If the source has a mip chain, the generated texture also has mips.</param>
    /// <param name="format">Target compressed format (concrete or generic).</param>
    /// <param name="srgb">If true, the output texture uses an sRGB format.</param>
    public static Texture2D EncodeTexture(Texture source, SparkFormat format, bool srgb = false)
    {
        format = ResolveFormat(format);

        // Do not encode mips smaller than 4 pixels per side.
        int targetMipCount = ComputeMipCount(source.width, source.height);
        int mipCount = Math.Min(source.mipmapCount, targetMipCount);

        GraphicsFormat compressedFmt = GetCompressedFormat(format, srgb);
        var result = new Texture2D(source.width, source.height, compressedFmt, mipCount,
            TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);

        var cmd = GetCommandBuffer();
        cmd.Clear();
        cmd.BeginSample(GpuSampleName);
        for (int mip = 0; mip < mipCount; mip++)
            EncodeTexture(cmd, source, result, format, mip, mip);
        cmd.EndSample(GpuSampleName);
        Graphics.ExecuteCommandBuffer(cmd);

        return result;
    }

    // Largest n such that every mip 0..n-1 has both dimensions >= 4 (one BC/ASTC block).
    static int ComputeMipCount(int w, int h)
    {
        int n = 0;
        while (w >= 4 && h >= 4) { n++; w >>= 1; h >>= 1; }
        return Math.Max(1, n);
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
    /// <param name="sourceMip">Mip level of <paramref name="source"/> to sample (default 0).</param>
    /// <param name="destMip">Mip level of <paramref name="destination"/> to write into (default 0).</param>
    public static void EncodeTexture(CommandBuffer cmd, Texture source, Texture destination, SparkFormat format, int sourceMip = 0, int destMip = 0)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (cmd == null)
            throw new ArgumentNullException(nameof(cmd));
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));

        format = ResolveFormat(format);

        int width  = Math.Max(1, source.width  >> sourceMip);
        int height = Math.Max(1, source.height >> sourceMip);

        // Resolve shader and kernel.
        ComputeShader shader = Shader;
        if (shader == null)
            throw new InvalidOperationException($"Could not load compute shader. Make sure SparkUnity is in a Resources folder.");

        string kernelName = GetKernelName(format);
        int kernel = shader.FindKernel(kernelName);

        int blockW = (width  + 3) / 4;
        int blockH = (height + 3) / 4;

        // Get or reuse cached render texture sized in blocks. The cache grows to the high-water mark
        // so a mip loop (largest -> smallest) reuses the same RT for every level.
        GraphicsFormat outputFmt = GetTemporaryOutputFormat(format);
        var rt = GetOrCreateRT(blockW, blockH, outputFmt);

        int groupsX = (blockW + 15) / 16;
        int groupsY = (blockH + 7) / 8;

        // This really sucks. It appears that Unity doesn't have a way to bind a specific mip level
        // of a Texture2D or RenderTexture as an SRV; the mipLevel parameter only affects UAV
        // bindings (RWTexture2D). So for sourceMip > 0 we copy that mip into a single-level scratch
        // RT and bind that. Another alternative would be to use a shader that uses textureFetch()
        // instead of gather(), but that would require an additional shader variant..
        bool needsScratch = sourceMip > 0;
        if (needsScratch)
        {
            cmd.GetTemporaryRT(s_srcMipCopyId, width, height, 0, FilterMode.Point, source.graphicsFormat, 1);
            cmd.CopyTexture(source, 0, sourceMip, s_srcMipCopyId, 0, 0);
            cmd.SetComputeTextureParam(shader, kernel, "_Src", s_srcMipCopyId);
        }
        else
        {
            cmd.SetComputeTextureParam(shader, kernel, "_Src", source);
        }
        cmd.SetComputeTextureParam(shader, kernel, "_Dst", rt);
        cmd.SetComputeIntParams(shader, "_SrcSize", width, height);
        cmd.DispatchCompute(shader, kernel, groupsX, groupsY, 1);
        cmd.CopyTexture(rt, 0, 0, 0, 0, blockW, blockH, destination, 0, destMip, 0, 0);

        if (needsScratch)
            cmd.ReleaseTemporaryRT(s_srcMipCopyId);
    }

    static readonly int s_srcMipCopyId = UnityEngine.Shader.PropertyToID("_SparkSrcMipCopy");

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
    /// Preload (warm up) compute shader kernels for the given formats.
    /// Forces the GPU driver to compile each kernel by issuing a tiny 4x4 dummy dispatch.
    /// Generic formats are resolved before warmup.
    /// </summary>
    public static void Preload(params SparkFormat[] formats)
    {
        foreach (var entry in CollectKernels(formats))
            WarmupKernel(entry.shader, entry.kernel, entry.format);
    }

    /// <summary>
    /// Coroutine that preloads compute shader kernels spread across frames (one kernel per frame).
    /// Use with StartCoroutine(Spark.PreloadAsync(SparkFormat.RGB, SparkFormat.RGBA)).
    /// </summary>
    public static IEnumerator PreloadAsync(params SparkFormat[] formats)
    {
        foreach (var entry in CollectKernels(formats))
        {
            WarmupKernel(entry.shader, entry.kernel, entry.format);
            yield return null;
        }
    }


    // ───────────────────────────────────────────────
    //  Compute Shaders
    // ───────────────────────────────────────────────

    static ComputeShader s_Shader;

    static ComputeShader Shader =>
        s_Shader != null ? s_Shader : (s_Shader = Resources.Load<ComputeShader>("SparkUnity"));

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

    static string GetKernelName(SparkFormat format)
    {
        switch (format)
        {
            case SparkFormat.BC1_RGB:        return $"kernel_spark_encode_bc1_rgb_q2";
            case SparkFormat.BC4_R:          return $"kernel_spark_encode_bc4_r_q2";
            case SparkFormat.BC5_RG:         return $"kernel_spark_encode_bc5_rg_q2";
            case SparkFormat.BC7_RGB:        return $"kernel_spark_encode_bc7_rgb_q2";
            case SparkFormat.BC7_RGBA:       return $"kernel_spark_encode_bc7_rgba_q2";
            case SparkFormat.ASTC_4x4_RGB:   return $"kernel_spark_encode_astc_4x4_rgb_q2";
            case SparkFormat.ASTC_4x4_RGBA:  return $"kernel_spark_encode_astc_4x4_rgba_q2";
            case SparkFormat.ETC2_RGB:       return $"kernel_spark_encode_etc2_rgb_q1";
            case SparkFormat.EAC_R:          return $"kernel_spark_encode_eac_r_q0";
            case SparkFormat.EAC_RG:         return $"kernel_spark_encode_eac_rg_q0";
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
            case SparkFormat.ETC2_RGB:
                return srgb ? GraphicsFormat.RGB_ETC2_SRGB   : GraphicsFormat.RGB_ETC2_UNorm;
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

    static List<KernelEntry> CollectKernels(SparkFormat[] formats)
    {
        var kernels = new List<KernelEntry>();
        var seen = new HashSet<string>();

        foreach (var raw in formats)
        {
            var format = ResolveFormat(raw);
            ComputeShader shader = Shader;
            if (shader == null) continue;

            string kernelName = GetKernelName(format);
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

        if (cached != null && cached.width >= blockW && cached.height >= blockH)
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
        s_resolvedRGBA = PickFirstSupported(SparkFormat.BC7_RGBA, SparkFormat.ASTC_4x4_RGBA);
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
