using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

/// <summary>
/// Generates an animated plasma into a RenderTexture each frame via a compute shader,
/// then compresses it with Spark and displays the result. Demonstrates the per-frame
/// encode path with destination reuse — no allocations during the render loop.
/// Mirrors spark.js/examples/realtime.html.
/// </summary>
public class PlasmaMode : SparkDemoMode
{
    public override string DisplayName => "Plasma";

    public int textureSize = 1024;
    public SparkFormat format = SparkFormat.RGB;

    ComputeShader _plasmaShader;
    int _plasmaKernel;
    RenderTexture _source;
    Texture2D _encoded;
    CommandBuffer _cmd;
    float _startTime;
    float _pausedAt;        // realtime stamp captured when paused
    float _pausedOffset;    // accumulated paused-time, subtracted from t so resume is seamless
    bool  _paused;

    readonly PanZoomController _pz = new PanZoomController();

    public override void Activate()
    {
        if (_plasmaShader == null)
        {
            _plasmaShader = Resources.Load<ComputeShader>("Plasma");
            if (_plasmaShader != null) _plasmaKernel = _plasmaShader.FindKernel("Plasma");
        }
        if (_plasmaShader == null) return;

        // Allocate source RT (UAV-writable). Use a linear UNorm so the compute shader's
        // numerical output ends up in the texture as-is — Spark will apply sRGB encoding
        // on the compressed output via the srgb:true argument.
        _source = new RenderTexture(textureSize, textureSize, 0, GraphicsFormat.R8G8B8A8_UNorm)
        {
            enableRandomWrite = true,
            useMipMap = false,
            filterMode = FilterMode.Bilinear,
        };
        _source.Create();

        // Allocate destination compressed Texture2D once. Reused every frame.
        var compressedFmt = Spark.GetCompressedFormat(Spark.ResolveFormat(format), srgb: true);
        _encoded = new Texture2D(textureSize, textureSize, compressedFmt, mipCount: 1,
            TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
        _encoded.filterMode = FilterMode.Point;
        if (_encoded.isReadable) _encoded.Apply(false, true);

        _cmd = new CommandBuffer { name = "Plasma + Spark" };
        _startTime = Time.realtimeSinceStartup;
        _pausedOffset = 0f;
        _paused = false;
        _pz.Invalidate();
    }

    public override void Deactivate()
    {
        if (_source != null)  { _source.Release(); Destroy(_source);  _source  = null; }
        if (_encoded != null) { Destroy(_encoded); _encoded = null; }
        if (_cmd != null)     { _cmd.Release();    _cmd = null; }
    }

    void OnDestroy() { Deactivate(); }

    void TogglePaused()
    {
        if (!_paused)
        {
            _pausedAt = Time.realtimeSinceStartup;
            _paused = true;
        }
        else
        {
            // Resume: extend the offset by the time we were paused so t is continuous.
            _pausedOffset += Time.realtimeSinceStartup - _pausedAt;
            _paused = false;
        }
    }

    public override void OnTick()
    {
        if (_plasmaShader == null || _source == null || _encoded == null) return;

        // When paused, freeze t at the pause moment — Spark still re-encodes each frame
        // (so the user can pan/zoom around the frozen frame), but the plasma animation halts.
        float now = _paused ? _pausedAt : Time.realtimeSinceStartup;
        float t = now - _startTime - _pausedOffset;

        _cmd.Clear();
        _cmd.BeginSample("Plasma");
        _cmd.SetComputeVectorParam(_plasmaShader, "_Params",
            new Vector4(t, 0f, textureSize, textureSize));
        _cmd.SetComputeTextureParam(_plasmaShader, _plasmaKernel, "_Dst", _source);
        int groups = (textureSize + 15) / 16;
        _cmd.DispatchCompute(_plasmaShader, _plasmaKernel, groups, groups, 1);
        _cmd.EndSample("Plasma");

        _cmd.BeginSample("Spark");
        Spark.EncodeTexture(_cmd, _source, _encoded, format);
        _cmd.EndSample("Spark");

        Graphics.ExecuteCommandBuffer(_cmd);

        _pz.Tick(Time.unscaledDeltaTime, _encoded.width, _encoded.height);
    }

    public override void OnGUIBackground(Rect bounds)
    {
        if (_encoded == null) return;
        _pz.Configure(bounds, Controller, _encoded.width, _encoded.height);
        _pz.DrawTextureClampToBorder(_encoded, Color.black);
    }

    public override void OnGUIForeground(Rect bounds)
    {
        if (_encoded == null)
        {
            GUI.Label(new Rect(bounds.x + 10, bounds.y + 40, bounds.width, 60),
                "Plasma compute shader unavailable (Resources/Plasma.compute missing).");
            return;
        }

        // Overlay text.
        long vmem = Profiler.GetRuntimeMemorySizeLong(_encoded);
        var text =
            $"<b>Procedural Plasma</b>  {_encoded.width}x{_encoded.height}\n" +
            $"RGB → {_encoded.format}  VMem {FormatBytes(vmem)} FPS {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0}";
        GUI.Label(new Rect(bounds.x + 8, bounds.y + 8, bounds.width - 16, 80), text, OverlayStyle());

        // ── Buttons (drawn BEFORE view input so they consume their MouseDowns first) ──
        const float btnGap = 4f;
        const float pauseW = 70f, viewBtnW = 60f, btnH = 24f;
        float totalW = pauseW;// + btnGap + viewBtnW + btnGap + viewBtnW;

        float btnY = bounds.y + bounds.height - 32f;
        float x = bounds.x + (bounds.width - totalW) * 0.5f;

        if (GUI.Button(new Rect(x, btnY, pauseW, btnH), _paused ? "▶ Play" : "■ Pause"))
            TogglePaused();
        // x += pauseW + btnGap;

        // if (GUI.Button(new Rect(x, btnY, viewBtnW, btnH), "1:1"))
        //     _pz.OneToOne();
        // x += viewBtnW + btnGap;

        // if (GUI.Button(new Rect(x, btnY, viewBtnW, btnH), "Fit"))
        //     _pz.ResetFit(_encoded.width, _encoded.height);

        _pz.HandleInput(Controller);
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
