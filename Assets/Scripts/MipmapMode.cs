using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Experimental.Rendering;

// Mip-chain viewer: encodes a texture with a full mip chain and displays one mip level at a
// time. Owns its own source texture (loaded WITH mipChain) rather than borrowing from
// SparkDemo.SourceTextures, which the slideshow mode loads mip-less.
public class MipmapMode : SparkDemoMode
{
    public override string DisplayName => "Mipmaps";

    public SparkFormat format = SparkFormat.RGB;

    [Tooltip("Path under StreamingAssets/ for the mip-mode source image. Loaded with a mip "
           + "chain so the encoder has all levels to compress. Default points at "
           + "StreamingAssets/Mipmap/paint.png — keep mip-mode sources in their own subfolder "
           + "so they're not picked up by SparkDemo's slideshow loader.")]
    public string sourcePath = "Mipmap/paint.png";

    Texture2D _source;
    Texture2D _encoded;
    int _selectedMip;
    Material _previewMat;
    string _status;
    bool _loading;

    public override void Activate()
    {
        if (_previewMat == null)
        {
            var sh = Resources.Load<Shader>("MipPreview");
            if (sh != null) _previewMat = new Material(sh);
        }

        // Source is loaded once and kept across tab switches. Encode runs every Activate so
        // a format change or _selectedMip recovery happens predictably.
        if (_source == null && !_loading)
        {
            _loading = true;
            StartCoroutine(LoadSourceAndEncode());
        }
        else if (_source != null)
        {
            Encode();
        }
    }

    public override void Deactivate()
    {
        if (_encoded != null) { Destroy(_encoded); _encoded = null; }
    }

    void OnDestroy()
    {
        if (_encoded != null)   Destroy(_encoded);
        if (_source != null)    Destroy(_source);
        if (_previewMat != null) Destroy(_previewMat);
    }

    /// <summary>Load <see cref="sourcePath"/> from StreamingAssets with a mip chain, then
    /// kick off the first encode. Uses UnityWebRequest on Android (StreamingAssets sits
    /// inside the APK there) and direct File IO everywhere else.</summary>
    IEnumerator LoadSourceAndEncode()
    {
        string path = Application.streamingAssetsPath + "/" + sourcePath;
        byte[] data = null;

        if (path.Contains("://"))
        {
            using (var req = UnityWebRequest.Get(path))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"Failed to load {sourcePath}: {req.error}";
                    _loading = false;
                    yield break;
                }
                data = req.downloadHandler.data;
            }
        }
        else
        {
            if (!File.Exists(path))
            {
                _status = $"Source not found: {sourcePath}";
                _loading = false;
                yield break;
            }
            data = File.ReadAllBytes(path);
        }

        _source = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
        _source.name = Path.GetFileNameWithoutExtension(sourcePath);
        if (!_source.LoadImage(data, markNonReadable: true))
        {
            Destroy(_source);
            _source = null;
            _status = $"Failed to decode {sourcePath}";
            _loading = false;
            yield break;
        }

        _loading = false;
        Encode();
    }

    void Encode()
    {
        if (_source == null) return;

        if (_source.mipmapCount <= 1)
        {
            _status = $"{_source.name}: source has no mip chain (mipmapCount={_source.mipmapCount}).";
            return;
        }

        try
        {
            if (_encoded != null) { Destroy(_encoded); _encoded = null; }
            _encoded = Spark.EncodeTexture(_source, format, srgb: true);
            if (_encoded != null)
            {
                _encoded.filterMode = FilterMode.Point;
                _selectedMip = Mathf.Clamp(_selectedMip, 0, _encoded.mipmapCount - 1);
            }
            _status = null;
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
        const float topInset = 4f;

        // ── Overlay text ──
        if (_source != null || _encoded != null || !string.IsNullOrEmpty(_status))
        {
            var lines = new List<string>();
            if (_encoded != null)
            {
                int mw = Mathf.Max(1, _encoded.width  >> _selectedMip);
                int mh = Mathf.Max(1, _encoded.height >> _selectedMip);
                lines.Add($"mip {_selectedMip} of {_encoded.mipmapCount - 1}  ({mw}×{mh})");

                long vmem = GraphicsFormatUtility.ComputeMipChainSize(_encoded.width, _encoded.height, _encoded.graphicsFormat, _encoded.mipmapCount);
                lines.Add($"{format} → {_encoded.format}  VMem {FormatBytes(vmem)}");
            }
            if (!string.IsNullOrEmpty(_status)) lines.Add(_status);

            GUI.Label(new Rect(bounds.x + 8, bounds.y + topInset, bounds.width - 16, 80),
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
