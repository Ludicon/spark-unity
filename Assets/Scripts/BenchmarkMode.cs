using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// Benchmark mode: encodes a 1024×1024 texture in a tight loop and reports throughput per format.
public class BenchmarkMode : SparkDemoMode
{
    public override string DisplayName => "Benchmark";

    [Tooltip("Edge length of the synthetic fallback texture, used only when a preferred PBR "
           + "source for a format isn't present in StreamingAssets.")]
    public int textureSize = 1024;

    [Tooltip("How many back-to-back encodes the initial calibration batch performs.")]
    public int initialDispatchCount = 100;

    [Tooltip("Target wall-clock duration (in seconds) for each individual measurement run. "
           + "After calibration, the dispatch count is scaled so each run lands close to this.")]
    public float perRunSeconds = 0.2f;

    [Tooltip("How many measurement runs to perform per format. The best (fastest) is reported.")]
    public int measurementRuns = 10;

    static readonly SparkFormat[] s_formats =
    {
        SparkFormat.RGBA,
        SparkFormat.RGB,
        SparkFormat.RG,
        SparkFormat.R,
    };

    // Real-world source texture filename (no extension) preferred for each format. Looked up by
    // case-insensitive name match against Controller.SourceTextures. Missing entries fall back
    // to the synthetic deterministic-noise texture below.
    static readonly Dictionary<SparkFormat, string> s_preferredSources = new Dictionary<SparkFormat, string>
    {
        { SparkFormat.RGBA, "quadTexture_rgba"            },
        { SparkFormat.RGB,  "Material.003_Base_color"     },
        { SparkFormat.RG,   "Material.003_Normal_OpenGL"  },
        { SparkFormat.R,    "Material.003_Roughness"      },
    };

    Texture2D _syntheticSource;   // lazy fallback when a preferred source isn't loaded.
    RenderTexture _syncRT;

    bool _running;
    string _status = "Press Run to start.";
    readonly List<string> _results = new List<string>();
    Coroutine _coroutine;

    public override void Activate()
    {
        Spark.Preload(s_formats);
        EnsureSyncRT();
    }

    /// <summary>Return the per-format source texture if present in
    /// <c>Controller.SourceTextures</c>, otherwise the synthetic deterministic-noise fallback.</summary>
    Texture2D GetSource(SparkFormat format)
    {
        if (s_preferredSources.TryGetValue(format, out string preferred))
        {
            foreach (var tex in Controller.SourceTextures)
            {
                if (tex != null && string.Equals(tex.name, preferred, System.StringComparison.OrdinalIgnoreCase))
                    return tex;
            }
        }
        return EnsureSyntheticSource();
    }

    public override void Deactivate()
    {
        if (_coroutine != null)
        {
            StopCoroutine(_coroutine);
            _coroutine = null;
        }
        _running = false;
    }

    void OnDestroy()
    {
        if (_syntheticSource != null) Destroy(_syntheticSource);
        if (_syncRT != null) { _syncRT.Release(); Destroy(_syncRT); }
    }

    Texture2D EnsureSyntheticSource()
    {
        if (_syntheticSource != null) return _syntheticSource;

        _syntheticSource = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, mipChain: false);
        _syntheticSource.name = "BenchmarkSyntheticSource";

        var data = new Color32[textureSize * textureSize];
        var rng = new System.Random(42);
        for (int i = 0; i < data.Length; i++)
            data[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        _syntheticSource.SetPixels32(data);
        _syntheticSource.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return _syntheticSource;
    }

    void EnsureSyncRT()
    {
        if (_syncRT != null) return;
        _syncRT = new RenderTexture(1, 1, 0, GraphicsFormat.R8G8B8A8_UNorm);
        _syncRT.Create();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  No OnGUIBackground — the mode is content-free. Foreground draws the UI.
    // ────────────────────────────────────────────────────────────────────────

    public override void OnGUIForeground(Rect bounds)
    {
        float pad = 12f;
        float w = bounds.width - pad * 2;
        float x = bounds.x + pad;
        float y = bounds.y + pad;

        // Device info — useful context for benchmark comparison across machines.
        var info = DeviceInfo();
        foreach (var line in info)
        {
            GUI.Label(new Rect(x, y, w, 18), line, BodyStyle());
            y += 18;
        }
        y += 6;

        GUI.Label(new Rect(x, y, w, 20),
            $"{measurementRuns} runs × {perRunSeconds * 1000:F0} ms (per-format source size shown in results)",
            BodyStyle());
        y += 26;

        if (!_running)
        {
            if (GUI.Button(new Rect(x, y, 160, 28), _results.Count == 0 ? "Run Benchmark" : "Run Again"))
            {
                _coroutine = StartCoroutine(RunBenchmarkCoroutine());
            }
            y += 36;
        }
        else
        {
            //GUI.Label(new Rect(x, y, w, 24), $"<b>{_status}</b>", BodyStyle());
            y += 30;
        }

        // Results listing — monospace so the columns line up.
        foreach (var line in _results)
        {
            GUI.Label(new Rect(x, y, w, 20), line, MonoStyle());
            y += 20;
        }

        if (!_running && _results.Count > 0)
        {
            y += 8;
            GUI.Label(new Rect(x, y, w, 20),
                "Numbers include driver overhead + GPU encode + the implicit GPU-drain readback.",
                FadedStyle());
        }
    }

    IEnumerator RunBenchmarkCoroutine()
    {
        _running = true;
        _results.Clear();

        for (int i = 0; i < s_formats.Length; i++)
        {
            yield return RunOneCoroutine(i, s_formats[i]);
        }

        _status = "Done.";
        _running = false;
        _coroutine = null;
    }

    IEnumerator RunOneCoroutine(int formatIndex, SparkFormat requested)
    {
        string label = $"[{formatIndex + 1}/{s_formats.Length}] {requested}";

        if (!Spark.IsFormatSupported(requested))
        {
            _results.Add($"{requested,-5} → unsupported on this device");
            yield break;
        }

        Texture2D src = GetSource(requested);
        int srcW = src.width;
        int srcH = src.height;

        var resolved = Spark.ResolveFormat(requested);
        var compressedFmt = Spark.GetCompressedFormat(resolved, srgb: true);
        Texture2D dst = new Texture2D(srcW, srcH, compressedFmt, mipCount: 1,
            TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);

        // Use this code to only measure the dispatch without the output copy.
        // var outputFmt = Spark.GetTemporaryOutputFormat(resolved);
        // RenderTexture dst = new RenderTexture((textureSize + 3) / 4, (textureSize + 3) / 4, 1, outputFmt)
        // {
        //     enableRandomWrite = true,
        // };

        // ── Calibration ── pick a dispatch count that lands each measured run near
        // perRunSeconds. The calibration run also serves as a warmup pass so the driver
        // settles into steady-state before any of the timed runs.
        _status = $"{label} — calibrating";
        yield return null;

        int count;
        try
        {
            double calSeconds = RunBatch(src, dst, requested, initialDispatchCount);
            count = (calSeconds > 0.001)
                ? Mathf.Clamp(
                    Mathf.CeilToInt(initialDispatchCount * (float)(perRunSeconds / calSeconds)),
                    Mathf.Max(1, initialDispatchCount / 10),
                    50_000)
                : initialDispatchCount;
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            _results.Add($"{requested,-5} → error: {e.Message}");
            Destroy(dst);
            yield break;
        }

        // ── Measurement runs ── execute N runs at the calibrated count. Track BEST as the
        // steady-state throughput proxy (slower runs reflect transient stalls from vsync,
        // OS scheduling, or thermal throttling, not the encoder's intrinsic cost). The mean
        // is also reported for context.
        double bestSeconds = double.MaxValue;
        double sumSeconds = 0;
        bool errored = false;

        for (int run = 0; run < measurementRuns; run++)
        {
            _status = $"{label} — run {run + 1}/{measurementRuns}";
            yield return null;

            try
            {
                double s = RunBatch(src, dst, requested, count);
                if (s < bestSeconds) bestSeconds = s;
                sumSeconds += s;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                _results.Add($"{requested,-5} → error in run {run + 1}: {e.Message}");
                errored = true;
                break;
            }
        }

        Destroy(dst);
        if (errored) yield break;

        double meanSeconds = sumSeconds / measurementRuns;
        double msPerTexBest = bestSeconds * 1000.0 / count;
        double mpixPerSec   = (double)srcW * srcH * count / bestSeconds / 1_000_000.0;
        double msPerTexMean = meanSeconds * 1000.0 / count;

        string srcLabel = $"{src.name} {srcW}×{srcH}";
        var msg = $"{resolved,-15} {srcLabel,-40} best: {msPerTexBest,6:F2} ms ({mpixPerSec,5:F0} MPix/s)  mean: {msPerTexMean,6:F2} ms";
        Debug.Log(msg);
        _results.Add(msg);
        yield return null;
    }

    /// <summary>Submit a CommandBuffer with <paramref name="count"/> back-to-back encodes plus
    /// a 1×1 blit as a sync point, then wait for the GPU to drain via AsyncGPUReadback. Returns
    /// the wall-clock seconds elapsed.</summary>
    double RunBatch(Texture src, Texture dst, SparkFormat format, int count)
    {
        var cmd = new CommandBuffer { name = $"Benchmark {format}" };
        for (int i = 0; i < count; i++)
            Spark.EncodeTexture(cmd, src, dst, format);
        // Force the GPU to finish all queued encodes before we time the elapsed wall clock.
        cmd.Blit(Texture2D.whiteTexture, _syncRT);

        var sw = Stopwatch.StartNew();
        Graphics.ExecuteCommandBuffer(cmd);
        AsyncGPUReadback.Request(_syncRT).WaitForCompletion();
        sw.Stop();

        cmd.Release();
        return sw.Elapsed.TotalSeconds;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Device info
    // ────────────────────────────────────────────────────────────────────────

    static string[] s_deviceInfo;

    static string[] DeviceInfo()
    {
        if (s_deviceInfo != null) return s_deviceInfo;

        string vendor = string.IsNullOrEmpty(SystemInfo.graphicsDeviceVendor) ? "?" : SystemInfo.graphicsDeviceVendor;
        string name   = string.IsNullOrEmpty(SystemInfo.graphicsDeviceName)   ? "?" : SystemInfo.graphicsDeviceName;
        string ver    = string.IsNullOrEmpty(SystemInfo.graphicsDeviceVersion)? "?" : SystemInfo.graphicsDeviceVersion;
        var    api    = SystemInfo.graphicsDeviceType;
        long   vram   = SystemInfo.graphicsMemorySize;    // MB

        string n16 = ProbeNative16Bit();

        s_deviceInfo = new[]
        {
            $"<b>Device:</b> {vendor} {name}",
            $"<b>API:</b> {api}   <b>Driver:</b> {ver}",
            $"<b>VRAM:</b> {vram} MB   <b>Native16Bit:</b> {n16}",
        };
        return s_deviceInfo;
    }

    /// <summary>Determine native-16-bit support by dispatching the Native16BitProbe compute
    /// shader. Unity's multi_compile picks the variant matching the device's reported
    /// capability, so the value the kernel writes is Unity's own verdict — the same one used
    /// to gate kernels with <c>#pragma require Native16Bit</c>.</summary>
    static string ProbeNative16Bit()
    {
        try
        {
            var probe = Resources.Load<ComputeShader>("Native16BitProbe");
            if (probe == null) return "<color=#999>unknown</color>";

            int kernel = probe.FindKernel("Probe");
            if (kernel < 0) return "<color=#999>unknown</color>";

            var buf = new ComputeBuffer(1, sizeof(uint));
            try
            {
                buf.SetData(new uint[] { 0xFFFFFFFFu });   // sentinel — different from both 0 and 1
                probe.SetBuffer(kernel, "_Result", buf);
                probe.Dispatch(kernel, 1, 1, 1);

                uint[] result = new uint[1];
                buf.GetData(result);
                if (result[0] > 1) return "<color=#999>unknown</color>";   // shader didn't actually run
                return result[0] != 0 ? "<color=#7f7>yes</color>" : "<color=#f77>no</color>";
            }
            finally
            {
                buf.Release();
            }
        }
        catch (System.Exception)
        {
            return "<color=#999>unknown</color>";
        }
    }

    static GUIStyle s_title, s_body, s_faded, s_mono;
    static Font s_monoFont;

    static GUIStyle TitleStyle() { return s_title ?? (s_title = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16 }); }
    static GUIStyle BodyStyle()  { return s_body  ?? (s_body  = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 }); }
    static GUIStyle FadedStyle() { return s_faded ?? (s_faded = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11, normal = { textColor = new Color(0.7f, 0.7f, 0.7f) } }); }

    /// <summary>Monospace label style. Probes the OS installed-font list and uses the first
    /// monospace font that's actually present. Falls back to the default proportional font if
    /// none of the candidates is installed — alignment won't be perfect but rendering still
    /// works. (Unity 6's <c>CreateDynamicFontFromOSFont(string[])</c> path doesn't reliably
    /// skip missing fonts in the candidate list — it tries the first name and emits "Unable
    /// to find a font file" warnings on Android — so we filter against
    /// <c>GetOSInstalledFontNames</c> ourselves.)</summary>
    static GUIStyle MonoStyle()
    {
        if (s_mono != null) return s_mono;
        if (!s_monoChecked)
        {
            s_monoChecked = true;
            s_monoFont = FindMonospaceFont();
        }
        s_mono = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
        if (s_monoFont != null) s_mono.font = s_monoFont;
        return s_mono;
    }

    static bool s_monoChecked;

    static Font FindMonospaceFont()
    {
        // Candidates ordered from most-likely-good (modern desktop/iOS) to fallback.
        string[] candidates =
        {
            "Menlo",              // macOS, iOS
            "Consolas",           // Windows 7+
            "Courier New",        // Windows, macOS
            "DejaVu Sans Mono",   // Linux
            "Roboto Mono",        // Android 7+
            "Droid Sans Mono",    // Older Android
            "monospace",          // Android generic alias (sometimes works)
        };
        try
        {
            string[] installed = Font.GetOSInstalledFontNames();
            if (installed == null || installed.Length == 0) return null;

            var installedSet = new HashSet<string>(installed, System.StringComparer.OrdinalIgnoreCase);
            foreach (var name in candidates)
            {
                if (installedSet.Contains(name))
                {
                    Font f = Font.CreateDynamicFontFromOSFont(name, 12);
                    if (f != null) return f;
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BenchmarkMode] Couldn't enumerate OS fonts: {e.Message}");
        }
        return null;
    }
}
