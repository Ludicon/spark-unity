using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Mip-chain viewer: encodes a chosen texture with a full mip chain and displays one mip
/// level at a time via Spark/MipPreview.shader. <c>+</c> and <c>−</c> buttons step through
/// the chain. Point filtering on the encoded texture means each level shows individual
/// texels crisply — useful for visually inspecting how Spark's downsampler produces each
/// successive level.
/// </summary>
public class MipmapMode : SparkDemoMode
{
    public override string DisplayName => "Mipmaps";

    public SparkFormat format = SparkFormat.RGB;

    [Tooltip("Filename (without path) preferred for the mip viewer. Falls back to the first texture with a mip chain if not found.")]
    public string preferredTextureName = "paint";

    Texture2D _encoded;
    int _sourceIndex;
    int _selectedMip;
    Material _previewMat;
    string _status;

    public override void Activate()
    {
        if (_previewMat == null)
        {
            var sh = Resources.Load<Shader>("MipPreview");
            if (sh != null) _previewMat = new Material(sh);
        }
        SelectInitial();
        Encode();
    }

    public override void Deactivate()
    {
        if (_encoded != null) { Destroy(_encoded); _encoded = null; }
    }

    void OnDestroy()
    {
        if (_encoded != null) Destroy(_encoded);
        if (_previewMat != null) Destroy(_previewMat);
    }

    void SelectInitial()
    {
        var sources = Controller.SourceTextures;
        _sourceIndex = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] != null && sources[i].name.IndexOf(preferredTextureName,
                System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _sourceIndex = i;
                break;
            }
        }
    }

    void Encode()
    {
        var sources = Controller.SourceTextures;
        if (sources.Count == 0) { _status = "No source textures."; return; }
        if (_sourceIndex >= sources.Count) _sourceIndex = 0;

        var source = sources[_sourceIndex];
        if (source == null) return;

        if (source.mipmapCount <= 1)
        {
            _status = $"{source.name}: source has no mip chain (mipmapCount={source.mipmapCount}).";
            return;
        }

        try
        {
            if (_encoded != null) { Destroy(_encoded); _encoded = null; }
            _encoded = Spark.EncodeTexture(source, format, srgb: true);
            if (_encoded != null)
                _encoded.filterMode = FilterMode.Point;
            _status = null;
            // Clamp the previously-selected mip into the new texture's range.
            if (_encoded != null)
                _selectedMip = Mathf.Clamp(_selectedMip, 0, _encoded.mipmapCount - 1);
        }
        catch (System.Exception e)
        {
            _status = $"Error: {e.Message}";
            Debug.LogException(e);
        }
    }

    public override void OnGUIBackground(Rect bounds)
    {
        if (_encoded == null || _previewMat == null) return;

        Matrix4x4 prev = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;

        // Convert mode-area bounds (in scaled GUI coords) to raw pixels.
        float scale = Controller.UiScaleFactor;
        Rect raw = new Rect(bounds.x * scale, bounds.y * scale, bounds.width * scale, bounds.height * scale);

        // Fit the texture's aspect ratio inside the mode area.
        float aspect = (float)_encoded.width / _encoded.height;
        Rect dst = FitAspect(raw, aspect);

        _previewMat.mainTexture = _encoded;
        _previewMat.SetFloat("_MipLevel", _selectedMip);
        Graphics.DrawTexture(dst, _encoded, _previewMat);

        GUI.matrix = prev;
    }

    public override void OnGUIForeground(Rect bounds)
    {
        var sources = Controller.SourceTextures;
        const float topInset = 4f;

        // ── Overlay text ──
        if (sources.Count > 0 && _sourceIndex < sources.Count && sources[_sourceIndex] != null)
        {
            var src = sources[_sourceIndex];
            var lines = new List<string>();
            if (_encoded != null)
            {
                int mw = Mathf.Max(1, _encoded.width  >> _selectedMip);
                int mh = Mathf.Max(1, _encoded.height >> _selectedMip);
                lines.Add($"mip {_selectedMip} of {_encoded.mipmapCount - 1}  ({mw}×{mh})");

                long vmem = Profiler.GetRuntimeMemorySizeLong(_encoded);
                lines.Add($"{format} → {_encoded.format}  VMem {FormatBytes(vmem)}");
            }
            if (!string.IsNullOrEmpty(_status)) lines.Add(_status);

            GUI.Label(new Rect(bounds.x + 8, bounds.y + topInset, bounds.width - 16, 60),
                      string.Join("\n", lines), OverlayStyle());
        }

        if (_encoded == null) return;

        // ── + / − buttons, centered ──
        const float btnGap = 6f;
        const float btnW = 40f, btnH = 28f;
        float totalW = btnW + btnGap + btnW;

        float btnY = bounds.y + bounds.height - 36f;
        float x = bounds.x + (bounds.width - totalW) * 0.5f;

        bool canMinus = _selectedMip > 0;
        bool canPlus  = _selectedMip < _encoded.mipmapCount - 1;

        GUI.enabled = canMinus;
        if (GUI.Button(new Rect(x, btnY, btnW, btnH), "−"))
            _selectedMip = Mathf.Max(0, _selectedMip - 1);
        x += btnW + btnGap;

        GUI.enabled = canPlus;
        if (GUI.Button(new Rect(x, btnY, btnW, btnH), "+"))
            _selectedMip = Mathf.Min(_encoded.mipmapCount - 1, _selectedMip + 1);

        GUI.enabled = true;
    }

    static Rect FitAspect(Rect container, float aspect)
    {
        float w, h;
        if (container.width / container.height > aspect) { h = container.height; w = h * aspect; }
        else                                              { w = container.width;  h = w / aspect; }
        return new Rect(container.x + (container.width  - w) * 0.5f,
                        container.y + (container.height - h) * 0.5f,
                        w, h);
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
