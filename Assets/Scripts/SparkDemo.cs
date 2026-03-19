using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
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
///   3. Texture is compressed in the GPU whenever settings change.
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
    float     _cpuTimeMs;
    string    _status = "Loading textures...";

    // Dirty flag — set when options change, consumed in LateUpdate.
    // Set to 2 on change so it skips one frame (lets OnGUI
    // run a matching Layout+Repaint cycle before the texture changes).
    int _encodeCountdown = 2;

    // Cached format info
    static readonly string[] FormatNames = System.Enum.GetNames(typeof(SparkFormat));

    void Start()
    {
        StartCoroutine(LoadTexturesFromStreamingAssets());

        // Preload most common formats at all quality levels.
        Spark.Preload(SparkQuality.Low, SparkFormat.RGB, SparkFormat.RGBA, SparkFormat.RG, SparkFormat.R);
        Spark.Preload(SparkQuality.Medium, SparkFormat.RGB, SparkFormat.RGBA, SparkFormat.RG, SparkFormat.R);
        Spark.Preload(SparkQuality.High, SparkFormat.RGB, SparkFormat.RGBA, SparkFormat.RG, SparkFormat.R);
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
    [Range(1f, 5f)]
    public float uiScale = 1.5f;

    float EffectiveScale
    {
        get
        {
            // On mobile, scale so the panel (~340px) is about 1/3 of screen width.
            if (Application.isMobilePlatform)
                return Mathf.Max(uiScale, Screen.width / (340f * 3f));
            return uiScale;
        }
    }

    void OnGUI()
    {
        float scale = EffectiveScale;

        // Scale the entire UI uniformly.
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        // Use safe area to avoid rounded corners and notches.
        Rect safe = Screen.safeArea;
        float safeX = safe.x / scale;
        float safeY = safe.y / scale;
        float safeW = safe.width / scale;
        float safeH = safe.height / scale;

        // Work in scaled coordinates.
        float panelW = 340f;
        float margin = 10f;

        GUILayout.BeginArea(new Rect(safeX + margin, safeY + margin, panelW, safeH - margin * 2));

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
        float previewX = safeX + panelW + margin * 2;
        float previewMaxW = safeW - (previewX - safeX) - margin;
        DrawPreviews(previewX, safeY + margin, previewMaxW);
    }

    void DrawPreviews(float startX, float startY, float available)
    {
        float margin   = 10f;
        float previewW = (available - margin) / 2f;
        float previewH = previewW;

        // Original
        if (_selectedTexture < _sourceTextures.Count && _sourceTextures[_selectedTexture] != null)
        {
            var src = _sourceTextures[_selectedTexture];
            GUI.Label(new Rect(startX, startY, previewW, 20), "Original");
            GUI.DrawTexture(new Rect(startX, startY + 22, previewW, previewH), src, ScaleMode.ScaleToFit);
        }

        // Encoded
        if (_encodedTexture != null)
        {
            float ex = startX + previewW + margin;
            GUI.Label(new Rect(ex, startY, previewW, 20),
                $"Encoded  ({_encodedTexture.graphicsFormat})");
            GUI.DrawTexture(new Rect(ex, startY + 22, previewW, previewH), _encodedTexture, ScaleMode.ScaleToFit);
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
            _cpuTimeMs = (float)sw.Elapsed.TotalMilliseconds;

            _status = $"Encoded {format} ({_quality}) — CPU {_cpuTimeMs:F1} ms, GPU {Spark.GpuTimeMs:F1} ms";
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
    /// Load a texture from a file path (PNG or JPG). Not supported on Android StreamingAssets.
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

    /// <summary>
    /// Load textures from StreamingAssets/SparkTextures/.
    /// On desktop, enumerates the directory directly.
    /// On mobile (Android), uses UnityWebRequest + textures.txt manifest
    /// since StreamingAssets is inside the APK.
    /// </summary>
    IEnumerator LoadTexturesFromStreamingAssets()
    {
        if (Application.isMobilePlatform)
            yield return LoadTexturesFromManifest();
        else
            LoadTexturesFromDirectory();

        if (_sourceTextures.Count == 0)
            _status = "No textures found. Place PNGs in StreamingAssets/SparkTextures/.";
        else
        {
            _status = "";
            _encodeCountdown = 2;
        }
    }

    void LoadTexturesFromDirectory()
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
                var tex = LoadTexture(file);
                _sourceTextures.Add(tex);
                Debug.Log($"[SparkDemo] Loaded {tex.name} ({tex.width}x{tex.height})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SparkDemo] Failed to load {file}: {e.Message}");
            }
        }
    }

    IEnumerator LoadTexturesFromManifest()
    {
        string basePath = Application.streamingAssetsPath + "/SparkTextures";

        using (var req = UnityWebRequest.Get(basePath + "/textures.txt"))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[SparkDemo] Could not load manifest: {req.error}");
                yield break;
            }

            string[] filenames = req.downloadHandler.text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string filename in filenames)
            {
                using (var texReq = UnityWebRequest.Get(basePath + "/" + filename.Trim()))
                {
                    yield return texReq.SendWebRequest();
                    if (texReq.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[SparkDemo] Failed to load {filename}: {texReq.error}");
                        continue;
                    }

                    try
                    {
                        var tex = LoadTexture(texReq.downloadHandler.data);
                        tex.name = Path.GetFileNameWithoutExtension(filename);
                        _sourceTextures.Add(tex);
                        Debug.Log($"[SparkDemo] Loaded {tex.name} ({tex.width}x{tex.height})");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SparkDemo] Failed to decode {filename}: {e.Message}");
                    }
                }
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
