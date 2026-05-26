using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// Compare mode: quality (RMSE) and throughput (MPix/s) of the builtin runtime
// Texture2D.Compress vs Spark. Uses the same per-format source textures and timing
// setup as BenchmarkMode so the Spark numbers line up.
//
// Texture2D.Compress picks its target BCn format from the source TextureFormat:
// R8 -> BC4, RG16 -> BC5, RGB24 -> DXT1, RGBA32 -> DXT5. It has no BC7 path, so
// every row except RGB-hi is fillable; RGB-hi shows "---".
public class CompareMode : SparkDemoMode
{
    public override string DisplayName => "Compare";

    [Tooltip("highQuality flag passed to Texture2D.Compress.")]
    public bool builtinHighQuality = true;

    [Tooltip("Edge length of the synthetic fallback texture, used when a row's preferred source isn't loaded.")]
    public int textureSize = 1024;

    [Tooltip("How many back-to-back encodes the initial calibration batch performs.")]
    public int initialDispatchCount = 100;

    [Tooltip("Target wall-clock seconds for each measured run; the dispatch count is calibrated to land near this.")]
    public float perRunSeconds = 0.2f;

    [Tooltip("Measured runs per format/row; the best (fastest) is reported.")]
    public int measurementRuns = 10;

    [Tooltip("Write the decoded reference / builtin / Spark images to disk (persistentDataPath/CompareOutput) for visual inspection.")]
    public bool saveDecodedImages = false;

    struct Row
    {
        public string label;
        public SparkFormat sparkFormat;  // generic (R/RG/RGB/RGBA); Spark resolves the concrete format
        public bool preferLowQuality;    // RGB only: pick the low-quality variant (BC1/ETC2)
        public int channelMask;          // 1=R 2=G 4=B 8=A; channels that count toward RMSE
        public bool builtinSupported;    // false only for RGB-hi (no BC7 in Texture2D.Compress)
        public TextureFormat builtinSrc; // source format that drives Compress's target BCn
        public string sourceName;        // preferred source (no extension); BenchmarkMode's mapping
    }

    static readonly Row[] s_rows =
    {
        new Row { label = "R",           sparkFormat = SparkFormat.R,    channelMask = 1,  builtinSupported = true,  builtinSrc = TextureFormat.R8,     sourceName = "Material.003_Roughness" },
        new Row { label = "RG",          sparkFormat = SparkFormat.RG,   channelMask = 3,  builtinSupported = true,  builtinSrc = TextureFormat.RG16,   sourceName = "Ground037_1K-PNG_NormalGL" },
        new Row { label = "RGB (4 bpp)", sparkFormat = SparkFormat.RGB,  preferLowQuality = true,  channelMask = 7,  builtinSupported = true,  builtinSrc = TextureFormat.RGB24,  sourceName = "Material.003_Base_color" },
        new Row { label = "RGB (8 bpp)", sparkFormat = SparkFormat.RGB,  channelMask = 7,  builtinSupported = false,                                   sourceName = "Material.003_Base_color" },
        new Row { label = "RGBA",        sparkFormat = SparkFormat.RGBA, channelMask = 15, builtinSupported = true,  builtinSrc = TextureFormat.RGBA32, sourceName = "quadTexture_rgba" },
    };

    struct Cell { public bool valid; public double mpix; public double rmse; }

    // Per-row source context, prepared once before the timed phases.
    struct Ctx { public bool valid; public Texture2D src; public Texture2D working; public Color32[] raw; public Color32[] reference; public int w, h; }

    Cell[] _spark;
    Cell[] _builtin;
    string[] _srcInfo;
    bool _running;
    bool _done;
    string _status = "Press Run.";
    Texture2D _synthetic;
    RenderTexture _syncRT;
    Coroutine _co;

    public override void Activate()
    {
        Spark.Preload(SparkFormat.BC4_R, SparkFormat.BC5_RG, SparkFormat.BC1_RGB, SparkFormat.BC7_RGB, SparkFormat.BC7_RGBA);
        if (_syncRT == null)
        {
            _syncRT = new RenderTexture(1, 1, 0, GraphicsFormat.R8G8B8A8_UNorm);
            _syncRT.Create();
        }
    }

    public override void Deactivate()
    {
        if (_co != null) { StopCoroutine(_co); _co = null; }
        _running = false;
    }

    void OnDestroy()
    {
        if (_syncRT != null) { _syncRT.Release(); Destroy(_syncRT); }
        if (_synthetic != null) Destroy(_synthetic);
    }

    // Per-format preferred source (BenchmarkMode's mapping), else the synthetic fallback.
    Texture2D GetSource(Row row)
    {
        if (!string.IsNullOrEmpty(row.sourceName) && Controller.SourceTextures != null)
            foreach (var t in Controller.SourceTextures)
                if (t != null && string.Equals(t.name, row.sourceName, System.StringComparison.OrdinalIgnoreCase))
                    return t;
        return EnsureSyntheticSource();
    }

    Texture2D EnsureSyntheticSource()
    {
        if (_synthetic != null) return _synthetic;
        _synthetic = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false) { name = "synthetic" };
        var data = new Color32[textureSize * textureSize];
        var rng = new System.Random(42);
        for (int i = 0; i < data.Length; i++)
            data[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        _synthetic.SetPixels32(data);
        _synthetic.Apply(false, false);   // keep readable — we need GetPixels32 for the builtin path
        return _synthetic;
    }

    // ── Measurement ──────────────────────────────────────────────────────────

    IEnumerator RunCompare()
    {
        _running = true;
        _done = false;
        int n = s_rows.Length;
        _spark = new Cell[n];
        _builtin = new Cell[n];
        _srcInfo = new string[n];

        // Prepare per-row source context up front (off the timed path).
        var ctx = new Ctx[n];
        for (int i = 0; i < n; i++)
        {
            var src = GetSource(s_rows[i]);
            if (src == null || !src.isReadable)
            {
                _srcInfo[i] = "(missing)";
                _status = $"Prep {s_rows[i].label} — source unavailable";
                yield return null;
                continue;
            }

            int w = src.width, h = src.height;
            Color32[] raw = src.GetPixels32();

            // Linear working copy: GPU sampling returns raw stored bytes (no sRGB decode), so
            // both pipelines encode and are compared in the same space.
            var working = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            working.SetPixels32(raw);
            working.Apply(false, false);

            // Reference is the uncompressed source decoded through the same Blit->readback path
            // used for the compressed results, so any orientation/sampling quirk cancels out.
            Color32[] reference = DecodeToPixels(working, w, h);

            ctx[i] = new Ctx { valid = true, src = src, working = working, raw = raw, reference = reference, w = w, h = h };
            _srcInfo[i] = $"{src.name} {w}×{h}";
            yield return null;
        }

        // Phase 1 — Spark, all rows back-to-back so the GPU stays in its boost state for the
        // whole timed section (no interleaved CPU work, matching BenchmarkMode).
        for (int i = 0; i < n; i++)
        {
            if (!ctx[i].valid) continue;
            _status = $"Spark {s_rows[i].label} ({i + 1}/{n})";
            yield return null;
            yield return MeasureSpark(i, ctx[i]);
        }

        // Phase 2 — builtin Texture2D.Compress (CPU). Kept separate from the Spark timing.
        for (int i = 0; i < n; i++)
        {
            if (!ctx[i].valid) continue;
            _status = $"Builtin {s_rows[i].label} ({i + 1}/{n})";
            yield return null;
            MeasureBuiltin(i, ctx[i]);
            yield return null;
        }

        for (int i = 0; i < n; i++)
            if (ctx[i].working != null) Destroy(ctx[i].working);

        _status = "Done.";
        _done = true;
        _running = false;
        _co = null;
    }

    IEnumerator MeasureSpark(int i, Ctx c)
    {
        var row = s_rows[i];
        var cell = new Cell();
        if (!Spark.IsFormatSupported(row.sparkFormat)) { _spark[i] = cell; yield break; }

        // Let Spark pick the concrete format up front, then use it for both the destination
        // texture and the encode (no concrete BC format is hardcoded in the row table).
        var resolved = Spark.ResolveFormat(row.sparkFormat, row.preferLowQuality);
        var dst = new Texture2D(c.w, c.h, Spark.GetCompressedFormat(resolved, false), 1,
            TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);

        // Calibration run doubles as warmup; pick a count that lands each measured run near perRunSeconds.
        int count = initialDispatchCount;
        try
        {
            double cal = RunSparkBatch(c.working, dst, resolved, count);
            if (cal > 0.001)
                count = Mathf.Clamp(Mathf.CeilToInt(count * (float)(perRunSeconds / cal)),
                                    Mathf.Max(1, initialDispatchCount / 10), 50_000);
        }
        catch (System.Exception e) { Debug.LogException(e); Destroy(dst); _spark[i] = cell; yield break; }

        double best = double.MaxValue;
        bool ok = true;
        for (int run = 0; run < measurementRuns; run++)
        {
            double s;
            try { s = RunSparkBatch(c.working, dst, resolved, count); }
            catch (System.Exception e) { Debug.LogException(e); ok = false; break; }
            if (s < best) best = s;
            yield return null;
        }

        if (ok && best < double.MaxValue)
        {
            Color32[] decoded = DecodeToPixels(dst, c.w, c.h);
            cell.valid = true;
            cell.mpix = (double)c.w * c.h * count / best / 1_000_000.0;
            cell.rmse = Rmse(c.reference, decoded, row.channelMask);
            Debug.Log($"[CompareMode] Spark   {row.label,-9} {resolved,-12} {c.src.name} {c.w}×{c.h}  {cell.mpix,5:F0} MPix/s  RMSE {cell.rmse:F2}");

            if (saveDecodedImages)
            {
                SavePng(c.reference, c.w, c.h, $"{Slug(row.label)}_reference_{c.src.name}.png");
                SavePng(decoded, c.w, c.h, $"{Slug(row.label)}_spark_{resolved}_{c.src.name}.png");
            }
        }
        Destroy(dst);
        _spark[i] = cell;
    }

    void MeasureBuiltin(int i, Ctx c)
    {
        var row = s_rows[i];
        var cell = new Cell();
        if (!row.builtinSupported) { _builtin[i] = cell; return; }

        double best = double.MaxValue;
        Texture2D kept = null;

        for (int run = 0; run < measurementRuns; run++)
        {
            var copy = new Texture2D(c.w, c.h, row.builtinSrc, false, true);
            copy.SetPixels32(c.raw);
            copy.Apply(false, false);

            var sw = Stopwatch.StartNew();
            copy.Compress(builtinHighQuality);
            sw.Stop();

            double s = sw.Elapsed.TotalSeconds;
            if (s < best) best = s;

            if (run == measurementRuns - 1) { copy.Apply(false, false); kept = copy; }
            else Destroy(copy);
        }

        cell.valid = true;
        cell.mpix = (double)c.w * c.h / best / 1_000_000.0;
        if (kept != null)
        {
            Color32[] decoded = DecodeToPixels(kept, c.w, c.h);
            cell.rmse = Rmse(c.reference, decoded, row.channelMask);
            Destroy(kept);
            Debug.Log($"[CompareMode] Builtin {row.label,-9} {row.builtinSrc,-8} {c.src.name} {c.w}×{c.h}  {cell.mpix,5:F0} MPix/s  RMSE {cell.rmse:F2}");

            if (saveDecodedImages)
                SavePng(decoded, c.w, c.h, $"{Slug(row.label)}_builtin_{row.builtinSrc}_{c.src.name}.png");
        }
        _builtin[i] = cell;
    }

    // One CommandBuffer with `count` back-to-back encodes plus a 1x1 blit sync point, drained
    // via AsyncGPUReadback. Returns elapsed wall-clock seconds.
    double RunSparkBatch(Texture src, Texture dst, SparkFormat format, int count)
    {
        var cmd = new CommandBuffer { name = $"Compare {format}" };
        for (int j = 0; j < count; j++)
            Spark.EncodeTexture(cmd, src, dst, format);
        cmd.Blit(Texture2D.whiteTexture, _syncRT);

        var sw = Stopwatch.StartNew();
        Graphics.ExecuteCommandBuffer(cmd);
        AsyncGPUReadback.Request(_syncRT).WaitForCompletion();
        sw.Stop();

        cmd.Release();
        return sw.Elapsed.TotalSeconds;
    }

    // Hardware-decode a (compressed) texture to RGBA32 bytes via Blit + readback.
    static Color32[] DecodeToPixels(Texture tex, int w, int h)
    {
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        var req = AsyncGPUReadback.Request(rt, 0, GraphicsFormat.R8G8B8A8_UNorm);
        req.WaitForCompletion();
        Color32[] data = req.GetData<Color32>().ToArray();
        RenderTexture.ReleaseTemporary(rt);
        return data;
    }

    // Write decoded RGBA bytes to persistentDataPath/CompareOutput as a PNG.
    static void SavePng(Color32[] pixels, int w, int h, string fileName)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        byte[] png = tex.EncodeToPNG();
        Destroy(tex);

        string dir = Path.Combine(Application.persistentDataPath, "CompareOutput");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, png);
        Debug.Log($"[CompareMode] wrote {path}");
    }

    static string Slug(string s) => s.Replace(" ", "");

    static double Rmse(Color32[] reference, Color32[] decoded, int mask)
    {
        double sumSq = 0;
        long n = 0;
        int len = Mathf.Min(reference.Length, decoded.Length);
        for (int i = 0; i < len; i++)
        {
            Color32 a = reference[i], b = decoded[i];
            if ((mask & 1) != 0) { int d = a.r - b.r; sumSq += d * d; n++; }
            if ((mask & 2) != 0) { int d = a.g - b.g; sumSq += d * d; n++; }
            if ((mask & 4) != 0) { int d = a.b - b.b; sumSq += d * d; n++; }
            if ((mask & 8) != 0) { int d = a.a - b.a; sumSq += d * d; n++; }
        }
        return n > 0 ? System.Math.Sqrt(sumSq / n) : 0;
    }

    // ── UI ─────────────────────────────────────────────────────────────────────

    public override void OnGUIForeground(Rect bounds)
    {
        float pad = 12f;
        float x = bounds.x + pad, y = bounds.y + pad, w = bounds.width - pad * 2;

        GUI.Label(new Rect(x, y, w, 18), $"Builtin = Texture2D.Compress(highQuality: {builtinHighQuality})  ·  best of {measurementRuns} runs", Faded()); y += 22;

        if (!_running)
        {
            if (GUI.Button(new Rect(x, y, 160, 28), _done ? "Run Again" : "Run"))
                _co = StartCoroutine(RunCompare());
            y += 38;
        }
        else
        {
            GUI.Label(new Rect(x, y, w, 20), _status, Body()); y += 30;
        }

        if (_spark != null)
        {
            DrawTable(ref y, x, w, "Throughput ↑ (MPix/s)", c => c.valid ? c.mpix.ToString("F0") : "N/A");
            y += 14;
            DrawTable(ref y, x, w, "Quality ↓ (RMSE)", c => c.valid ? c.rmse.ToString("F2") : "N/A");

            if (_srcInfo != null)
            {
                y += 10;
                GUI.Label(new Rect(x, y, w, 16), "<b>Sources</b>", Faded()); y += 16;
                var mono = Mono();
                for (int i = 0; i < s_rows.Length; i++)
                {
                    GUI.Label(new Rect(x, y, w, 16), $"{s_rows[i].label,-10}{_srcInfo[i]}", mono); y += 16;
                }
            }
        }
    }

    void DrawTable(ref float y, float x, float w, string title, System.Func<Cell, string> fmt)
    {
        var mono = Mono();
        GUI.Label(new Rect(x, y, w, 20), $"<b>{title}</b>", Body()); y += 22;
        GUI.Label(new Rect(x, y, w, 18), Row3("", "Builtin", "Spark"), mono); y += 18;
        for (int i = 0; i < s_rows.Length; i++)
        {
            string b = _builtin != null ? fmt(_builtin[i]) : "---";
            string s = _spark != null ? fmt(_spark[i]) : "---";
            GUI.Label(new Rect(x, y, w, 18), Row3(s_rows[i].label, b, s), mono); y += 18;
        }
    }

    static string Row3(string a, string b, string c) => $"{a,-14}{b,20}{c,10}";

    static GUIStyle s_body, s_faded, s_mono;
    static Font s_monoFont;
    static bool s_monoTried;

    static GUIStyle Body()  { return s_body  ?? (s_body  = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13 }); }
    static GUIStyle Faded() { return s_faded ?? (s_faded = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11, normal = { textColor = new Color(0.7f, 0.7f, 0.7f) } }); }

    static GUIStyle Mono()
    {
        if (s_mono != null) return s_mono;
        if (!s_monoTried) { s_monoTried = true; s_monoFont = LoadMonoFont(); }
        s_mono = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13 };
        if (s_monoFont != null) s_mono.font = s_monoFont;
        return s_mono;
    }

    static Font LoadMonoFont()
    {
        string[] candidates = { "Menlo", "Consolas", "Courier New", "DejaVu Sans Mono", "Roboto Mono" };
        try
        {
            var installed = new HashSet<string>(Font.GetOSInstalledFontNames(), System.StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
                if (installed.Contains(c))
                {
                    Font f = Font.CreateDynamicFontFromOSFont(c, 13);
                    if (f != null) return f;
                }
        }
        catch (System.Exception e) { Debug.LogWarning($"[CompareMode] font enumeration failed: {e.Message}"); }
        return null;
    }
}
