using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;


public class SparkDemo : MonoBehaviour
{
    [Header("Modes")]
    [Tooltip("Mode components on child GameObjects. Order = tab order. First entry is active at start.")]
    public SparkDemoMode[] modes;

    [Header("UI")]
    [Range(0.5f, 4f)]
    public float uiScale = 2.0f;

    /// <summary>The scale factor applied to GUI.matrix by the controller. Modes need this
    /// to convert their scaled-coord bounds back to raw screen pixels.</summary>
    public float UiScaleFactor => EffectiveScale;

    /// <summary>Textures loaded from StreamingAssets/SparkTextures, shared by all modes.</summary>
    public List<Texture2D> SourceTextures { get; } = new List<Texture2D>();

    int _activeIndex = 0;

    void Awake()
    {
        // Auto-discover modes if not assigned in the inspector.
        if (modes == null || modes.Length == 0)
            modes = GetComponentsInChildren<SparkDemoMode>(includeInactive: true);

        foreach (var m in modes)
        {
            if (m != null) m.Controller = this;
        }
    }

    void Start()
    {
        // Disable all modes; activate index 0 once textures are ready.
        foreach (var m in modes)
            if (m != null) m.gameObject.SetActive(false);

        StartCoroutine(LoadTexturesThenActivate());

        // Preload formats covering the slideshow's heuristics.
        Spark.Preload(SparkFormat.RGB, SparkFormat.RGBA, SparkFormat.RG, SparkFormat.R);
    }

    IEnumerator LoadTexturesThenActivate()
    {
        yield return LoadTexturesFromStreamingAssets();
        SwitchTo(0);
    }

    void OnDestroy()
    {
        foreach (var tex in SourceTextures)
            if (tex != null) Destroy(tex);
        Spark.ReleaseCache();
    }

    void SwitchTo(int index)
    {
        if (modes == null || modes.Length == 0) return;
        index = Mathf.Clamp(index, 0, modes.Length - 1);

        if (_activeIndex >= 0 && _activeIndex < modes.Length && modes[_activeIndex] != null)
        {
            modes[_activeIndex].Deactivate();
            modes[_activeIndex].gameObject.SetActive(false);
        }
        _activeIndex = index;
        if (modes[_activeIndex] != null)
        {
            modes[_activeIndex].gameObject.SetActive(true);
            modes[_activeIndex].Activate();
        }
    }

    void LateUpdate()
    {
        if (modes == null || _activeIndex >= modes.Length) return;
        var active = modes[_activeIndex];
        if (active != null) active.OnTick();
    }

    float EffectiveScale
    {
        get
        {
            // DPI-based UI scaling, tuned for desktop at the /160f divisor. Mobile devices
            // report a much higher Screen.dpi for the same physical button size we want, so
            // halve the result there to keep the UI from ballooning.
            float s = uiScale * Screen.dpi / 160f;
            if (Application.isMobilePlatform) s *= 0.5f;
            return s;
        }
    }

    void OnGUI()
    {
        float scale = EffectiveScale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        Rect safe = Screen.safeArea;
        float topMargin = Screen.height - (safe.y + safe.height);
        float safeX = safe.x / scale;
        float safeY = topMargin / scale;
        float safeW = safe.width / scale;
        float safeH = safe.height / scale;

        var activeMode = modes[_activeIndex];

        // Render background. Pass the full-screen rect in *scaled* coords (matching the active
        // GUI.matrix), so modes that convert via bounds * UiScaleFactor land on the real screen
        // pixels — not Screen.width * scale, which over-inflates the rect by the scale factor.
        Rect screenRect = new Rect(0f, 0f, Screen.width / scale, Screen.height / scale);
        activeMode.OnGUIBackground(screenRect);

        // Render tab strip.
        const float TabH = 28f;
        Rect tabRect = new Rect(safeX, safeY, safeW, TabH);
        DrawTabs(tabRect);

        // Render foreground (overlay, buttons, input).
        Rect modeArea = new Rect(safeX, safeY + TabH, safeW, safeH - TabH);
        activeMode.OnGUIForeground(modeArea);
    }

    void DrawTabs(Rect bounds)
    {
        if (modes == null || modes.Length == 0) return;

        // Semi-transparent tab strip — texture shows through behind the buttons.
        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.85f);

        float w = bounds.width / modes.Length;
        for (int i = 0; i < modes.Length; i++)
        {
            var m = modes[i];
            var r = new Rect(bounds.x + i * w, bounds.y, w - 2f, bounds.height);
            bool isActive = i == _activeIndex;
            bool clicked = GUI.Toggle(r, isActive, m.DisplayName, "Button");
            if (clicked && !isActive)
                SwitchTo(i);
        }

        GUI.color = prev;
    }

    // ─── Texture loading (shared by all modes) ───

    /// <summary>Load a Texture2D from PNG/JPG bytes with mipChain enabled.</summary>
    public static Texture2D LoadTexture(byte[] data, bool linear = false)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
        if (!tex.LoadImage(data))
        {
            UnityEngine.Object.Destroy(tex);
            throw new InvalidOperationException("Failed to decode image data.");
        }
        return tex;
    }

    public static Texture2D LoadTexture(string filePath, bool linear = false)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Texture file not found.", filePath);

        byte[] data = File.ReadAllBytes(filePath);
        Texture2D tex = LoadTexture(data, linear);
        tex.name = Path.GetFileNameWithoutExtension(filePath);
        return tex;
    }

    IEnumerator LoadTexturesFromStreamingAssets()
    {
        if (Application.platform == RuntimePlatform.Android)
            yield return LoadTexturesFromManifest();
        else
            LoadTexturesFromDirectory();
    }

    void LoadTexturesFromDirectory()
    {
        string dir = Application.streamingAssetsPath;
        if (!Directory.Exists(dir)) return;

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
                SourceTextures.Add(tex);
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
        string basePath = Application.streamingAssetsPath;

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
                        SourceTextures.Add(tex);
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
}
