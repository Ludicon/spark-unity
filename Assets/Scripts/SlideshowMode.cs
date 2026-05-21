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

    [Header("Debug")]
    [Tooltip("Re-encode the current texture every frame. Useful for capturing the Spark dispatch in RenderDoc.")]
    public bool encodeEveryFrame = false;

    int _selected;
    float _nextAdvanceTime;
    bool _paused;

    Texture2D _encoded;
    float _cpuTimeMs;
    SparkFormat _detectedFormat;
    string _status;

    readonly PanZoomController _pz = new PanZoomController();

    public override void Activate()
    {
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
        _pz.DrawTexture(_encoded);
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
            lines.Add($"{_detectedFormat} → {_encoded.format}  VMem {FormatBytes(vmem)}");
        }
        if (!string.IsNullOrEmpty(_status))
            lines.Add(_status);

        if (lines.Count > 0)
        {
            float pad = 6f;
            var style = OverlayStyle();
            var content = new GUIContent(string.Join("\n", lines));
            var size = style.CalcSize(content);
            var rect = new Rect(bounds.x + pad, bounds.y + pad, size.x + pad * 2, size.y + pad * 2);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + pad, rect.y + pad, rect.width, rect.height), content, style);
        }

        // ── Buttons (drawn BEFORE view input so they consume their MouseDowns first) ──
        const float btnGap = 4f;
        const float pauseW = 70f, arrowW = 30f, viewBtnW = 60f, btnH = 24f;
        // float totalW = pauseW + btnGap + arrowW + btnGap + arrowW + btnGap + viewBtnW + btnGap + viewBtnW;
        float totalW = pauseW + btnGap + arrowW + btnGap + arrowW + btnGap + viewBtnW;

        float btnY = bounds.y + bounds.height - 32f;
        float x = bounds.x + (bounds.width - totalW) * 0.5f;

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

        // if (GUI.Button(new Rect(x, btnY, viewBtnW, btnH), "1:1"))
        //     _pz.OneToOne();
        // x += viewBtnW + btnGap;

        if (GUI.Button(new Rect(x, btnY, viewBtnW, btnH), "Fit"))
        {
            if (_encoded != null) _pz.ResetFit(_encoded.width, _encoded.height);
        }

        // ── View input AFTER buttons. ──
        if (_encoded != null) _pz.HandleInput(Controller);
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
