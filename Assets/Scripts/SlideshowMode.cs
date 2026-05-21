using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

// Slideshow mode: loops through textures from StreamingAssets every few seconds, auto-detecting the right Spark format from each file's name.
public class SlideshowMode : SparkDemoMode
{
    public override string DisplayName => "Slideshow";

    [Tooltip("Seconds between automatic texture advances.")]
    public float advanceInterval = 3f;

    [Header("View")]
    [Tooltip("Initial view mode. Original = source, Compressed = Spark output, Diff = |source - compressed| ×Amplify.")]
    public ViewMode initialViewMode = ViewMode.Compressed;

    [Tooltip("Multiplier applied to the per-channel absolute difference in Diff mode. Compression error is usually a few percent — ×8 brings it into the visible range.")]
    [Range(1f, 64f)]
    public float diffAmplify = 8f;

    [Header("Debug")]
    [Tooltip("Re-encode the current texture every frame. Useful for capturing the Spark dispatch in RenderDoc.")]
    public bool encodeEveryFrame = false;

    public enum ViewMode { Original = 0, Compressed = 1, Diff = 2 }

    int _selected;
    float _nextAdvanceTime;
    bool _paused;

    Texture2D _encoded;
    float _cpuTimeMs;
    SparkFormat _detectedFormat;
    string _status;
    ViewMode _viewMode;

    Material _viewMat;
    static readonly int s_idCompressedTex = UnityEngine.Shader.PropertyToID("_CompressedTex");
    static readonly int s_idMode          = UnityEngine.Shader.PropertyToID("_Mode");
    static readonly int s_idDiffAmplify   = UnityEngine.Shader.PropertyToID("_DiffAmplify");
    static readonly int s_idChannelMode   = UnityEngine.Shader.PropertyToID("_ChannelMode");

    readonly PanZoomController _pz = new PanZoomController();

    public override void Activate()
    {
        _viewMode = initialViewMode;
        if (_viewMat == null)
        {
            var sh = Resources.Load<Shader>("SlideDiff");
            if (sh != null) _viewMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }
        _pz.Invalidate();
        ScheduleEncode();
    }

    public override void Deactivate()
    {
        if (_encoded != null) { Destroy(_encoded); _encoded = null; }
    }

    void OnDestroy()
    {
        if (_encoded != null) Destroy(_encoded);
        if (_viewMat != null) Destroy(_viewMat);
    }

    void ScheduleEncode()
    {
        _nextAdvanceTime = Time.unscaledTime + advanceInterval;
        Encode();
    }

    public override void OnTick()
    {
        var sources = Controller.SourceTextures;
        if (sources.Count == 0) return;

        if (!_paused && Time.unscaledTime >= _nextAdvanceTime)
        {
            _selected = (_selected + 1) % sources.Count;
            ScheduleEncode();
        }

        if (_encoded != null)
            _pz.Tick(Time.unscaledDeltaTime, _encoded.width, _encoded.height);

        if (encodeEveryFrame)
            Encode();
    }

    void Encode()
    {
        var sources = Controller.SourceTextures;
        if (sources.Count == 0) return;
        if (_selected >= sources.Count) _selected = 0;

        var source = sources[_selected];
        if (source == null) return;

        _detectedFormat = DetectFormat(source.name);

        // GLES ignores inline shader sampler states (`sampler_point_clamp` in SlideDiff.shader)
        // and falls back to each texture's own FilterMode. Without this, the original is
        // sampled bilinearly on GLES while the encoded RT is sampled point, putting resampling
        // noise into the diff. Force point on both so the comparison is honest across
        // backends.
        source.filterMode = FilterMode.Point;

        try
        {
            if (_encoded != null) { Destroy(_encoded); _encoded = null; }

            var sw = Stopwatch.StartNew();
            _encoded = Spark.EncodeTexture(source, _detectedFormat, srgb: true);
            sw.Stop();
            _cpuTimeMs = (float)sw.Elapsed.TotalMilliseconds;
            if (_encoded != null)
                _encoded.filterMode = FilterMode.Point;
            _status = null;
        }
        catch (System.Exception e)
        {
            _status = $"Error: {e.Message}";
            Debug.LogException(e);
        }
    }

    /// <summary>Map filename to a SparkFormat based on common PBR texture naming.</summary>
    public static SparkFormat DetectFormat(string nameNoExt)
    {
        if (string.IsNullOrEmpty(nameNoExt)) return SparkFormat.RGB;
        string n = nameNoExt.ToLowerInvariant();
        if (n.EndsWith("_rgba"))
            return SparkFormat.RGBA;
        if (n.Contains("normal") || n.Contains("_nor_") || n.Contains("_norm") || n.EndsWith("_n"))
            return SparkFormat.RG;
        if (n.Contains("rough") || n.EndsWith("_r") || n.Contains("_ao") || n.Contains("metallic"))
            return SparkFormat.R;
        return SparkFormat.RGB;
    }

    public override void OnGUIBackground(Rect bounds)
    {
        if (_encoded == null) return;
        _pz.Configure(bounds, Controller, _encoded.width, _encoded.height);

        var sources = Controller.SourceTextures;
        Texture2D original = (sources.Count > 0 && _selected < sources.Count) ? sources[_selected] : null;

        // Material path needs both the original and the compressed result. If either is
        // missing (or shader failed to load), fall through to the plain GUI draw of the
        // compressed texture — same as the pre-diff-mode behavior.
        if (_viewMat != null && original != null)
        {
            _viewMat.SetTexture(s_idCompressedTex, _encoded);
            _viewMat.SetFloat (s_idMode,           (float)_viewMode);
            _viewMat.SetFloat (s_idDiffAmplify,    diffAmplify);
            _viewMat.SetFloat (s_idChannelMode,    ChannelModeFor(_detectedFormat));
            // Shader uses a point-clamp sampler (so both textures sample identically), so
            // outside the texture's [0,1] UV range we want an honest black border instead
            // of clamped edge texels.
            _pz.DrawTextureMaterialClampToBorder(original, _viewMat, Color.black);
        }
        else
        {
            //_pz.DrawTexture(_encoded);
        }
    }

    /// <summary>Map <see cref="SparkFormat"/> to the shader's <c>_ChannelMode</c> uniform.
    /// Accepts both generic (R/RG/RGB/RGBA) and concrete (BC4_R/EAC_R/BC5_RG/…) formats so
    /// it works whether <c>_detectedFormat</c> has been resolved yet or not.</summary>
    static float ChannelModeFor(SparkFormat fmt)
    {
        switch (fmt)
        {
            case SparkFormat.R:
            case SparkFormat.BC4_R:
            case SparkFormat.EAC_R:
                return 0f;
            case SparkFormat.RG:
            case SparkFormat.BC5_RG:
            case SparkFormat.EAC_RG:
                return 1f;
            case SparkFormat.RGBA:
            case SparkFormat.BC7_RGBA:
            case SparkFormat.ASTC_4x4_RGBA:
                return 3f;
            default:
                return 2f;   // RGB / BC1_RGB / BC7_RGB / ETC2_RGB / ASTC_4x4_RGB
        }
    }

    public override void OnGUIForeground(Rect bounds)
    {
        if (_encoded == null && string.IsNullOrEmpty(_status)) return;

        var sources = Controller.SourceTextures;

        // ── Overlay ──
        var lines = new List<string>();
        if (sources.Count > 0 && _selected < sources.Count && sources[_selected] != null)
        {
            var src = sources[_selected];
            lines.Add($"<b>{src.name}</b>  {src.width}x{src.height}");
        }
        if (_encoded != null)
        {
            long vmem = Profiler.GetRuntimeMemorySizeLong(_encoded);
            string modeLabel = _viewMode == ViewMode.Diff ? $"Diff ×{diffAmplify:F0}" : _viewMode.ToString();
            lines.Add($"{_detectedFormat} → {_encoded.format}  VMem {FormatBytes(vmem)}  <i>view: {modeLabel}</i>");
        }
        if (!string.IsNullOrEmpty(_status))
            lines.Add(_status);

        // Track the bottom of the info box so the mode buttons can dock just under it.
        float overlayPad = 6f;
        float infoBottom = bounds.y + overlayPad;
        if (lines.Count > 0)
        {
            var style = OverlayStyle();
            var content = new GUIContent(string.Join("\n", lines));
            var size = style.CalcSize(content);
            var rect = new Rect(bounds.x + overlayPad, bounds.y + overlayPad, size.x + overlayPad * 2, size.y + overlayPad * 2);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + overlayPad, rect.y + overlayPad, rect.width, rect.height), content, style);
            infoBottom = rect.y + rect.height;
        }

        // ── Buttons (drawn BEFORE view input so they consume their MouseDowns first) ──
        const float btnGap = 4f;
        const float pauseW = 70f, arrowW = 30f, viewBtnW = 60f;
        // Compressed gets a slightly wider slot because its label is the longest of the three.
        const float modeBtnW = 80f, modeBtnWWide = 88f, btnH = 24f;
        const float rowGap = 6f;

        // View-mode toggle: docked just below the info box, left-aligned to it.
        float modeY = infoBottom + rowGap;
        float mx    = bounds.x + overlayPad;
        DrawModeButton(new Rect(mx,                                                modeY, modeBtnW,     btnH), "Original",   ViewMode.Original);
        DrawModeButton(new Rect(mx + modeBtnW + btnGap,                            modeY, modeBtnWWide, btnH), "Compressed", ViewMode.Compressed);
        DrawModeButton(new Rect(mx + modeBtnW + btnGap + modeBtnWWide + btnGap,    modeY, modeBtnW,     btnH), $"Diff ×{diffAmplify:F0}", ViewMode.Diff);

        // Playback + fit row stays anchored at the bottom.
        float playRowW = pauseW + btnGap + arrowW + btnGap + arrowW + btnGap + viewBtnW;
        float btnY = bounds.y + bounds.height - 32f;
        float x = bounds.x + (bounds.width - playRowW) * 0.5f;

        if (GUI.Button(new Rect(x, btnY, pauseW, btnH), _paused ? "▶ Play" : "■ Pause"))
            _paused = !_paused;
        x += pauseW + btnGap;

        if (GUI.Button(new Rect(x, btnY, arrowW, btnH), "◀"))
        {
            _selected = (_selected - 1 + sources.Count) % sources.Count;
            ScheduleEncode();
        }
        x += arrowW + btnGap;

        if (GUI.Button(new Rect(x, btnY, arrowW, btnH), "▶"))
        {
            _selected = (_selected + 1) % sources.Count;
            ScheduleEncode();
        }
        x += arrowW + btnGap;

        if (GUI.Button(new Rect(x, btnY, viewBtnW, btnH), "Fit"))
        {
            if (_encoded != null) _pz.ResetFit(_encoded.width, _encoded.height);
        }

        // Keyboard: Space toggles between Compressed and the previously-viewed alternative.
        // From Compressed → Original; from Original or Diff → Compressed. The two-way swap
        // makes "show me the artifact" / "show me the source" a one-key flip.
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Space)
        {
            _viewMode = (_viewMode == ViewMode.Compressed) ? ViewMode.Original : ViewMode.Compressed;
            Event.current.Use();
        }

        // ── View input AFTER buttons. ──
        if (_encoded != null) _pz.HandleInput(Controller);
    }

    /// <summary>Segmented-control style mode button. Renders as a Toggle in "Button" mode so
    /// the active mode appears pressed; click toggles into that mode.</summary>
    void DrawModeButton(Rect rect, string label, ViewMode mode)
    {
        bool isActive = _viewMode == mode;
        bool clicked = GUI.Toggle(rect, isActive, label, "Button");
        if (clicked && !isActive)
            _viewMode = mode;
    }

    static string FormatBytes(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024f:F1} KB";
        return $"{n / (1024f * 1024f):F2} MB";
    }

    static GUIStyle s_overlayStyle;
    static GUIStyle OverlayStyle()
    {
        if (s_overlayStyle == null)
            s_overlayStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
        return s_overlayStyle;
    }
}
