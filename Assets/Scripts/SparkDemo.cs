using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Demo MonoBehaviour that showcases Spark GPU texture compression.
///
/// Attach to any GameObject in a scene. Place PNG/JPG files in
/// StreamingAssets/SparkTextures — they are loaded at runtime via Spark.LoadTexture,
///
/// Usage:
///   1. Select a source texture from the list.
///   2. Pick a compression format and quality level.
///   3. Press "Encode" to compress on the GPU.
///   4. Compare original vs compressed side-by-side.
/// </summary>
public class SparkDemo : MonoBehaviour
{
    // UI state
    int           _selectedTexture;
    int           _selectedFormat = (int)SparkFormat.RGB;
    SparkQuality  _quality = SparkQuality.Medium;
    bool          _srgb    = false;
    Vector2       _texScroll;
    bool          _fmtDropdownOpen;

    // Source textures loaded from StreamingAssets
    List<Texture2D> _sourceTextures = new List<Texture2D>();

    // Result
    Texture2D _encodedTexture;
    float     _encodeTimeMs;
    string    _status = "";

    // Dirty flag — set when options change, consumed in LateUpdate.
    // Start at 0; set to 2 on change so it skips one frame (lets OnGUI
    // run a matching Layout+Repaint cycle before the texture changes).
    int _encodeCountdown = 2;

    // Cached format info
    static readonly string[] FormatNames = System.Enum.GetNames(typeof(SparkFormat));

    void Start()
    {
        LoadTexturesFromStreamingAssets();

        Spark.Preload(_quality, SparkFormat.RGB, SparkFormat.RGBA, SparkFormat.RG, SparkFormat.R);

        if (_sourceTextures.Count == 0)
            _status = "No textures found. Place PNGs in StreamingAssets/SparkTextures/.";
    }

    void LateUpdate()
    {
        if (_encodeCountdown > 0 && _sourceTextures.Count > 0)
        {
            if (--_encodeCountdown == 0)
                Encode();
        }
    }

    void OnDestroy()
    {
        if (_encodedTexture != null)
            Destroy(_encodedTexture);

        foreach (var tex in _sourceTextures)
            if (tex != null) Destroy(tex);

        Spark.ReleaseCache();
    }

    [Header("UI")]
    [Range(1f, 3f)]
    public float uiScale = 1.5f;

    void OnGUI()
    {
        // Scale the entire UI uniformly.
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

        // Work in scaled coordinates.
        float panelW = 340f;
        float margin = 10f;
        float scaledW = Screen.width  / uiScale;
        float scaledH = Screen.height / uiScale;

        GUILayout.BeginArea(new Rect(margin, margin, panelW, scaledH - margin * 2));

        // Title
        GUILayout.Label("<b>Spark Texture Compression</b>", RichStyle());
        GUILayout.Space(4);

        // Texture list
        GUILayout.Label("Source Texture:");
        _texScroll = GUILayout.BeginScrollView(_texScroll, GUILayout.Height(176));
        for (int i = 0; i < _sourceTextures.Count; i++)
        {
            var tex = _sourceTextures[i];
            string name = tex != null ? $"{tex.name}  ({tex.width}x{tex.height})" : "(null)";

            bool selected = (_selectedTexture == i);
            bool clicked  = GUILayout.Toggle(selected, name, "Button");
            if (clicked && !selected)
            {
                _selectedTexture = i;
                _encodeCountdown = 2;
            }
        }
        GUILayout.EndScrollView();

        GUILayout.Space(8);

        // Format combo box
        GUILayout.Label("Format:");
        if (GUILayout.Button(FormatNames[_selectedFormat], "Button"))
            _fmtDropdownOpen = !_fmtDropdownOpen;

        if (_fmtDropdownOpen)
        {
            for (int i = 0; i < FormatNames.Length; i++)
            {
                var fmt = (SparkFormat)i;
                bool supported = Spark.IsFormatSupported(fmt);
                var resolved = Spark.ResolveFormat(fmt);
                string label = FormatNames[i];
                if (resolved != fmt)
                    label += $" → {resolved}";
                if (!supported)
                    label = $"<color=#888>{label} (n/a)</color>";

                if (GUILayout.Button(label, RichButton()))
                {
                    _selectedFormat = i;
                    _fmtDropdownOpen = false;
                    _encodeCountdown = 2;
                }
            }
        }

        GUILayout.Space(8);

        // Quality
        GUILayout.Label("Quality:");
        var newQuality = (SparkQuality)GUILayout.SelectionGrid((int)_quality, new[] { "Low", "Medium", "High" }, 3);
        if (newQuality != _quality)
        {
            _quality = newQuality;
            _encodeCountdown = 2;
        }

        //GUILayout.Space(4);
        //_srgb = GUILayout.Toggle(_srgb, "sRGB");

        // Status
        if (!string.IsNullOrEmpty(_status))
        {
            GUILayout.Space(8);
            GUILayout.Label(_status, WrapStyle());
        }

        GUILayout.EndArea();

        // ── Preview area ──
        DrawPreviews(panelW + margin * 2);
    }

    void DrawPreviews(float startX)
    {
        float margin    = 10f;
        float scaledW   = Screen.width;
        float available = scaledW / uiScale - startX - margin * 2;
        float previewW  = available / 2f;
        float previewH  = previewW;
        float y         = margin;

        // Original — LoadImage produces correct sRGB tagging, so display directly.
        if (_selectedTexture < _sourceTextures.Count && _sourceTextures[_selectedTexture] != null)
        {
            var src = _sourceTextures[_selectedTexture];
            GUI.Label(new Rect(startX, y, previewW, 20), $"Original  ({src.width}x{src.height}, {src.graphicsFormat})");
            GUI.DrawTexture(new Rect(startX, y + 22, previewW, previewH), src, ScaleMode.ScaleToFit);
        }

        // Encoded
        if (_encodedTexture != null)
        {
            float ex = startX + previewW + margin;
            GUI.Label(new Rect(ex, y, previewW, 20),
                $"Encoded  ({_encodedTexture.graphicsFormat}, {_encodeTimeMs:F1} ms)");
            GUI.DrawTexture(new Rect(ex, y + 22, previewW, previewH), _encodedTexture, ScaleMode.ScaleToFit);
        }
    }

    void Encode()
    {
        if (_selectedTexture >= _sourceTextures.Count || _sourceTextures[_selectedTexture] == null)
        {
            _status = "No texture selected.";
            return;
        }

        var format = (SparkFormat)_selectedFormat;
        var source = _sourceTextures[_selectedTexture];

        try
        {
            if (_encodedTexture != null)
            {
                Destroy(_encodedTexture);
                _encodedTexture = null;
            }

            var sw = Stopwatch.StartNew();
            _encodedTexture = Spark.EncodeTexture(source, format, _quality, _srgb);
            sw.Stop();
            _encodeTimeMs = (float)sw.Elapsed.TotalMilliseconds;

            var resolved = Spark.ResolveFormat(format);
            string fmtLabel = resolved != format ? $"{format} → {resolved}" : $"{format}";
            _status = $"Encoded {source.width}x{source.height} → {fmtLabel} ({_quality}) in {_encodeTimeMs:F1} ms";
            Debug.Log($"[Spark] {_status}");
        }
        catch (System.Exception e)
        {
            _status = $"Error: {e.Message}";
            Debug.LogException(e);
        }
    }

    // ─── Helpers ───

    /// <summary>
    /// Load a texture from a PNG or JPG byte array.
    /// </summary>
    /// <param name="data">Raw PNG or JPG file bytes.</param>
    /// <param name="linear">True for linear data (normal maps, roughness). False for sRGB color data.</param>
    public static Texture2D LoadTexture(byte[] data, bool linear = false)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        if (!tex.LoadImage(data))
        {
            UnityEngine.Object.Destroy(tex);
            throw new InvalidOperationException("Failed to decode image data.");
        }
        return tex;
    }

    /// <summary>
    /// Load a texture from a file path (PNG or JPG).
    /// </summary>
    public static Texture2D LoadTexture(string filePath, bool linear = false)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Texture file not found.", filePath);

        byte[] data = File.ReadAllBytes(filePath);
        Texture2D tex = LoadTexture(data, linear);
        tex.name = Path.GetFileNameWithoutExtension(filePath);
        return tex;
    }

    void LoadTexturesFromStreamingAssets()
    {
        string dir = Path.Combine(Application.streamingAssetsPath, "SparkTextures");
        if (!Directory.Exists(dir))
            return;

        var files = new List<string>();
        files.AddRange(Directory.GetFiles(dir, "*.png"));
        files.AddRange(Directory.GetFiles(dir, "*.jpg"));
        files.AddRange(Directory.GetFiles(dir, "*.jpeg"));
        files.Sort();

        foreach (string file in files)
        {
            try
            {
                var tex = SparkDemo.LoadTexture(file);
                _sourceTextures.Add(tex);
                Debug.Log($"[SparkDemo] Loaded {tex.name} ({tex.width}x{tex.height})");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SparkDemo] Failed to load {file}: {e.Message}");
            }
        }
    }

    // GUI style helpers
    static GUIStyle s_richStyle;
    static GUIStyle RichStyle()
    {
        if (s_richStyle == null)
        {
            s_richStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
        }
        return s_richStyle;
    }

    static GUIStyle s_richButton;
    static GUIStyle RichButton()
    {
        if (s_richButton == null)
        {
            s_richButton = new GUIStyle("Button") { richText = true, alignment = TextAnchor.MiddleLeft };
        }
        return s_richButton;
    }

    static GUIStyle s_wrapStyle;
    static GUIStyle WrapStyle()
    {
        if (s_wrapStyle == null)
        {
            s_wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        }
        return s_wrapStyle;
    }
}
